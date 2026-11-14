using System.Collections.Immutable;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Rendering.Abstractions;
using MakoKaraGenerator.Scripting.Projection;

namespace MakoKaraGenerator.Scripting.Execution;

internal sealed class FrameSessionFactory
{
    private readonly ImmutableArray<CompiledFrameEffect> _effects;
    private readonly string _fingerprint;
    private readonly Func<ILineGeometryProvider> _geometryFactory;
    private readonly AudioFeatures _audio;
    private readonly VisualOutputBudget _visualBudget;
    private readonly PreparedSourceMaterials? _sourceMaterials;
    private readonly V2RuntimeBudgetOptions _v2Budgets;
    private readonly ProjectMediaFeatureBinding? _mediaBinding;
    public FrameSessionFactory(ImmutableArray<CompiledFrameEffect> effects, string fingerprint,
        Func<ILineGeometryProvider> geometryFactory, AudioFeatures? audio, VisualOutputBudget? visualBudget = null,
        PreparedSourceMaterials? sourceMaterials = null, V2RuntimeBudgetOptions? v2Budgets = null, ProjectMediaFeatureBinding? mediaBinding = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint); ArgumentNullException.ThrowIfNull(geometryFactory);
        if (effects.IsDefault || effects.IsEmpty && sourceMaterials is null || effects.Any(static item => item is null || !item.Id.IsValid || !HasExecutable(item)
            || item.Parameters is null || item.Binding is null || item.Resources is null || item.Subscription is null
            || (item.Capabilities & ~(EffectCapabilities.Transforms | EffectCapabilities.Style | EffectCapabilities.Overlay | EffectCapabilities.ImageEffects)) != 0)
            || effects.Select(static item => item.Id).Distinct().Count() != effects.Length)
            throw new ArgumentException("帧输入须有唯一有效实例与成功的固定控制器产物或真实直接图声明。", nameof(effects));
        _effects = effects; _fingerprint = fingerprint; _geometryFactory = geometryFactory;
        _audio = audio ?? AudioFeatures.NotAnalyzed;
        _visualBudget = visualBudget ?? VisualOutputBudget.Default;
        _sourceMaterials = sourceMaterials;
        _mediaBinding = mediaBinding;
        _v2Budgets = v2Budgets ?? V2RuntimeBudgetOptions.Default;
    }
    private static bool HasExecutable(CompiledFrameEffect effect) => effect.PackageDeclaration is EffectGraphDefinition graph
        ? effect.Artifact is null && effect.EntryType is null && effect.PackageDefinition is { } package
            && package.Exports.Any(export => ReferenceEquals(export, graph)) && effect.Capabilities == EffectCapabilities.ImageEffects
        : effect.Artifact is { Succeeded: true } && !string.IsNullOrWhiteSpace(effect.EntryType)
            && effect.PackageDeclaration is null or EffectControllerDefinition;
    public ValueTask<IEvaluatedFrameSource> CreateAsync(ProjectSnapshot snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot); cancellationToken.ThrowIfCancellationRequested();
        if (!StringComparer.Ordinal.Equals(ProjectRuntimeEffectInputs.Fingerprint(snapshot), _fingerprint))
            throw new PreparedFrameException("EffectInputsMismatch", "快照效果输入指纹不匹配固定会话。");
        ProjectMediaFeatureAdmission.Validate(snapshot, _effects, _sourceMaterials, _mediaBinding, cancellationToken);
        _sourceMaterials?.ValidateSnapshot(snapshot);
        var timeline = TimelineProjection.Create(snapshot, ReplayReason.Initial, _audio, cancellationToken);
        var content = timeline.Content;
        var project = new HostProjectInputs(content, timeline.Scene,
            new(snapshot.Composition.CanvasWidth, snapshot.Composition.CanvasHeight), snapshot.Composition.OutputDurationUs, _audio);
        foreach (var effect in _effects) ResolvedSourceSubscription.ValidateBinding(project.Content, project.Scene, effect.Binding);
        var subscriptions = _effects.Select(effect => ResolvedSourceSubscription.Create(timeline, effect.Binding, effect.Subscription)).ToImmutableArray();
        ILineGeometryProvider geometry = _geometryFactory() ?? throw new InvalidOperationException("几何后端工厂返回 null。");
        if (!_effects.IsEmpty) geometry = new FixedInputGeometryProvider(snapshot, geometry);
        FrameEffectHost? host = null;
        try
        {
            host = new(snapshot, project, geometry, _effects, subscriptions, _visualBudget, _sourceMaterials, timeline, _mediaBinding, cancellationToken)
            { V2Budgets = _v2Budgets };
            host.Load(cancellationToken);
            return ValueTask.FromResult<IEvaluatedFrameSource>(new FrameSession(host));
        }
        catch
        {
            try
            {
                if (host is not null) host.Dispose(); else (geometry as IDisposable)?.Dispose();
            }
            catch { /* 不覆盖原始初始化失败。 */ }
            throw;
        }
    }
}
