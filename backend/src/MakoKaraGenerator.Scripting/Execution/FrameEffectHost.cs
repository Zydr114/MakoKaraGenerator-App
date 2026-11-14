using System.Collections.Immutable;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Rendering.Abstractions;
using MakoKaraGenerator.Scripting.Compilation;
using MakoKaraGenerator.Scripting.Loading;
using MakoKaraGenerator.Scripting.Projection;

namespace MakoKaraGenerator.Scripting.Execution;

internal sealed partial class FrameEffectHost : IDisposable
{
    private readonly ProjectSnapshot _snapshot;
    private readonly ProjectMediaFeatureBinding? _mediaBinding;
    private readonly string? _runtimeInputsFingerprint;
    private readonly ProjectContext _project;
    private readonly ILineGeometryProvider _geometry;
    private readonly ImmutableArray<CompiledFrameEffect> _definitions;
    private readonly ImmutableArray<ResolvedSourceSubscription> _subscriptions;
    private readonly ImmutableDictionary<TokenId, Core.Projects.LyricStyle> _sourceStyles;
    private readonly ImmutableDictionary<NodeId, Core.Projects.LyricStyle> _sourceMarkStyles;
    private readonly BaselineStyles _baseline;
    private readonly PreparedSourceMaterials? _sourceMaterials;
    private readonly TimelineProjection? _timeline;
    private readonly VisualOutputBudget _visualBudget;
    private readonly List<ActiveEffect> _instances = [];
    public RuntimeDiagnosticBuffer Diagnostics { get; private set; } = new();
    public ContentCatalog Content => _project.Content;
    public FrameEffectHost(ProjectSnapshot snapshot, ProjectContext project, ILineGeometryProvider geometry,
        ImmutableArray<CompiledFrameEffect> definitions, ImmutableArray<ResolvedSourceSubscription> subscriptions, VisualOutputBudget visualBudget,
        PreparedSourceMaterials? sourceMaterials = null, TimelineProjection? timeline = null,
        ProjectMediaFeatureBinding? mediaBinding = null, CancellationToken cancellationToken = default)
    {
        ProjectMediaFeatureAdmission.Validate(snapshot, definitions, sourceMaterials, mediaBinding, cancellationToken);
        _mediaBinding = mediaBinding; _runtimeInputsFingerprint = ProjectRuntimeEffectInputs.Fingerprint(snapshot);
        _snapshot = snapshot; _project = project; _geometry = geometry; _definitions = definitions; _subscriptions = subscriptions;
        _visualBudget = visualBudget; _timeline = timeline;
        _sourceStyles = StaticStyleProjection.SourceStyles(snapshot);
        _sourceMarkStyles = StaticStyleProjection.MarkStyles(snapshot);
        _sourceMaterials = sourceMaterials;
        _baseline = StaticStyleProjection.CreateBaseline(snapshot, sourceMaterials);
        InitializeExitReplicas(timeline, cancellationToken);
    }
    internal void ValidateMediaOutput(OutputMode outputMode) => ProjectMediaFeatureAdmission.ValidateOutput(_mediaBinding, outputMode);
    private sealed record ActiveEffect(CompiledFrameEffect Definition, ScriptModule? Module,
        DeterministicRandom Random, ResolvedSourceSubscription? Subscription, string? TelemetryIdentity, PureEffectRuntime? Pure = null, HostDirectGraphRuntime? HostGraph = null)
    { internal bool IsPureText => Pure is not null || HostGraph is not null; }
    public void Load(CancellationToken cancellationToken)
    {
        try
        {
            Unload(); _instances.Clear(); Diagnostics = new();
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
                if (!definition.IsV2)
                    throw Failure(definition, "Load", 0, new EffectOutputException("PackageProfileUnsupported", "仅支持 SDK v2 效果包。"), Diagnostics.Snapshot());
                var active = definition.PackageDeclaration is EffectGraphDefinition ? LoadHostDirectGraph(definition) : LoadPure(definition, cancellationToken);
                _instances.Add(active);
            }
        }
        catch
        {
            try { Unload(); } catch { /* 保留原始失败。 */ }
            throw;
        }
    }
    public PreparedRenderFrame Evaluate(long timeUs, OutputMode outputMode, RenderTarget target,
        RuntimeDiagnosticBuffer diagnostics, CancellationToken cancellationToken,
        bool pureTextOnly = false, V2AnchorQueryBudget? anchorBudget = null)
    {
        ValidateMediaOutput(outputMode);
        SceneFrame scene = _geometry.GetSceneFrame(_snapshot, timeUs);
        scene = ExtendExitReplicas(scene, cancellationToken);
        FrameBindingScope scope = new(scene);
        bool hasExits = scene.Lines.Any(static line => line.ExitReplica is not null);
        bool sessionStyles = _snapshot.PresetApplications.Items.IsEmpty && !hasExits;
        var loadedStyles = sessionStyles ? null : StaticStyleProjection.LoadedSourceStyles(_snapshot, scene);
        var sourceMarkStyles = _snapshot.PresetApplications.Items.IsEmpty ? _sourceMarkStyles : StaticStyleProjection.MarkStyles(_snapshot, scene);
        // 本帧基线由会话基线派生：不在场行与场景无关、原样共享，只重算在场行与标记，并复用上面已解析的标记与装载外观。
        var baseline = sessionStyles ? _baseline : StaticStyleProjection.CreateFrameBaseline(_baseline, _snapshot, _sourceMaterials, scene,
            _snapshot.PresetApplications.Items.IsEmpty ? null : sourceMarkStyles, loadedStyles);
        var sourceGroupIds = scene.Elements.Where(static node => node.SourceNodeId is null).Select(static node => node.Id).ToHashSet(StringComparer.Ordinal);
        var sourceGroups = scene.GroupGeometries.Where(group => sourceGroupIds.Contains(group.Target.NodeId.Value)).ToImmutableArray();
        var allTokens = FrameProjection.Tokens(scene);
        Dictionary<TokenFamilyTarget, Affine2> combined = [];
        Dictionary<BaseTokenTarget, Affine2> baseTransforms = [];
        Dictionary<BaseTokenTarget, StyleUpdate> baseStyles = [];
        Dictionary<(BaseTokenTarget, MaterialSlot), ActiveEffect> baseMaterialOwners = [];
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
        // 模板逐行应用时实例按行／元素绑定、数量随全曲行数增长。Lines／Tokens 绑定的四类范围只经由行身份／元素身份判定
        // （Tokens 不含标记与组，Lines 不含组），绑定的行或元素都不在本帧时范围必为空，免去逐目标扫描与分配。
        HashSet<LineId>? frameLineIds = null; HashSet<TokenId>? frameTokenIds = null;
        bool OutsideFrame(EffectBinding binding) => binding switch
        {
            EffectBinding.Lines bound => !bound.Ids.Any((frameLineIds ??= [.. allTokens.Select(static token => token.LineId),
                .. allLines.Select(static line => line.LineId), .. allMarks.Select(static mark => mark.TargetLine.LineId)]).Contains),
            EffectBinding.Tokens bound => !bound.Ids.Any((frameTokenIds ??= [.. allTokens.Select(static token => token.TokenId),
                .. scene.Lines.SelectMany(static line => line.Frame.Line?.Tokens ?? []).Select(static token => new TokenId(token.Id))]).Contains),
            _ => false,
        };
        List<PreparedImageEffectBinding> imageEffects = [];
        var sourceBindings = _sourceMaterials?.Bindings(scene) ?? [];
        Dictionary<(ImageEffectTarget, PreparedMaterialSlot), PreparedMaterialBinding> materials = sourceBindings.ToDictionary(binding => (binding.Target, binding.Slot));
        foreach (var binding in sourceBindings)
            if (binding.Target is ImageEffectTarget.Token token) styles.TryAdd(token.Target, new());
            else if (binding.Target is ImageEffectTarget.Mark mark) markStyles.TryAdd(mark.Target, new());
        foreach (var active in _instances)
        {
            if (pureTextOnly && !active.IsPureText) continue;
            cancellationToken.ThrowIfCancellationRequested();
            var definition = active.Definition;
            bool outside = OutsideFrame(definition.Binding);
            var tokens = outside ? [] : allTokens.Where(token => scope.ContainsToken(definition.Binding, token)).ToImmutableArray();
            var lines = outside ? [] : allLines.Where(line => scope.ContainsLine(definition.Binding, line)).ToImmutableArray();
            var marks = outside ? [] : allMarks.Where(mark => scope.ContainsMark(definition.Binding, mark)).ToImmutableArray();
            var writableGroups = outside ? [] : sourceGroups.Where(group => scope.ContainsGroup(definition.Binding, group.Target)).ToImmutableArray();
            // 本时刻绑定范围内没有任何目标的纯实例（含宿主直接图）不能写文字或图绑定（越界写入一律拒绝），
            // 纯求值又没有跨帧状态（随机数按流键取值），输出必为空：正式帧与锚点补算都不调用，
            // 也不计入锚点文字调用，避免逐行应用模板时随全曲行数线性放大。
            if (tokens.IsEmpty && lines.IsEmpty && marks.IsEmpty && writableGroups.IsEmpty) continue;
            anchorBudget?.TextCall();
            var targets = tokens.Select(static token => token.WithRuby).ToHashSet();
            var scopedGeometry = scene.TokenGeometries.Where(item => targets.Contains(item.WithRuby)).ToImmutableArray();
            var scopedMarks = scene.MarkGeometries.Where(mark => marks.Any(item => item.Target == mark.Target)).ToImmutableArray();
            var scopedLines = scene.LineGeometries.Where(line => lines.Any(item => item.Target == line.Target)).ToImmutableArray();
            FrameContext frame = new() { TimeUs = timeUs, Project = _project, Parameters = definition.Parameters,
                Resources = definition.Resources, Random = active.Random, Baseline = baseline,
                Presentations = new() { TokensInBinding = tokens, LinesInBinding = lines, MarksInBinding = marks, Groups = FrameProjection.Groups(scene) },
                Geometry = scopedGeometry, LineGeometry = scopedLines, MarkGeometry = scopedMarks, GroupGeometry = sourceGroups, Binding = definition.Binding,
                OutputMode = outputMode, CancellationToken = cancellationToken };
            FrameOutput? output = null;
            Invoke(active, "Evaluate", timeUs, diagnostics, ignoredReporter =>
            {
                using (MeasureAuthor(active, "Evaluate"))
                {
                    if (active.HostGraph is not null) output = EvaluateHostDirectGraph(active, frame, writableGroups);
                    else output = EvaluatePure(active, frame, scene, writableGroups, cancellationToken);
                }
            });
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
                    foreach (var slot in MaterialSlots(style.Update)) baseMaterialOwners[(style.Target, slot)] = active;
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
        materialScopes.Complete(styles, markStyles, baseStyles, baseMaterialOwners, baseline, styleOwners, markOwners, materials, timeUs, diagnostics, cancellationToken);
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
                [.. clusterTransforms.Where(static pair => pair.Value != Affine2.Identity).Select(static pair => new V2.TextClusterTransform(pair.Key, pair.Value))],
                ComposeBaseMaterials(prepared, baseStyles, baseMaterialOwners, timeUs, diagnostics, cancellationToken))
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
    public void Unload()
    {
        _v2AnchorMemo.Clear(); _v2PrepareBudget = new(); _v2CursorCuts.Clear(); _v2CursorCutCount = 0; _v2AnchorBreakpoints = default;
        var owned = _instances.ToArray(); _instances.Clear(); List<Exception> failures = [];
        foreach (var active in owned)
        {
            active.Pure?.Source.Indexes.Invalidate();
            try { active.Module?.Dispose(); }
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
