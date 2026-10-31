using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Scripting.Particles;

namespace MakoKaraGenerator.Scripting.Execution;

internal sealed partial class FrameEffectHost
{
    private ContinuousParticleSimulation _particles = null!;
    public void AdvanceParticles(long endUs, OutputMode mode, RenderTarget target,
        TimelineHistory history, CancellationToken cancellationToken)
    {
        var geometry = BirthGeometry(mode, target, history, cancellationToken);
        ParticleOperation(() => { _particles.Commit(endUs, geometry, cancellationToken); return true; });
    }
    private Func<EffectInstanceId, long, PlaybackFrame> BirthGeometry(OutputMode mode, RenderTarget target,
        TimelineHistory history, CancellationToken cancellationToken)
    {
        long cachedTime = -1; PreparedRenderFrame? cached = null;
        return (instance, timeUs) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (cachedTime != timeUs)
            {
                // 在共享 Advance 前解析出生几何；没有事件／视觉求值或持久发射。
                RuntimeDiagnosticBuffer diagnostics = new(Diagnostics.Snapshot());
                cached = Evaluate(timeUs, mode, target, history, diagnostics, cancellationToken); cachedTime = timeUs;
            }
            var active = _instances.Single(item => item.Definition.Id == instance);
            return FinalGeometryFor(active, cached!).Playback;
        };
    }
    private T ParticleOperation<T>(Func<T> operation)
    {
        using var performance = PerformanceTelemetry.Measure(PerformancePhase.Particle);
        try { return operation(); }
        catch (ParticleSimulationException error)
        {
            var definition = _definitions.Single(item => item.Id == error.Instance);
            throw Failure(definition, "SimulateParticles", error.TimeUs, new EffectOutputException(error.Code, error.Message), Diagnostics.Snapshot());
        }
    }
}
