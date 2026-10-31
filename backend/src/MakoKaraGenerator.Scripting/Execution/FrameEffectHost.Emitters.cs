using System.Collections.Immutable;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Scripting.Particles;

namespace MakoKaraGenerator.Scripting.Execution;

internal sealed partial class FrameEffectHost
{
    public async ValueTask EvaluateEmittersAsync(PreparedRenderFrame text, TimelineHistory? history, long nextBoundaryUs,
        RuntimeDiagnosticBuffer diagnostics, CancellationToken cancellationToken)
    {
        List<ResolvedEmitter> emitters = [];
        foreach (var active in _instances.Where(static active => HasOverride(active.Instance, nameof(IKaraokeEffect.EvaluateEmitters))))
        {
            cancellationToken.ThrowIfCancellationRequested(); var definition = active.Definition;
            EmitterWriter writer = new(definition.Id, definition.Capabilities);
            var frame = new EmissionFrameContext { Project = _project, Parameters = definition.Parameters, Resources = definition.Resources,
                Random = active.Random, Binding = definition.Binding, OutputMode = text.OutputMode,
                FinalGeometry = FinalGeometryFor(active, text), CancellationToken = cancellationToken };
            if (history is not null) frame = frame with { History = history };
            ImmutableArray<EmitterBinding> output = [];
            try
            {
                Invoke(active, "EvaluateEmitters", text.TimeUs, diagnostics, reporter =>
                {
                    using (MeasureAuthor(active, "EvaluateEmitters"))
                        active.Instance.EvaluateEmitters(frame with { Diagnostics = reporter }, writer);
                    output = writer.Complete();
                    if (definition.OverlayLayerOverride is { } layer) output = [.. output.Select(emitter => emitter with { Descriptor = emitter.Descriptor with { Layer = layer } })];
                    if (!output.IsEmpty && history is null)
                        throw new EffectOutputException("TimelineSessionRequired", "连续发射需要固定时间线会话。");
                    foreach (var emitter in output) frame.Playback.GetCursors(emitter.Descriptor.AnchorMode);
                    if (definition.PackageDeclaration is { } declaration && output.Any(emitter => !declaration.Outputs.Any(item => item.Kind == "Overlay" && item.Id == emitter.Id.Value)))
                        throw new EffectOutputException("UndeclaredPackageOutput", "发射输出未在固定包导出中声明。");
                });
            }
            finally { writer.Invalidate(); }
            foreach (var emitter in output)
            {
                var bridges = await ResolveBridgesAsync(active, emitter, text, nextBoundaryUs, diagnostics, cancellationToken).ConfigureAwait(false);
                emitters.Add(new(emitter, active.Random, frame.Playback, bridges));
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (history is not null) ParticleOperation(() => { _particles.Register(text.TimeUs, emitters); return true; });
    }
    public PreparedRenderFrame PrepareParticles(PreparedRenderFrame text, TimelineHistory history,
        RuntimeDiagnosticBuffer diagnostics, CancellationToken cancellationToken)
    {
        var particles = ParticleOperation(() => _particles.Read(text.TimeUs,
            BirthGeometry(text.OutputMode, text.Target, history, cancellationToken), _visualBudget.DrawItemLimit, cancellationToken));
        return text.WithVisualSnapshot(particles.Dots, diagnostics.Snapshot(), _visualBudget, particles.Particles, _particles.EmissionSchedule);
    }
}
