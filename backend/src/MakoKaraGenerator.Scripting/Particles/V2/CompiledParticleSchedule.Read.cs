using System.Collections.Immutable;
using System.Numerics;
using MakoKaraGenerator.Scripting.V2;

namespace MakoKaraGenerator.Scripting.Particles.V2;

public sealed partial class CompiledParticleSchedule
{
    /// <summary>读取闭窗内全部出生，包括查询时已经死亡的记录；先计数，后分配和反解。</summary>
    public ImmutableArray<ScheduledBirth> ReadBirths(BirthWindow window, ParticleQueryBudget budget,
        CancellationToken cancellationToken = default)
        => Read(window, null, budget, cancellationToken);

    /// <summary>独立读取指定时刻的存活记录；只扣存活计数，不重复扣出生窗计数。</summary>
    public ImmutableArray<ScheduledBirth> ReadAlive(long timeUs, ParticleQueryBudget budget,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(timeUs);
        return Read(new(Math.Max(0, timeUs - ParticleLimits.MaximumLifetimeUs + 1), timeUs), timeUs, budget, cancellationToken);
    }

    private ImmutableArray<ScheduledBirth> Read(BirthWindow window, long? aliveAt, ParticleQueryBudget budget, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(budget); token.ThrowIfCancellationRequested();
        var context = new ExactParticleContext(_owner, Math.Min(_maximumBits, budget.MaximumBits), token, budget, window.EndInclusiveUs);
        budget.ReserveBytes(_owner, checked((long)_episodes.Length * 80 + (long)_bursts.Length * 16), window.EndInclusiveUs);
        List<Selection> selected = []; BigInteger total = BigInteger.Zero;
        foreach (var episode in _episodes)
        {
            token.ThrowIfCancellationRequested();
            if (episode.Range.StartUs > window.EndInclusiveUs) break;
            if (episode.Total == 0 || episode.Range.EndUs <= window.StartInclusiveUs) continue;
            long start = aliveAt is { } time ? Math.Max(window.StartInclusiveUs, Math.Max(0, time - episode.Plan.Particle.LifetimeUs + 1))
                : window.StartInclusiveUs;
            var before = start == 0 ? BigInteger.Zero : episode.Count(start - 1, context);
            var through = episode.Count(window.EndInclusiveUs, context);
            if (through <= before) continue;
            total = context.Add(total, through - before);
            selected.Add(new(episode, (ulong)(before + 1), (ulong)through));
        }
        int firstBurst = BurstBoundary(window.StartInclusiveUs, inclusive: true), lastBurst = BurstBoundary(window.EndInclusiveUs, inclusive: false);
        for (int index = firstBurst; index < lastBurst; index++)
        {
            token.ThrowIfCancellationRequested(); var burst = _bursts[index];
            if (aliveAt is { } time && time - burst.Event.TimeUs >= burst.Row.Particle.LifetimeUs) continue;
            total = context.Add(total, burst.Row.Count);
            budget.ReserveBytes(_owner, checked(ParticleIdentityEncoding.SourceCharacters(burst.Source) * 8), window.EndInclusiveUs);
        }
        if (aliveAt is { } queryTime)
        {
            if (total > int.MaxValue) throw _owner.Failure("ReadParticleBirths", queryTime, "LiveParticleBudgetExceeded", "存活计数超过固定预算。");
            budget.ReserveAlive(_owner, (int)total, queryTime);
        }
        else budget.ReserveBirths(_owner, total, window.EndInclusiveUs);
        int count = checked((int)total);
        // 身份记录、临时排序引用和 BigInteger 反解临时值的保守上界。
        budget.ReserveBytes(_owner, checked((long)count * 768 + 16_384), window.EndInclusiveUs);
        var result = new List<ScheduledBirth>(count);
        foreach (var selection in selected)
        {
            var episode = selection.Episode; ulong sequence = selection.First;
            while (true)
            {
                token.ThrowIfCancellationRequested(); long birth = episode.Solve(sequence, context);
                var origin = new ParticleOrigin.Continuous(episode.Head, episode.Range.StartUs, sequence);
                result.Add(new(new(_owner.InstanceId, episode.Plan.Id, origin), episode.SourceAt(birth, context), null,
                    birth, checked(birth + episode.Plan.Particle.LifetimeUs), episode.Plan.Particle, episode.Plan.AnchorMode)
                { OriginOrder = episode.HeadOrder });
                if (sequence == selection.Last) break;
                sequence++;
            }
        }
        for (int index = firstBurst; index < lastBurst; index++)
        {
            token.ThrowIfCancellationRequested(); var burst = _bursts[index];
            if (aliveAt is { } time && time - burst.Event.TimeUs >= burst.Row.Particle.LifetimeUs) continue;
            string order = ParticleIdentityEncoding.Source(burst.Source);
            for (int ordinal = 0; ordinal < burst.Row.Count; ordinal++)
            {
                token.ThrowIfCancellationRequested();
                result.Add(new(new(_owner.InstanceId, burst.Emitter,
                    new ParticleOrigin.Burst(burst.Event.EventId, burst.PlanOrdinal, burst.Source, ordinal)), burst.Source,
                    burst.Event.EventId, burst.Event.TimeUs, checked(burst.Event.TimeUs + burst.Row.Particle.LifetimeUs), burst.Row.Particle, burst.AnchorMode)
                { Trigger = burst.Event, OriginOrder = order });
            }
        }
        result.Sort(CompareBirths); token.ThrowIfCancellationRequested();
        return [.. result];
    }

    private sealed record Selection(Episode Episode, ulong First, ulong Last);

    // lower_bound(start) / upper_bound(end)，无需构造 end+1。
    private int BurstBoundary(long timeUs, bool inclusive)
    {
        int low = 0, high = _bursts.Length;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            if (_bursts[middle].Event.TimeUs < timeUs || !inclusive && _bursts[middle].Event.TimeUs == timeUs) low = middle + 1;
            else high = middle;
        }
        return low;
    }

    private static int CompareBirths(ScheduledBirth left, ScheduledBirth right)
    {
        int result = left.BirthUs.CompareTo(right.BirthUs);
        if (result == 0 && left.Trigger is { } a && right.Trigger is { } b) result = TimelineEvent.Comparer.Compare(a, b);
        else if (result == 0 && (left.Trigger is null) != (right.Trigger is null)) result = left.Trigger is null ? -1 : 1;
        if (result == 0) result = StringComparer.Ordinal.Compare(left.Id.EmitterId.Value, right.Id.EmitterId.Value);
        if (result == 0) result = StringComparer.Ordinal.Compare(left.OriginOrder, right.OriginOrder);
        if (result == 0 && left.Id.Origin is ParticleOrigin.Continuous x && right.Id.Origin is ParticleOrigin.Continuous y)
        {
            result = x.EpisodeStartUs.CompareTo(y.EpisodeStartUs);
            if (result == 0) result = x.SequenceStartingAt1.CompareTo(y.SequenceStartingAt1);
        }
        else if (result == 0 && left.Id.Origin is ParticleOrigin.Burst xBurst && right.Id.Origin is ParticleOrigin.Burst yBurst)
        {
            result = xBurst.PlanOrdinal.CompareTo(yBurst.PlanOrdinal);
            if (result == 0) result = xBurst.ParticleOrdinal.CompareTo(yBurst.ParticleOrdinal);
        }
        return result;
    }
}
