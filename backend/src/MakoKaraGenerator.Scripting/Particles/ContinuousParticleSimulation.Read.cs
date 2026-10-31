using System.Collections.Immutable;

namespace MakoKaraGenerator.Scripting.Particles;

// 纯读取在副本上解析，不登记意图或提交出生。
internal sealed partial class ContinuousParticleSimulation
{
    public ParticleFrame Read(long timeUs, Func<EffectInstanceId, long, PlaybackFrame> geometry, int drawLimit, CancellationToken cancellationToken)
    {
        // 临时出生包括 t，不提交到持久模拟；边界出生也只有下一半开区间接纳一次。
        var copy = new ContinuousParticleSimulation(budget)
        {
            _boundary = _boundary, _trailCount = _trailCount, _lastPruned = _lastPruned,
            _emitters = _emitters.ToDictionary(static item => item.Key, static item => item.Value.Copy()),
            _particles = _particles.Select(static particle => particle.Copy()).ToList(), _boundaryBirths = [.. _boundaryBirths]
        };
        long end = checked(timeUs + 1);
        copy.Advance(end, geometry, cancellationToken); copy.Prune(timeUs);
        List<ParticleSample> samples = []; Dictionary<EmitterKey, List<Dot>> dots = []; long drawCount = 0;
        foreach (var particle in copy._particles)
        {
            cancellationToken.ThrowIfCancellationRequested(); var descriptor = particle.Descriptor;
            try
            {
                var position = particle.Position(timeUs);
                double age = (timeUs - particle.BirthUs) / (double)descriptor.LifetimeUs;
                double opacity = descriptor.Motion.Opacity.Evaluate(age), size = descriptor.SizePx * descriptor.Motion.Size.Evaluate(age);
                if (opacity is < 0 or > 1 || size < 0 || !double.IsFinite(size))
                    throw new ParticleSimulationException(particle.Key.Instance, timeUs, "InvalidParticleAppearance", "年龄外观曲线产生非法透明度或尺寸。");
                var color = new ColorRgba(descriptor.Color.Red, descriptor.Color.Green, descriptor.Color.Blue, descriptor.Color.Alpha * opacity);
                if (descriptor.TrailDurationUs > 0) copy.AddTrail(particle, timeUs);
                samples.Add(new() { Id = new(particle.Key.Instance, particle.Key.Emitter, particle.Index), Source = particle.Source, EventId = particle.EventId, BirthUs = particle.BirthUs, DeathUs = particle.DeathUs, BirthPosition = particle.BirthPosition,
                    Position = position, SizePx = size, Color = color, Layer = copy._emitters[particle.Key].Layer, Trail = [.. particle.Trail] });
                if (descriptor.Rendering == ParticleRenderMode.Custom) continue;
                if (!dots.TryGetValue(particle.Key, out var batch)) dots.Add(particle.Key, batch = []);
                if (size == 0 || color.Alpha == 0) continue;
                int requested = 1 + particle.Trail.Where(sample => sample.TimeUs != timeUs && sample.Position != position).Select(sample => sample.Position).Distinct().Count();
                drawCount += requested;
                if (drawCount > drawLimit) throw new ParticleSimulationException(particle.Key.Instance, timeUs, "VisualOutputBudgetExceeded", "粒子与手工图元超过全部实例绘制预算。");
                HashSet<Point2> drawn = [position];
                foreach (var sample in particle.Trail)
                    if (sample.TimeUs != timeUs && drawn.Add(sample.Position)) batch.Add(new(sample.Position, size / 2, color));
                batch.Add(new(position, size / 2, color));
            }
            catch (Exception error) when (error is ArgumentException or OverflowException)
            { throw new ParticleSimulationException(particle.Key.Instance, timeUs, "InvalidParticleAppearance", error.Message); }
        }
        return new([.. samples], [.. dots.OrderBy(static item => item.Key.Instance.Value, StringComparer.Ordinal)
            .ThenBy(static item => item.Key.Emitter.Value, StringComparer.Ordinal)
            .Select(item => new DotBatch(item.Key.Instance, new(item.Key.Emitter.Value), copy._emitters[item.Key].Layer, [.. item.Value]))]);
    }
}
