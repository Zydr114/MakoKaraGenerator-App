using System.Collections.Immutable;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Scripting.Compilation;
using MakoKaraGenerator.Scripting.Loading;
using MakoKaraGenerator.Scripting.Projection;

namespace MakoKaraGenerator.Scripting.Execution;

internal sealed partial class FrameEffectHost : IDisposable
{
    private readonly ProjectSnapshot _snapshot;
    private readonly ProjectContext _project;
    private readonly ILineGeometryProvider _geometry;
    private readonly ImmutableArray<CompiledFrameEffect> _definitions;
    private readonly ImmutableArray<ResolvedSourceSubscription> _subscriptions;
    private readonly ImmutableDictionary<TokenId, Core.Projects.LyricStyle> _sourceStyles;
    private readonly ImmutableDictionary<NodeId, Core.Projects.LyricStyle> _sourceMarkStyles;
    private readonly BaselineStyles _baseline;
    private readonly PreparedSourceMaterials? _sourceMaterials;
    private readonly VisualOutputBudget _visualBudget;
    private readonly List<ActiveEffect> _instances = [];
    public RuntimeDiagnosticBuffer Diagnostics { get; private set; } = new();
    public ContentCatalog Content => _project.Content;
    public FrameEffectHost(ProjectSnapshot snapshot, ProjectContext project, ILineGeometryProvider geometry,
        ImmutableArray<CompiledFrameEffect> definitions, ImmutableArray<ResolvedSourceSubscription> subscriptions, VisualOutputBudget visualBudget,
        ParticleSimulationBudget particleBudget, PreparedSourceMaterials? sourceMaterials = null, TimelineProjection? timeline = null, SemanticAnchorQuery? anchorQuery = null)
    {
        _snapshot = snapshot; _project = project; _geometry = geometry; _definitions = definitions; _subscriptions = subscriptions;
        _visualBudget = visualBudget; _particles = new(particleBudget); _timeline = timeline; _anchorQuery = anchorQuery;
        _sourceStyles = StaticStyleProjection.SourceStyles(snapshot);
        _sourceMarkStyles = StaticStyleProjection.MarkStyles(snapshot);
        _sourceMaterials = sourceMaterials;
        _baseline = StaticStyleProjection.CreateBaseline(snapshot, sourceMaterials);
    }
    private sealed record ActiveEffect(CompiledFrameEffect Definition, ScriptModule? Module, IKaraokeEffect Instance,
        DeterministicRandom Random, ResolvedSourceSubscription? Subscription, string? TelemetryIdentity);
    public void Load(bool semantic, CancellationToken cancellationToken)
    {
        Unload(); Diagnostics = new();
        try
        {
            for (int index = 0; index < _definitions.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var definition = _definitions[index];
                RequireAudio(definition, 0);
                try
                {
                    definition.Parameters.ValidateResources(definition.Resources);
                    if (definition.PackageDeclaration is EffectGraphDefinition graph)
                    {
                        var package = definition.PackageDefinition!;
                        DirectGraphEffectValidation.Validate(package, graph, definition.Binding);
                        var handle = definition.Resources.GetGraph(graph.Id);
                        if (handle.Identity.PackageId != package.Contents.PackageId
                            || handle.Identity.Revision != package.Contents.Revision)
                            throw new EffectOutputException("InvalidGraphResource", "直接图句柄不属于本实例固定包修订。");
                        PackageSdkParameters.Validate(package, graph, definition.Parameters, definition.Resources);
                    }
                }
                catch (EffectOutputException error) { throw Failure(definition, "ValidateInputs", 0, error, Diagnostics.Snapshot()); }
                catch (EffectPackageException error) { throw Failure(definition, "ValidateInputs", 0, new EffectOutputException(error.Code, error.Message), Diagnostics.Snapshot()); }
                catch (KeyNotFoundException error) { throw Failure(definition, "ValidateInputs", 0, new EffectOutputException("InvalidGraphResource", error.Message), Diagnostics.Snapshot()); }
                ScriptModule? module = null;
                ActiveEffect? active = null;
                try
                {
                    IKaraokeEffect instance;
                    if (definition.PackageDeclaration is EffectGraphDefinition graph) instance = new DirectGraphEffect(graph);
                    else
                    {
                        var artifact = definition.Artifact!;
                        module = new ScriptModule(artifact.PeImage.AsMemory(), artifact.PdbImage.AsMemory());
                        instance = module.CreateInstance<IKaraokeEffect>(definition.EntryType!);
                    }
                    string? telemetryIdentity = module is not null && PerformanceTelemetry.IsEnabled
                        ? PerformanceTelemetry.CreateScriptIdentity($"{definition.RuntimeHash}|{definition.Id}|{definition.PackageIdentity}") : null;
                    active = new(definition, module, instance, new(definition.Seed, definition.Id), semantic ? _subscriptions[index] : null, telemetryIdentity);
                    _instances.Add(active);
                    if (!semantic)
                    {
                        var map = instance.GetType().GetInterfaceMap(typeof(IKaraokeEffect));
                        for (int method = 0; method < map.InterfaceMethods.Length; method++)
                            if (map.InterfaceMethods[method].Name is nameof(IKaraokeEffect.OnEvent) or nameof(IKaraokeEffect.Advance) or nameof(IKaraokeEffect.AdvanceVisuals)
                                && map.TargetMethods[method].DeclaringType != typeof(EffectBase))
                                throw Failure(definition, "Load", 0, new EffectOutputException("TimelineSessionRequired", "有状态作者入口需要语义会话。"), Diagnostics.Snapshot());
                    }
                    InvokeContext(active, "Initialize", 0, Diagnostics, (reporter, queries) => instance.Initialize(new()
                        { Project = _project, Parameters = definition.Parameters, Resources = definition.Resources,
                            Random = active.Random, Diagnostics = reporter, GeometryQueries = queries, CancellationToken = cancellationToken }), cancellationToken);
                }
                catch (Exception error)
                {
                    if (active is null)
                    {
                        try { module?.Dispose(); } catch { /* 保留原始入口失败。 */ }
                        if (error is not OperationCanceledException) throw Failure(definition, "Load", 0, error, Diagnostics.Snapshot());
                    }
                    throw;
                }
            }
        }
        catch { try { Unload(); } catch { /* 保留原始失败。 */ } throw; }
    }
    public void Advance(SimulationStep step, TimelineHistory history, CancellationToken cancellationToken)
    {
        foreach (var active in _instances)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var definition = active.Definition;
            InvokeContext(active, "Advance", step.StartUs, Diagnostics, (reporter, queries) => active.Instance.Advance(step, new()
            { Project = _project, Parameters = definition.Parameters, Resources = definition.Resources, Random = active.Random,
                History = history, Binding = definition.Binding, Diagnostics = reporter, GeometryQueries = queries, CancellationToken = cancellationToken }), cancellationToken);
        }
    }
    public PreparedRenderFrame Evaluate(long timeUs, OutputMode outputMode, RenderTarget target, TimelineHistory? history,
        RuntimeDiagnosticBuffer diagnostics, CancellationToken cancellationToken)
    {
        SceneFrame scene = _geometry.GetSceneFrame(_snapshot, timeUs);
        FrameBindingScope scope = new(scene);
        var baseline = _snapshot.PresetApplications.Items.IsEmpty ? _baseline
            : StaticStyleProjection.CreateBaseline(_snapshot, _sourceMaterials, scene);
        var sourceStyles = _snapshot.PresetApplications.Items.IsEmpty ? _sourceStyles : StaticStyleProjection.SourceStyles(_snapshot, scene);
        var sourceMarkStyles = _snapshot.PresetApplications.Items.IsEmpty ? _sourceMarkStyles : StaticStyleProjection.MarkStyles(_snapshot, scene);
        var allTokens = FrameProjection.Tokens(scene);
        Dictionary<TokenFamilyTarget, Affine2> combined = [];
        Dictionary<TokenFamilyTarget, StyleUpdate> styles = [];
        Dictionary<TokenFamilyTarget, ActiveEffect> styleOwners = [];
        Dictionary<TextMarkTarget, Affine2> markTransforms = [];
        Dictionary<TextMarkTarget, StyleUpdate> markStyles = [];
        Dictionary<TextMarkTarget, ActiveEffect> markOwners = [];
        Dictionary<LineTarget, Affine2> lineTransforms = [];
        Dictionary<GroupTarget, Affine2> groupTransforms = [];
        Dictionary<GroupTarget, double> groupOpacities = [];
        Dictionary<GroupTarget, ActiveEffect> groupOpacityOwners = [];
        var allLines = FrameProjection.Lines(scene, _project.Scene);
        var allMarks = FrameProjection.Marks(scene, _project.Scene);
        List<PreparedImageEffectBinding> imageEffects = [];
        var sourceBindings = _sourceMaterials?.Bindings(scene) ?? [];
        Dictionary<(ImageEffectTarget, PreparedMaterialSlot), PreparedMaterialBinding> materials = sourceBindings.ToDictionary(binding => (binding.Target, binding.Slot));
        foreach (var binding in sourceBindings)
            if (binding.Target is ImageEffectTarget.Token token) styles.TryAdd(token.Target, new());
            else if (binding.Target is ImageEffectTarget.Mark mark) markStyles.TryAdd(mark.Target, new());
        foreach (var active in _instances)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var definition = active.Definition;
            var tokens = allTokens.Where(token => scope.ContainsToken(definition.Binding, token)).ToImmutableArray();
            var lines = allLines.Where(line => scope.ContainsLine(definition.Binding, line)).ToImmutableArray();
            var marks = allMarks.Where(mark => scope.ContainsMark(definition.Binding, mark)).ToImmutableArray();
            var targets = tokens.Select(static token => token.WithRuby).ToHashSet();
            var scopedGeometry = scene.TokenGeometries.Where(item => targets.Contains(item.WithRuby)).ToImmutableArray();
            var scopedMarks = scene.MarkGeometries.Where(mark => marks.Any(item => item.Target == mark.Target)).ToImmutableArray();
            var scopedLines = scene.LineGeometries.Where(line => lines.Any(item => item.Target == line.Target)).ToImmutableArray();
            var writableLines = definition.Binding is EffectBinding.Tokens ? ImmutableArray<LineGeometrySample>.Empty : scopedLines;
            var writableGroups = scene.GroupGeometries.Where(group => scope.ContainsGroup(definition.Binding, group.Target)).ToImmutableArray();
            FrameWriter writer = new(scopedGeometry, definition.Capabilities, writableGroups, baseline, definition.Resources, scopedMarks, writableLines);
            FrameContext frame = new() { TimeUs = timeUs, Project = _project, Parameters = definition.Parameters,
                Resources = definition.Resources, Random = active.Random, Baseline = baseline,
                Presentations = new() { TokensInBinding = tokens, LinesInBinding = lines, MarksInBinding = marks, Groups = FrameProjection.Groups(scene) },
                Geometry = scopedGeometry, LineGeometry = scopedLines, MarkGeometry = scopedMarks, GroupGeometry = scene.GroupGeometries, Binding = definition.Binding,
                OutputMode = outputMode, CancellationToken = cancellationToken };
            if (history is not null) frame = frame with { History = history };
            FrameOutput? output = null;
            try { InvokeContext(active, "Evaluate", timeUs, diagnostics, (reporter, queries) =>
                {
                    using (MeasureAuthor(active, "Evaluate"))
                        active.Instance.Evaluate(frame with { Diagnostics = reporter, GeometryQueries = queries }, writer);
                    output = writer.Complete();
                }, cancellationToken); }
            finally { writer.Invalidate(); }
            Invoke(active, "ComposeOutputs", timeUs, diagnostics, ignoredReporter =>
            {
                ValidatePackageOutput(definition, output!);
                foreach (var style in output!.TokenStyles)
                {
                    styles[style.Target] = styles.TryGetValue(style.Target, out var previous) ? previous.Then(style.Update) : style.Update;
                    styleOwners[style.Target] = active;
                    TrackMaterials(new ImageEffectTarget.Token(style.Target), style.Update,
                        styles[style.Target].ApplyMaterials(baseline.Get(style.Target)), active, materials);
                }
                foreach (var transform in output.TokenTransforms)
                {
                    var local = combined.GetValueOrDefault(transform.Target, Affine2.Identity).Compose(transform.LocalMatrix);
                    if (!local.TryInverse(out _)) throw new EffectOutputException("InvalidTransform", "跨实例变换组合不可逆。");
                    var basis = scene.TokenGeometries.Single(item => item.WithRuby == transform.Target).FamilyGeometry;
                    _ = (basis with { BaseWorldMatrix = basis.BaseWorldMatrix.Compose(local) }).WorldInkCorners;
                    combined[transform.Target] = local;
                }
                foreach (var style in output.MarkStyles)
                {
                    markStyles[style.Target] = markStyles.TryGetValue(style.Target, out var previous) ? previous.Then(style.Update) : style.Update;
                    markOwners[style.Target] = active;
                    TrackMaterials(new ImageEffectTarget.Mark(style.Target), style.Update,
                        markStyles[style.Target].ApplyMaterials(baseline.Get(style.Target)), active, materials);
                }
                foreach (var transform in output.MarkTransforms)
                {
                    var local = markTransforms.GetValueOrDefault(transform.Target, Affine2.Identity).Compose(transform.LocalMatrix);
                    if (!local.TryInverse(out _)) throw new EffectOutputException("InvalidTransform", "跨实例标记变换组合不可逆。");
                    var basis = scene.MarkGeometries.Single(item => item.Target == transform.Target).Geometry;
                    _ = (basis with { BaseWorldMatrix = basis.BaseWorldMatrix.Compose(local) }).WorldInkCorners;
                    markTransforms[transform.Target] = local;
                }
                ComposeLines(output.LineTransforms, lineTransforms, scene);
                ComposeGroups(output.GroupTransforms, groupTransforms, scene);
                foreach (var opacity in output.GroupOpacities)
                {
                    double factor = groupOpacities.GetValueOrDefault(opacity.Target, 1) * opacity.Factor;
                    if (!double.IsFinite(factor)) throw new EffectOutputException("InvalidStyleOpacity", "跨实例组透明度组合溢出。");
                    groupOpacities[opacity.Target] = factor; groupOpacityOwners[opacity.Target] = active;
                }
                if (groupTransforms.Count != 0)
                    _ = SceneAffineProjection.Apply(scene, [.. lineTransforms.Select(static pair => new LineTransform(pair.Key, pair.Value))],
                        [.. groupTransforms.Select(static pair => new GroupTransform(pair.Key, pair.Value))]);
                imageEffects.AddRange(output.ImageEffects.Select(binding => new PreparedImageEffectBinding(definition.Id, definition.RuntimeHash, binding)
                { PackageIdentity = definition.PackageIdentity }));
            });
        }
        // 只在所有实例合并后校验最终 alpha，允许先乘大于 1 再缩小。
        foreach (var pair in styles.Where(pair => styleOwners.ContainsKey(pair.Key)))
            Invoke(styleOwners[pair.Key], "ComposeOutputs", timeUs, diagnostics, ignoredReporter => StaticStyleProjection.ApplyDrawingStyle(sourceStyles[pair.Key.TokenId], pair.Value, _sourceMaterials));
        foreach (var pair in markStyles.Where(pair => markOwners.ContainsKey(pair.Key)))
            Invoke(markOwners[pair.Key], "ComposeOutputs", timeUs, diagnostics, ignoredReporter => StaticStyleProjection.ApplyDrawingStyle(sourceMarkStyles[pair.Key.NodeId], pair.Value, _sourceMaterials));
        foreach (var pair in groupOpacities)
            Invoke(groupOpacityOwners[pair.Key], "ComposeOutputs", timeUs, diagnostics, ignoredReporter =>
            {
                if (pair.Value is < 0 or > 1) throw new EffectOutputException("InvalidStyleOpacity", "最终组透明度必须在 [0,1]，不夹值。");
            });
        return PreparedRenderFrame.CreateWithDiagnosticSnapshot(_snapshot, timeUs, outputMode, target, scene,
            [.. combined.Select(static pair => new TokenFamilyTransform(pair.Key, pair.Value))],
            [.. styles.Select(static pair => new TokenFamilyStyle(pair.Key, pair.Value))], diagnostics.Snapshot(),
            [.. markTransforms.Where(static pair => pair.Value != Affine2.Identity).Select(static pair => new TextMarkTransform(pair.Key, pair.Value))],
            [.. markStyles.Select(static pair => new TextMarkStyle(pair.Key, pair.Value))],
            [.. lineTransforms.Where(static pair => pair.Value != Affine2.Identity).Select(static pair => new LineTransform(pair.Key, pair.Value))],
            [.. groupTransforms.Where(static pair => pair.Value != Affine2.Identity).Select(static pair => new GroupTransform(pair.Key, pair.Value))], [.. materials.Values], _sourceMaterials)
            .WithGroupOpacities([.. groupOpacities.Select(pair => new PreparedGroupOpacity(groupOpacityOwners[pair.Key].Definition.Id,
                groupOpacityOwners[pair.Key].Definition.RuntimeHash, new(pair.Key, pair.Value))
                { PackageIdentity = groupOpacityOwners[pair.Key].Definition.PackageIdentity })])
            .WithImageEffects([.. imageEffects]);
    }
    public void Unload()
    {
        _pendingBursts.Clear(); _particles.Clear(); _bridgeEndpoints.Clear(); var owned = _instances.ToArray(); _instances.Clear(); List<Exception> failures = [];
        foreach (var active in owned)
        {
            try { if (active.Module is { } module) module.Dispose(); else active.Instance.Dispose(); }
            catch (Exception error) { failures.Add(Failure(active.Definition, "Dispose", 0, error, Diagnostics.Snapshot())); }
        }
        if (failures.Count > 0)
        {
            var first = (EffectFailureException)failures[0];
            throw new EffectFailureException(first.InstanceId, first.ArtifactHash, "Dispose", 0, "ScriptDisposeFailed",
                new AggregateException("会话实例释放失败。", failures), first.PackageIdentity) { Diagnostic = first.Diagnostic, DiagnosticSnapshot = first.DiagnosticSnapshot ?? Diagnostics.Snapshot() };
        }
    }
    public void Dispose()
    {
        List<Exception> failures = [];
        try { Unload(); } catch (Exception error) { failures.Add(error); }
        try { (_geometry as IDisposable)?.Dispose(); } catch (Exception error) { failures.Add(error); }
        if (failures.Count == 1) throw failures[0];
        if (failures.Count > 1)
        {
            var aggregate = new AggregateException("会话释放失败。", failures);
            if (failures[0] is EffectFailureException first)
                throw new EffectFailureException(first.InstanceId, first.ArtifactHash, first.Stage, first.TimeUs, first.Code, aggregate, first.PackageIdentity)
                    { Diagnostic = first.Diagnostic };
            throw aggregate;
        }
    }
}
