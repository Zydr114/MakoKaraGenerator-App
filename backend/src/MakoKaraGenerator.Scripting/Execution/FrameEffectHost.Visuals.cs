using System.Collections.Immutable;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Scripting.Projection;

namespace MakoKaraGenerator.Scripting.Execution;

internal sealed partial class FrameEffectHost
{
    public void AdvanceVisuals(VisualSimulationStep step, PreparedRenderFrame previous, PreparedRenderFrame current,
        TimelineHistory previousHistory, TimelineHistory history, CancellationToken cancellationToken)
    {
        foreach (var active in _instances.Where(static active => HasOverride(active.Instance, nameof(IKaraokeEffect.AdvanceVisuals))))
        {
            cancellationToken.ThrowIfCancellationRequested(); var definition = active.Definition;
            var left = FinalGeometryFor(active, previous); var right = FinalGeometryFor(active, current);
            Invoke(active, "AdvanceVisuals", step.EndUs, Diagnostics, reporter =>
            {
                using var measured = MeasureAuthor(active, "AdvanceVisuals");
                active.Instance.AdvanceVisuals(step, new()
                { Project = _project, Parameters = definition.Parameters, Resources = definition.Resources, Random = active.Random,
                    Binding = definition.Binding, OutputMode = current.OutputMode, PreviousGeometry = left, CurrentGeometry = right,
                    PreviousHistory = previousHistory, History = history, PreviousParticles = ParticlesFor(active, previous),
                    Particles = ParticlesFor(active, current), EmissionSchedule = ScheduleFor(active, current),
                    Diagnostics = reporter, CancellationToken = cancellationToken });
            });
        }
    }
    public PreparedRenderFrame EvaluateVisuals(PreparedRenderFrame text, PreparedRenderFrame? boundary, TimelineHistory? history,
        RuntimeDiagnosticBuffer diagnostics, CancellationToken cancellationToken)
    {
        List<OverlayBatch> batches = [.. text.OverlayBatches]; long count = batches.Sum(static batch => batch.ItemCount);
        foreach (var active in _instances.Where(static active => HasOverride(active.Instance, nameof(IKaraokeEffect.EvaluateVisuals))))
        {
            cancellationToken.ThrowIfCancellationRequested(); var definition = active.Definition;
            VisualWriter writer = new(definition.Id, definition.Capabilities, _visualBudget);
            var frame = new VisualFrameContext { Project = _project, Parameters = definition.Parameters, Resources = definition.Resources,
                Random = active.Random, Binding = definition.Binding, OutputMode = text.OutputMode,
                FinalGeometry = FinalGeometryFor(active, text), BoundaryGeometry = boundary is null ? null : FinalGeometryFor(active, boundary),
                CancellationToken = cancellationToken };
            if (history is not null) frame = frame with { History = history, Particles = ParticlesFor(active, text), EmissionSchedule = ScheduleFor(active, text) };
            try
            {
                Invoke(active, "EvaluateVisuals", text.TimeUs, diagnostics, reporter =>
                {
                    using (MeasureAuthor(active, "EvaluateVisuals"))
                        active.Instance.EvaluateVisuals(frame with { Diagnostics = reporter }, writer);
                    var output = writer.CompleteOverlay();
                    if (output.OfType<SpriteBatch>().SelectMany(batch => batch.Sprites).Any(sprite => !definition.Resources.Contains(sprite.Image)))
                        throw new EffectOutputException("InvalidSpriteResource", "图片精灵句柄不属于本实例固定包修订。");
                    if (definition.OverlayLayerOverride is { } layer) output = [.. output.Select(batch => batch with { Layer = layer })];
                    if (definition.PackageDeclaration is { } declaration && output.Any(batch => !declaration.Outputs.Any(item => item.Kind == "Overlay" && item.Id == batch.Id.Value)))
                        throw new EffectOutputException("UndeclaredPackageOutput", "视觉批次未在固定包导出中声明。");
                    if (output.Any(batch => batches.Any(existing => existing.InstanceId == batch.InstanceId && existing.Id == batch.Id)
                        || text.Particles.Any(particle => particle.Id.InstanceId == batch.InstanceId && particle.Id.EmitterId.Value == batch.Id.Value)))
                        throw new EffectOutputException("DuplicateOverlayBatch", "手工批次与仍存活粒子的发射输出同名。");
                    long requested = count + output.Sum(static batch => batch.ItemCount);
                    if (requested > _visualBudget.DrawItemLimit)
                        throw new EffectOutputException("VisualOutputBudgetExceeded", $"全部实例绘制项请求 {requested}，限制 {_visualBudget.DrawItemLimit}。");
                    count = requested; batches.AddRange(output);
                });
            }
            finally { writer.Invalidate(); }
        }
        cancellationToken.ThrowIfCancellationRequested();
        var order = _definitions.Select((definition, index) => (definition.Id, index)).ToDictionary(static item => item.Id, static item => item.index);
        return text.WithOverlaySnapshot([.. batches.OrderBy(batch => order[batch.InstanceId])
            .ThenBy(static batch => batch.Id.Value, StringComparer.Ordinal)], diagnostics.Snapshot(), _visualBudget,
            owners: [.. _definitions.Select(definition => new PreparedOverlayOwner(definition.Id, definition.RuntimeHash, definition.PackageIdentity))]);
    }
    private static ImmutableArray<ParticleSample> ParticlesFor(ActiveEffect active, PreparedRenderFrame frame)
        => [.. frame.Particles.Where(particle => particle.Id.InstanceId == active.Definition.Id)];
    private static EmissionSchedule ScheduleFor(ActiveEffect active, PreparedRenderFrame frame)
    {
        var schedule = frame.EmissionSchedule ?? throw new InvalidOperationException("时间线帧缺少出生记录。");
        return new(schedule.StartUs, schedule.EndUs, [.. schedule.Births.Where(birth => birth.Id.InstanceId == active.Definition.Id)]);
    }
    private FinalSceneGeometry FinalGeometryFor(ActiveEffect active, PreparedRenderFrame frame)
    {
        var scene = frame.HierarchyScene; var binding = active.Definition.Binding;
        FrameBindingScope scope = new(scene);
        var activeBindings = scene.Lines.Where(static line => line.ExitReplica is null)
            .Select(static line => line.BindingId!).ToHashSet(StringComparer.Ordinal);
        var tokens = FrameProjection.Tokens(scene).Where(token => (active.Pure is not null || activeBindings.Contains(token.BindingId.Value))
            && scope.ContainsToken(binding, token)).ToImmutableArray();
        var lines = FrameProjection.Lines(scene, _project.Scene).Where(line => (active.Pure is not null || activeBindings.Contains(line.BindingId.Value))
            && scope.ContainsLine(binding, line)).ToImmutableArray();
        var marks = FrameProjection.Marks(scene, _project.Scene).Where(mark => scope.ContainsMark(binding, mark)).ToImmutableArray();
        var tokenTargets = tokens.Select(static token => token.WithRuby).ToHashSet();
        var lineTargets = lines.Select(static line => line.Target).ToHashSet(); var markTargets = marks.Select(static mark => mark.Target).ToHashSet();
        var sourceGroupIds = scene.Elements.Where(static node => node.SourceNodeId is null).Select(static node => node.Id).ToHashSet(StringComparer.Ordinal);
        var groups = frame.FinalGroupGeometry.Where(group => sourceGroupIds.Contains(group.Target.NodeId.Value)).ToImmutableArray();
        if (active.Pure is null && scene.Lines.Any(static line => line.ExitReplica is not null))
        {
            // The source group ink also aggregates replica aliases for v2. Recompute
            // the legacy view from active loads so old visual authors cannot observe
            // or anchor particles to the new presentation-only extension.
            groups = LegacyGroups(scene, frame.FinalGeometry, frame.FinalMarkGeometry, groups, activeBindings);
        }
        bool CursorInBinding(CursorGeometry cursor) => cursor.Identity.TokenId is { } token
            ? tokenTargets.Contains(new(token, cursor.Identity.BindingId))
            : binding is not EffectBinding.Tokens && lineTargets.Contains(new(cursor.Identity.LineId, cursor.Identity.BindingId));
        return new() { TimeUs = frame.TimeUs,
            Presentations = new() { TokensInBinding = tokens, LinesInBinding = lines, MarksInBinding = marks, Groups = FrameProjection.Groups(scene) },
            Tokens = [.. frame.FinalGeometry.Where(token => tokenTargets.Contains(token.WithRuby))],
            Lines = [.. frame.FinalLineGeometry.Where(line => lineTargets.Contains(line.Target))],
            Marks = [.. frame.FinalMarkGeometry.Where(mark => markTargets.Contains(mark.Target))],
            Groups = groups,
            Playback = frame.Playback.IsAvailable ? new([.. frame.Playback.Cursors.Where(CursorInBinding)]) : PlaybackFrame.Unavailable };
    }
    private static ImmutableArray<GroupGeometrySample> LegacyGroups(SceneFrame scene,
        ImmutableArray<TokenGeometrySample> tokens, ImmutableArray<TextMarkGeometrySample> marks,
        ImmutableArray<GroupGeometrySample> groups, HashSet<string> activeBindings)
    {
        var activeScene = scene with
        {
            Lines = [.. scene.Lines.Where(static line => line.ExitReplica is null)],
            Elements = [.. scene.Elements.Where(static node => node.SourceNodeId is null)],
            LineGeometries = [.. scene.LineGeometries.Where(line => activeBindings.Contains(line.Target.BindingId.Value))],
            GroupGeometries = groups,
        };
        return GroupInkProjection.Create(activeScene,
            [.. tokens.Where(token => activeBindings.Contains(token.WithRuby.BindingId.Value))], marks);
    }
    private static bool HasOverride(IKaraokeEffect? instance, string name)
    {
        if (instance is null) return false;
        var map = instance.GetType().GetInterfaceMap(typeof(IKaraokeEffect));
        for (int i = 0; i < map.InterfaceMethods.Length; i++)
            if (map.InterfaceMethods[i].Name == name) return map.TargetMethods[i].DeclaringType != typeof(EffectBase);
        return false;
    }
}
