using System.Collections.Immutable;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Scripting.Projection;

namespace MakoKaraGenerator.Scripting.Execution;

internal sealed class FrameSessionFactory
{
    private readonly ImmutableArray<CompiledFrameEffect> _effects;
    private readonly string _fingerprint;
    private readonly Func<ILineGeometryProvider> _geometryFactory;
    private readonly AudioFeatures _audio;
    private readonly ReplayReason? _reason;
    private readonly VisualOutputBudget _visualBudget;
    private readonly ParticleSimulationBudget _particleBudget;
    private readonly PreparedSourceMaterials? _sourceMaterials;
    public FrameSessionFactory(ImmutableArray<CompiledFrameEffect> effects, string fingerprint,
        Func<ILineGeometryProvider> geometryFactory, AudioFeatures? audio, ReplayReason? reason, VisualOutputBudget? visualBudget = null, ParticleSimulationBudget? particleBudget = null, PreparedSourceMaterials? sourceMaterials = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint); ArgumentNullException.ThrowIfNull(geometryFactory);
        if (effects.IsDefault || effects.IsEmpty && sourceMaterials is null || effects.Any(static item => item is null || !item.Id.IsValid || !HasExecutable(item)
            || item.Parameters is null || item.Binding is null || item.Resources is null || item.Subscription is null
            || (item.Capabilities & ~(EffectCapabilities.Transforms | EffectCapabilities.Style | EffectCapabilities.Overlay | EffectCapabilities.ImageEffects)) != 0)
            || effects.Select(static item => item.Id).Distinct().Count() != effects.Length)
            throw new ArgumentException("帧输入须有唯一有效实例与成功的固定控制器产物或真实直接图声明。", nameof(effects));
        _effects = effects; _fingerprint = fingerprint; _geometryFactory = geometryFactory;
        _audio = audio ?? AudioFeatures.NotAnalyzed; _reason = reason;
        _visualBudget = visualBudget ?? VisualOutputBudget.Default;
        _particleBudget = particleBudget ?? ParticleSimulationBudget.Default; _sourceMaterials = sourceMaterials;
    }
    private static bool HasExecutable(CompiledFrameEffect effect) => effect.PackageDeclaration is EffectGraphDefinition graph
        ? effect.Artifact is null && effect.EntryType is null && effect.PackageDefinition is { } package
            && package.Exports.Any(export => ReferenceEquals(export, graph)) && effect.Capabilities == EffectCapabilities.ImageEffects
        : effect.Artifact is { Succeeded: true } && !string.IsNullOrWhiteSpace(effect.EntryType)
            && effect.PackageDeclaration is null or EffectControllerDefinition;
    public ValueTask<IEvaluatedFrameSource> CreateAsync(ProjectSnapshot snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot); cancellationToken.ThrowIfCancellationRequested();
        if (!StringComparer.Ordinal.Equals(snapshot.EffectInputsFingerprint, _fingerprint))
            throw new PreparedFrameException("EffectInputsMismatch", "快照效果输入指纹不匹配固定会话。");
        _sourceMaterials?.ValidateSnapshot(snapshot);
        var timeline = _reason is { } reason ? TimelineProjection.Create(snapshot, reason, _audio, cancellationToken) : null;
        var content = timeline?.Content ?? ContentProjection.Create(snapshot);
        var project = new ProjectContext { Content = content, Scene = timeline?.Scene ?? SceneProjection.Create(snapshot, content),
            CanvasSize = new(snapshot.Composition.CanvasWidth, snapshot.Composition.CanvasHeight), DurationUs = snapshot.Composition.OutputDurationUs, Audio = _audio };
        foreach (var effect in _effects) ResolvedSourceSubscription.ValidateBinding(project.Content, project.Scene, effect.Binding);
        var subscriptions = timeline is null ? ImmutableArray<ResolvedSourceSubscription>.Empty
            : _effects.Select(effect => ResolvedSourceSubscription.Create(timeline, effect.Binding, effect.Subscription)).ToImmutableArray();
        var geometry = _geometryFactory() ?? throw new InvalidOperationException("几何后端工厂返回 null。");
        FrameEffectHost? host = null;
        try
        {
            var query = _reason is not null && _reason != ReplayReason.AnchorQuery
                ? new SemanticAnchorQuery(snapshot, _effects, _fingerprint, _geometryFactory, _audio, _sourceMaterials) : null;
            host = new(snapshot, project, geometry, _effects, subscriptions, _visualBudget, _particleBudget, _sourceMaterials, timeline, query);
            host.Load(timeline is not null, cancellationToken);
            return ValueTask.FromResult<IEvaluatedFrameSource>(new FrameSession(host, timeline, _reason, cancellationToken));
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
