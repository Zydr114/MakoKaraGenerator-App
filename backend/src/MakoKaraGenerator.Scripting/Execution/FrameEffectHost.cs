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
        ParticleSimulationBudget particleBudget, PreparedSourceMaterials? sourceMaterials = null, TimelineProjection? timeline = null,
        SemanticAnchorQuery? anchorQuery = null, CancellationToken cancellationToken = default)
    {
        _snapshot = snapshot; _project = project; _geometry = geometry; _definitions = definitions; _subscriptions = subscriptions;
        _visualBudget = visualBudget; _particles = new(particleBudget); _timeline = timeline; _anchorQuery = anchorQuery;
        _sourceStyles = StaticStyleProjection.SourceStyles(snapshot);
        _sourceMarkStyles = StaticStyleProjection.MarkStyles(snapshot);
        _sourceMaterials = sourceMaterials;
        _baseline = StaticStyleProjection.CreateBaseline(snapshot, sourceMaterials);
        InitializeExitReplicas(timeline, cancellationToken);
    }
    private sealed record ActiveEffect(CompiledFrameEffect Definition, ScriptModule? Module, IKaraokeEffect Instance,
        DeterministicRandom Random, ResolvedSourceSubscription? Subscription, string? TelemetryIdentity, PureEffectRuntime? Pure = null, HostDirectGraphRuntime? HostGraph = null)
    { internal bool IsPureText => Pure is not null || HostGraph is not null; }
    public bool RequiresPureAtLegacyBoundaries { get; private set; }
    public bool CanReuseBoundaryFrame => RequiresPureAtLegacyBoundaries || !_definitions.Any(static definition => definition.IsV2);
    public void Load(bool semantic, CancellationToken cancellationToken, bool preservePure = false)
    {
        var retained = preservePure ? _instances.Where(static active => active.IsPureText)
            .ToDictionary(static active => active.Definition.Id) : [];
        try
        {
            Unload(preservePure); _instances.Clear(); Diagnostics = new(); RequiresPureAtLegacyBoundaries = false;
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
                    if (definition.IsV2)
                    {
                        active = retained.Remove(definition.Id, out var prepared) ? prepared
                            : definition.PackageDeclaration is EffectGraphDefinition ? LoadHostDirectGraph(definition) : LoadPure(definition, cancellationToken);
                        _instances.Add(active);
                        continue;
                    }
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
                    // Legacy consumers of final geometry inherit their existing replay
                    // cost: their boundary inputs must include current pure text output.
                    RequiresPureAtLegacyBoundaries |= HasOverride(instance, nameof(IKaraokeEffect.OnEvent))
                        || HasOverride(instance, nameof(IKaraokeEffect.EvaluateEmitters))
                        || HasOverride(instance, nameof(IKaraokeEffect.AdvanceVisuals))
                        || HasOverride(instance, nameof(IKaraokeEffect.EvaluateVisuals));
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
                        if (error is not (OperationCanceledException or EffectFailureException))
                            throw Failure(definition, "Load", 0, error, Diagnostics.Snapshot());
                    }
                    throw;
                }
            }
        }
        catch
        {
            // A failed legacy rebuild still owns every retained pure module, including
            // definitions not reached yet. Drain them before returning the first error.
            foreach (var active in retained.Values)
                if (!_instances.Contains(active)) _instances.Add(active);
            try { Unload(); } catch { /* 保留原始失败。 */ }
            throw;
        }
    }
    public void Advance(SimulationStep step, TimelineHistory history, CancellationToken cancellationToken)
    {
        foreach (var active in _instances)
        {
            if (active.IsPureText) continue;
            cancellationToken.ThrowIfCancellationRequested();
            var definition = active.Definition;
            InvokeContext(active, "Advance", step.StartUs, Diagnostics, (reporter, queries) => active.Instance.Advance(step, new()
            { Project = _project, Parameters = definition.Parameters, Resources = definition.Resources, Random = active.Random,
                History = history, Binding = definition.Binding, Diagnostics = reporter, GeometryQueries = queries, CancellationToken = cancellationToken }), cancellationToken);
        }
    }
    public PreparedRenderFrame Evaluate(long timeUs, OutputMode outputMode, RenderTarget target, TimelineHistory? history,
        RuntimeDiagnosticBuffer diagnostics, CancellationToken cancellationToken, bool includePure = true,
        bool pureTextOnly = false, V2AnchorQueryBudget? anchorBudget = null)
    {
        SceneFrame scene = _geometry.GetSceneFrame(_snapshot, timeUs);
        if (includePure) scene = ExtendExitReplicas(scene, cancellationToken);
        FrameBindingScope scope = new(scene);
        bool hasExits = scene.Lines.Any(static line => line.ExitReplica is not null);
        var baseline = _snapshot.PresetApplications.Items.IsEmpty && !hasExits ? _baseline
            : StaticStyleProjection.CreateBaseline(_snapshot, _sourceMaterials, scene);
        var loadedStyles = _snapshot.PresetApplications.Items.IsEmpty && !hasExits ? null
            : StaticStyleProjection.LoadedSourceStyles(_snapshot, scene);
        var sourceMarkStyles = _snapshot.PresetApplications.Items.IsEmpty ? _sourceMarkStyles : StaticStyleProjection.MarkStyles(_snapshot, scene);
        var activeBindings = scene.Lines.Where(static line => line.ExitReplica is null).Select(static line => line.BindingId!).ToHashSet(StringComparer.Ordinal);
        var sourceGroupIds = scene.Elements.Where(static node => node.SourceNodeId is null).Select(static node => node.Id).ToHashSet(StringComparer.Ordinal);
        var sourceGroups = scene.GroupGeometries.Where(group => sourceGroupIds.Contains(group.Target.NodeId.Value)).ToImmutableArray();
        var legacyGroups = hasExits ? LegacyGroups(scene, scene.TokenGeometries, scene.MarkGeometries, sourceGroups, activeBindings) : sourceGroups;
        var allTokens = FrameProjection.Tokens(scene);
        Dictionary<TokenFamilyTarget, Affine2> combined = [];
        Dictionary<BaseTokenTarget, Affine2> baseTransforms = [];
        Dictionary<BaseTokenTarget, StyleUpdate> baseStyles = [];
        Dictionary<V2.TextClusterTarget, Affine2> clusterTransforms = [];
        Dictionary<V2.TextTarget, ActiveEffect> partitionOwners = [];
        Dictionary<V2.TextTarget, PreparedTextWarpBinding> textWarps = [];
        TextWarpBudget warpBudget = new();
        GraphBindingBudget graphBudget = new();
        MaterialUniformScopes materialScopes = new();
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
            if (pureTextOnly && !active.IsPureText) continue;
            if (!includePure && active.IsPureText) continue;
            cancellationToken.ThrowIfCancellationRequested();
            anchorBudget?.TextCall();
            var definition = active.Definition;
            var tokens = allTokens.Where(token => (active.IsPureText || activeBindings.Contains(token.BindingId.Value))
                && scope.ContainsToken(definition.Binding, token)).ToImmutableArray();
            var lines = allLines.Where(line => (active.IsPureText || activeBindings.Contains(line.BindingId.Value))
                && scope.ContainsLine(definition.Binding, line)).ToImmutableArray();
            var marks = allMarks.Where(mark => scope.ContainsMark(definition.Binding, mark)).ToImmutableArray();
            var targets = tokens.Select(static token => token.WithRuby).ToHashSet();
            var scopedGeometry = scene.TokenGeometries.Where(item => targets.Contains(item.WithRuby)).ToImmutableArray();
            var scopedMarks = scene.MarkGeometries.Where(mark => marks.Any(item => item.Target == mark.Target)).ToImmutableArray();
            var scopedLines = scene.LineGeometries.Where(line => lines.Any(item => item.Target == line.Target)).ToImmutableArray();
            var writableLines = definition.Binding is EffectBinding.Tokens ? ImmutableArray<LineGeometrySample>.Empty : scopedLines;
            var frameGroups = !active.IsPureText ? legacyGroups : sourceGroups;
            var writableGroups = frameGroups.Where(group => scope.ContainsGroup(definition.Binding, group.Target)).ToImmutableArray();
            FrameWriter writer = new(scopedGeometry, definition.Capabilities, writableGroups, baseline, definition.Resources, scopedMarks, writableLines);
            FrameContext frame = new() { TimeUs = timeUs, Project = _project, Parameters = definition.Parameters,
                Resources = definition.Resources, Random = active.Random, Baseline = baseline,
                Presentations = new() { TokensInBinding = tokens, LinesInBinding = lines, MarksInBinding = marks, Groups = FrameProjection.Groups(scene) },
                Geometry = scopedGeometry, LineGeometry = scopedLines, MarkGeometry = scopedMarks, GroupGeometry = frameGroups, Binding = definition.Binding,
                OutputMode = outputMode, CancellationToken = cancellationToken };
            if (history is not null) frame = frame with { History = history };
            FrameOutput? output = null;
            try { InvokeContext(active, "Evaluate", timeUs, diagnostics, (reporter, queries) =>
                {
                    using (MeasureAuthor(active, "Evaluate"))
                    {
                        if (active.HostGraph is not null) output = EvaluateHostDirectGraph(active, frame);
                        else if (active.Pure is not null) output = EvaluatePure(active, frame, scene, writableGroups, cancellationToken);
                        else
                        {
                            active.Instance.Evaluate(frame with { Diagnostics = reporter, GeometryQueries = queries }, writer);
                            output = writer.Complete();
                        }
                    }
                }, cancellationToken); }
            finally { writer.Invalidate(); }
            Invoke(active, "ComposeOutputs", timeUs, diagnostics, ignoredReporter =>
            {
                ValidatePureGraphWrites(definition, output!, graphBudget, cancellationToken);
                materialScopes.Observe(active, output!, graphBudget, cancellationToken);
                ValidatePackageOutput(definition, output!, cancellationToken);
                ComposeWarps(active, output!, warpBudget, textWarps);
                foreach (var transform in output!.BaseTransforms)
                {
                    var matrix = transform.LocalMatrix.Compose(baseTransforms.GetValueOrDefault(transform.Target, Affine2.Identity));
                    if (!matrix.TryInverse(out _)) throw new EffectOutputException("InvalidTransform", "跨实例基础字变换不可逆。");
                    baseTransforms[transform.Target] = matrix;
                    partitionOwners[new V2.TextTarget.Base(transform.Target)] = active;
                }
                foreach (var style in output.BaseStyles)
                {
                    baseStyles[style.Target] = baseStyles.TryGetValue(style.Target, out var previousBase) ? previousBase.Then(style.Update) : style.Update;
                    partitionOwners[new V2.TextTarget.Base(style.Target)] = active;
                }
                foreach (var transform in output.ClusterTransforms)
                {
                    var matrix = transform.LocalMatrix.Compose(clusterTransforms.GetValueOrDefault(transform.Target, Affine2.Identity));
                    if (!matrix.TryInverse(out _)) throw new EffectOutputException("InvalidTransform", "跨实例字簇变换不可逆。");
                    clusterTransforms[transform.Target] = matrix;
                    partitionOwners[new V2.TextTarget.Cluster(transform.Target)] = active;
                }
                foreach (var style in output!.TokenStyles)
                {
                    styles[style.Target] = styles.TryGetValue(style.Target, out var previous) ? previous.Then(style.Update) : style.Update;
                    styleOwners[style.Target] = active;
                    TrackMaterials(new ImageEffectTarget.Token(style.Target), style.Update,
                        styles[style.Target].ApplyMaterials(baseline.Get(style.Target)), active, materials);
                }
                foreach (var transform in output.TokenTransforms)
                {
                    var local = ComposeFor(active, combined.GetValueOrDefault(transform.Target, Affine2.Identity), transform.LocalMatrix);
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
                    var local = ComposeFor(active, markTransforms.GetValueOrDefault(transform.Target, Affine2.Identity), transform.LocalMatrix);
                    if (!local.TryInverse(out _)) throw new EffectOutputException("InvalidTransform", "跨实例标记变换组合不可逆。");
                    var basis = scene.MarkGeometries.Single(item => item.Target == transform.Target).Geometry;
                    _ = (basis with { BaseWorldMatrix = basis.BaseWorldMatrix.Compose(local) }).WorldInkCorners;
                    markTransforms[transform.Target] = local;
                }
                ComposeLines(output.LineTransforms, lineTransforms, scene, active.Pure is not null);
                ComposeGroups(output.GroupTransforms, groupTransforms, scene, active.Pure is not null);
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
        materialScopes.Complete(styles, markStyles, baseline, styleOwners, markOwners, materials, timeUs, diagnostics, cancellationToken);
        // 只在所有实例合并后校验最终 alpha，允许先乘大于 1 再缩小。
        foreach (var pair in styles.Where(pair => styleOwners.ContainsKey(pair.Key)))
            Invoke(styleOwners[pair.Key], "ComposeOutputs", timeUs, diagnostics, ignoredReporter => StaticStyleProjection.ApplyDrawingStyle(
                loadedStyles is null ? _sourceStyles[pair.Key.TokenId] : loadedStyles[pair.Key], pair.Value, _sourceMaterials));
        foreach (var pair in markStyles.Where(pair => markOwners.ContainsKey(pair.Key)))
            Invoke(markOwners[pair.Key], "ComposeOutputs", timeUs, diagnostics, ignoredReporter => StaticStyleProjection.ApplyDrawingStyle(sourceMarkStyles[pair.Key.NodeId], pair.Value, _sourceMaterials));
        foreach (var pair in groupOpacities)
            Invoke(groupOpacityOwners[pair.Key], "ComposeOutputs", timeUs, diagnostics, ignoredReporter =>
            {
                if (pair.Value is < 0 or > 1) throw new EffectOutputException("InvalidStyleOpacity", "最终组透明度必须在 [0,1]，不夹值。");
            });
        var prepared = PreparedRenderFrame.CreateWithDiagnosticSnapshot(_snapshot, timeUs, outputMode, target, scene,
            [.. combined.Where(static pair => pair.Value != Affine2.Identity).Select(static pair => new TokenFamilyTransform(pair.Key, pair.Value))],
            [.. styles.Select(static pair => new TokenFamilyStyle(pair.Key, pair.Value))], diagnostics.Snapshot(),
            [.. markTransforms.Where(static pair => pair.Value != Affine2.Identity).Select(static pair => new TextMarkTransform(pair.Key, pair.Value))],
            [.. markStyles.Select(static pair => new TextMarkStyle(pair.Key, pair.Value))],
            [.. lineTransforms.Where(static pair => pair.Value != Affine2.Identity).Select(static pair => new LineTransform(pair.Key, pair.Value))],
            [.. groupTransforms.Where(static pair => pair.Value != Affine2.Identity).Select(static pair => new GroupTransform(pair.Key, pair.Value))], [.. materials.Values], _sourceMaterials)
            .WithGroupOpacities([.. groupOpacities.Select(pair => new PreparedGroupOpacity(groupOpacityOwners[pair.Key].Definition.Id,
                groupOpacityOwners[pair.Key].Definition.RuntimeHash, new(pair.Key, pair.Value))
                { PackageIdentity = groupOpacityOwners[pair.Key].Definition.PackageIdentity })])
            .WithImageEffects([.. imageEffects]);
        try
        {
            return prepared.WithTextPartitions([.. baseTransforms.Where(static pair => pair.Value != Affine2.Identity).Select(static pair => new V2.BaseTokenTransform(pair.Key, pair.Value))],
                [.. baseStyles.Select(static pair => new V2.BaseTokenStyle(pair.Key, pair.Value))],
                [.. clusterTransforms.Where(static pair => pair.Value != Affine2.Identity).Select(static pair => new V2.TextClusterTransform(pair.Key, pair.Value))])
                .WithTextWarps([.. textWarps.Values]);
        }
        catch (PreparedFrameException error) when (error.TextTarget is { } warpTarget && textWarps.ContainsKey(warpTarget))
        {
            var owner = textWarps[warpTarget];
            var active = _instances.Single(item => item.Definition.Id == owner.InstanceId);
            throw Failure(active.Definition, "ComposeOutputs", timeUs, error, diagnostics.Snapshot());
        }
        catch (PreparedFrameException error) when (error.TextTarget is { } partition && partitionOwners.ContainsKey(partition))
        {
            throw Failure(partitionOwners[error.TextTarget!].Definition, "ComposeOutputs", timeUs, error, diagnostics.Snapshot());
        }
    }
    public void Unload(bool preservePure = false)
    {
        if (!preservePure)
        {
            _v2AnchorMemo.Clear(); _v2PrepareBudget = new(); _v2CursorCuts.Clear(); _v2CursorCutCount = 0; _v2AnchorBreakpoints = default;
        }
        _pendingBursts.Clear(); _particles.Clear(); _bridgeEndpoints.Clear(); var owned = _instances.ToArray(); _instances.Clear(); List<Exception> failures = [];
        foreach (var active in owned)
        {
            if (preservePure && active.IsPureText) { _instances.Add(active); continue; }
            active.Pure?.Source.Indexes.Invalidate();
            try { if (active.Module is { } module) module.Dispose(); else active.Instance?.Dispose(); }
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
