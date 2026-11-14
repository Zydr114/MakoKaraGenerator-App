using System.Collections.Immutable;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Scripting.Loading;
using MakoKaraGenerator.Scripting.Projection;
using MakoKaraGenerator.Scripting.Particles.V2;
using MakoKaraGenerator.Scripting.V2;

namespace MakoKaraGenerator.Scripting.Execution;

internal sealed partial class FrameEffectHost
{
    private sealed record PureEffectRuntime(IPureKaraokeEffect Instance, DeclareContext Source, PreparedEffect Prepared,
        ParticleOwner? ParticleOwner = null, CompiledParticleSchedule? ParticleSchedule = null, EmissionSourceCatalog? ParticleSources = null,
        ProjectMediaFeatureProjection? MediaProjection = null);

    private ActiveEffect LoadPure(CompiledFrameEffect definition, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ScriptModule? module = null;
        SourceIndexes? indexes = null;
        try
        {
            var artifact = definition.Artifact ?? throw new EffectOutputException("PackageEntryInvalid", "纯文字入口缺少固定编译产物。");
            module = new(artifact.PeImage.AsMemory(), artifact.PdbImage.AsMemory());
            var instance = module.CreateInstance<IPureKaraokeEffect>(definition.EntryType!);
            var mediaProjection = _mediaBinding is null ? null : ProjectMediaFeatureProjection.ResolveInstance(_mediaBinding, definition, token);
            var timeline = _timeline ?? throw new EffectOutputException("PureSourceIndexRequired", "纯求值需要固定源索引。");
            var scope = ResolvedSourceSubscription.Create(timeline, definition.Binding, definition.Subscription);
            indexes = PureSourceIndexes.Create(_snapshot, timeline, _geometry, definition.Binding, definition.Subscription, token);
            var source = new DeclareContext(_project.Content, _project.Scene, definition.Parameters, _project.Audio,
                definition.Resources, definition.Seed, indexes.Timeline, _project.CanvasSize, definition.Id, indexes, mediaProjection?.Catalog ?? MediaQueryCatalog.Empty, _project.DurationUs);
            string? telemetry = PerformanceTelemetry.CreateScriptIdentity($"{definition.RuntimeHash}|{definition.Id}|{definition.PackageIdentity}");
            var active = new ActiveEffect(definition, module, new(definition.Seed, definition.Id), scope, telemetry);
            PreparedEffect? prepared = null;
            Invoke(active, "Declare", 0, Diagnostics, () =>
            {
                using var measured = MeasureAuthor(active, "Declare");
                prepared = instance.Declare(source) ?? throw new EffectOutputException("PreparedEffectMissing", "Declare 必须返回递归只读准备结果。");
            });
            token.ThrowIfCancellationRequested();
            var pure = new PureEffectRuntime(instance, source, prepared!, MediaProjection: mediaProjection);
            Invoke(active, "PrepareParticles", 0, Diagnostics, () =>
            {
                var package = definition.PackageDefinition!;
                PreparedEffectDeclaration.Validate(prepared!, source, package.Contents.Requires,
                    [.. package.Emitters.Select(static item => new ParticleEmitterDeclaration(new(item.Id), item.Kind, item.MaxLifetimeUs))],
                    [.. definition.PackageDeclaration!.Outputs.Where(static item => item.Kind == "Overlay" && item.Target == "canvas")
                        .Select(static item => new CanvasOutputDeclaration(new(item.Id), item.Requires))], token);
                string inputs = _runtimeInputsFingerprint ?? throw new EffectOutputException("EffectInputsMismatch", "纯发射器缺少固定输入指纹。");
                var owner = new ParticleOwner(definition.Id, definition.PackageIdentity, definition.RuntimeHash, definition.Seed,
                    inputs, "m08-prepared-v1|" + inputs + "|" + definition.RuntimeHash + "|" + definition.Id.Value);
                var sources = BuildV2EmissionSources(active, pure, token);
                _v2PrepareBudget.ReserveBytes(owner, 256L * (sources.Continuous.Length + sources.Bursts.Length));
                var schedule = CompiledParticleSchedule.Compile(prepared!.Particles, sources, owner, _v2PrepareBudget, token);
                pure = pure with { ParticleOwner = owner, ParticleSchedule = schedule, ParticleSources = sources };
            });
            token.ThrowIfCancellationRequested();
            return active with { Pure = pure };
        }
        catch (OperationCanceledException) { indexes?.Invalidate(); module?.Dispose(); throw; }
        catch (Exception error)
        {
            indexes?.Invalidate();
            try { module?.Dispose(); } catch { /* 保留准备失败及其归属。 */ }
            if (error is EffectFailureException) throw;
            throw Failure(definition, "Declare", 0, error, Diagnostics.Snapshot());
        }
    }

    private static Affine2 ComposeFor(ActiveEffect active, Affine2 previous, Affine2 next)
        => active.Pure is null ? previous.Compose(next) : next.Compose(previous);

    private static FrameOutput EvaluatePure(ActiveEffect active, TextFrameHostInputs frame, SceneFrame scene,
        ImmutableArray<GroupGeometrySample> writableGroups, CancellationToken token)
    {
        var pure = active.Pure!;
        pure.Source.Indexes.SetQueryCancellation(token);
        MediaFeatures media = pure.MediaProjection?.CreateStage(token) ?? new(pure.Source.Media, pure.Source.CanvasSize, token: token);
        try
        {
            TextOutput? output = null;
            try
            {
                TextFrame input = new(pure.Source, frame, PresentationIndex.LineLoads(scene,
                    frame.Presentations.LinesInBinding, active.Definition.PackageDefinition!.Contents.LineExitDurationUs), media);
                output = new(input, active.Definition.PackageDefinition!.Contents.Requires,
                    [.. writableGroups.Select(static group => group.Target)]) { GraphCancellation = token };
                pure.Instance.Evaluate(in input, pure.Prepared, output);
                token.ThrowIfCancellationRequested();
                return ComposePureOutput(output.Complete(), token);
            }
            finally { output?.Invalidate(); }
        }
        finally { media.Invalidate(); }
    }

    private static FrameOutput ComposePureOutput(TextOutputSnapshot snapshot, CancellationToken token)
    {
        Dictionary<TextTarget, Affine2> transforms = [];
        Dictionary<TextTarget, StyleUpdate> styles = [];
        Dictionary<GroupTarget, double> groupOpacity = [];
        var warps = ImmutableArray.CreateBuilder<TextWrite.Warp>();
        var graphs = ImmutableArray.CreateBuilder<TextWrite.GraphBinding>();
        var materialWrites = ImmutableArray.CreateBuilder<TextWrite>();
        Dictionary<ImageEffectTarget, ImageEffectBinding> graphBindings = [];
        foreach (var write in snapshot.Writes)
        {
            token.ThrowIfCancellationRequested();
            if (write is TextWrite.GraphBinding graph)
            {
                graphs.Add(graph);
                var target = GraphTarget(graph.Target);
                // Dictionary insertion order is the first target write; replacing
                // its value never reorders a different target or instance.
                graphBindings[target] = new(target, graph.Value);
                continue;
            }
            if (write is TextWrite.Fill or TextWrite.Strokes or TextWrite.MaterialUniform) materialWrites.Add(write);
            if (write is TextWrite.MaterialUniform) continue; // Final identity is a whole-frame host reduction.
            if (write is TextWrite.Warp warp) { warps.Add(warp); continue; }
            if (write is TextWrite.Transform transform)
            {
                transforms[write.Target] = transform.LocalMatrix.Compose(transforms.GetValueOrDefault(write.Target, Affine2.Identity));
                continue;
            }
            if (write is TextWrite.Opacity opacity && write.Target is TextTarget.Group group)
            {
                groupOpacity[group.Value] = groupOpacity.GetValueOrDefault(group.Value, 1) * opacity.Factor;
                continue;
            }
            StyleUpdate update = write switch
            {
                TextWrite.Opacity value => new(opacityFactor: value.Factor),
                TextWrite.Fill value => State(value.State, new(fill: value.Material)),
                TextWrite.Strokes value => State(value.State, new(strokes: new(value.Layers))),
                TextWrite.Shadow value => State(value.State, new(shadow: new(value.Value))),
                TextWrite.Glow value => State(value.State, new(glow: new(value.Value))),
                TextWrite.Wipe value => new(wipe: value.Value),
                _ => throw new EffectOutputException("PropertyTargetUnsupported", "未知文字输出。"),
            };
            styles[write.Target] = styles.TryGetValue(write.Target, out var previous) ? previous.Then(update) : update;
        }
        // TextOutput 已把「两态」展开成两条写入，这里每条只属于一态。
        static StyleUpdate State(TextState state, AppearanceUpdate appearance) => state == TextState.Unsung ? new(unsung: appearance) : new(sung: appearance);
        foreach (var target in transforms.Where(static pair => pair.Value == Affine2.Identity).Select(static pair => pair.Key).ToArray())
            transforms.Remove(target);
        return new()
        {
            TextWarps = warps.ToImmutable(),
            GraphWrites = graphs.ToImmutable(),
            MaterialWrites = materialWrites.ToImmutable(),
            ImageEffects = [.. graphBindings.Values],
            TokenTransforms = [.. transforms.Where(static pair => pair.Key is TextTarget.Family)
                .Select(static pair => new TokenFamilyTransform(((TextTarget.Family)pair.Key).Value, pair.Value))],
            LineTransforms = [.. transforms.Where(static pair => pair.Key is TextTarget.Line)
                .Select(static pair => new LineTransform(((TextTarget.Line)pair.Key).Value, pair.Value))],
            GroupTransforms = [.. transforms.Where(static pair => pair.Key is TextTarget.Group)
                .Select(static pair => new GroupTransform(((TextTarget.Group)pair.Key).Value, pair.Value))],
            MarkTransforms = [.. transforms.Where(static pair => pair.Key is TextTarget.Mark)
                .Select(static pair => new TextMarkTransform(((TextTarget.Mark)pair.Key).Value, pair.Value))],
            TokenStyles = [.. styles.Where(static pair => pair.Key is TextTarget.Family)
                .Select(static pair => new TokenFamilyStyle(((TextTarget.Family)pair.Key).Value, pair.Value))],
            MarkStyles = [.. styles.Where(static pair => pair.Key is TextTarget.Mark)
                .Select(static pair => new TextMarkStyle(((TextTarget.Mark)pair.Key).Value, pair.Value))],
            GroupOpacities = [.. groupOpacity.Select(static pair => new GroupOpacity(pair.Key, pair.Value))],
            BaseTransforms = [.. transforms.Where(static pair => pair.Key is TextTarget.Base)
                .Select(static pair => new BaseTokenTransform(((TextTarget.Base)pair.Key).Value, pair.Value))],
            BaseStyles = [.. styles.Where(static pair => pair.Key is TextTarget.Base)
                .Select(static pair => new BaseTokenStyle(((TextTarget.Base)pair.Key).Value, pair.Value))],
            ClusterTransforms = [.. transforms.Where(static pair => pair.Key is TextTarget.Cluster)
                .Select(static pair => new TextClusterTransform(((TextTarget.Cluster)pair.Key).Value, pair.Value))],
        };
    }
}
