using System.Collections.Immutable;
using System.Numerics;
using MakoKaraGenerator.Scripting.V2;

namespace MakoKaraGenerator.Scripting.Particles.V2;

/// <summary>仅保存有限轨道、源区段和精确前缀的无历史粒子计划。</summary>
public sealed partial class CompiledParticleSchedule
{
    private readonly ParticleOwner _owner;
    private readonly int _maximumBits;
    private readonly Episode[] _episodes;
    private readonly Burst[] _bursts;

    private CompiledParticleSchedule(ParticleOwner owner, int maximumBits, Episode[] episodes, Burst[] bursts)
    { _owner = owner; _maximumBits = maximumBits; _episodes = episodes; _bursts = bursts; }

    /// <summary>固定所有者，可供宿主对本请求的存活与帧内存扣款。</summary>
    public ParticleOwner Owner => _owner;

    /// <summary>编译原始速率前缀和有限 episode；不生成任何历史粒子或查询最终几何。</summary>
    public static CompiledParticleSchedule Compile(ParticleSchedule plan, EmissionSourceCatalog sources,
        ParticleOwner owner, ParticlePrepareBudget? budget = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan); ArgumentNullException.ThrowIfNull(sources); ArgumentNullException.ThrowIfNull(owner);
        budget ??= new(); cancellationToken.ThrowIfCancellationRequested();
        if (!owner.InstanceId.IsValid || string.IsNullOrWhiteSpace(owner.RuntimeHash))
            throw new ArgumentException("粒子计划需要实际宿主所有者。", nameof(owner));
        ValidatePlan(plan, sources, owner, budget, cancellationToken);
        var context = new ExactParticleContext(owner, budget.MaximumBits, cancellationToken);
        Dictionary<EmitterId, CursorEmissionPlan> continuous = plan.Continuous.ToDictionary(static item => item.Id);
        Dictionary<EmitterId, CompiledRateTrack> rates = [];
        foreach (var item in plan.Continuous)
        { cancellationToken.ThrowIfCancellationRequested(); rates.Add(item.Id, new(item.Rate, context)); }
        List<Episode> episodes = [];
        foreach (var grouping in sources.Continuous.GroupBy(static item => (item.EmitterId, item.Head))
            .OrderBy(static group => group.Key.EmitterId.Value, StringComparer.Ordinal)
            .ThenBy(static group => ParticleIdentityEncoding.Head(group.Key.Head), StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!continuous.TryGetValue(grouping.Key.EmitterId, out var emitter))
                throw Invalid(owner, "来源目录引用未声明的连续发射器。");
            var intervals = grouping.OrderBy(static item => item.Range.StartUs).ThenBy(static item => item.Range.EndUs).ToArray();
            for (int index = 0; index < intervals.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested(); ValidateSource(intervals[index], emitter, owner);
                if (index > 0 && intervals[index].Range.StartUs < intervals[index - 1].Range.EndUs)
                    throw owner.Failure("PrepareParticles", 0, "EmissionHeadAmbiguous", "同一 track／Binding 在同刻含多个 Current 身份。");
            }
            var enabled = MergeEnabled(emitter.Rate.EnabledRanges);
            int first = 0, window = 0;
            while (first < intervals.Length && window < enabled.Count)
            {
                cancellationToken.ThrowIfCancellationRequested(); int last = first;
                while (last + 1 < intervals.Length && intervals[last + 1].Range.StartUs == intervals[last].Range.EndUs) last++;
                long start = intervals[first].Range.StartUs, end = intervals[last].Range.EndUs;
                while (window < enabled.Count && enabled[window].EndUs <= start) window++;
                int selected = window;
                while (selected < enabled.Count && enabled[selected].StartUs < end)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var range = new TimeRange(Math.Max(start, enabled[selected].StartUs), Math.Min(end, enabled[selected].EndUs));
                    budget.Reserve(owner, episodes: 1); budget.ReserveBytes(owner, 2048);
                    var rate = rates[emitter.Id]; var prefix = rate.IntegralAt(range.StartUs, context);
                    var total = rate.IntegralAt(range.EndUs - 1, context).Subtract(prefix, context).Floor;
                    context.Check(total);
                    if (total.Sign < 0 || total > ulong.MaxValue)
                        throw owner.Failure("PrepareParticles", 0, "ParticleTimeOrIdentityOverflow", "连续出生序号超过 UInt64。");
                    var episode = new Episode(emitter, grouping.Key.Head, range, rate, prefix, (ulong)total, intervals);
                    if (episode.Total != 0)
                    {
                        long finalBirth = episode.Solve(episode.Total, context);
                        if (finalBirth > long.MaxValue - emitter.Particle.LifetimeUs)
                            throw owner.Failure("PrepareParticles", finalBirth, "ParticleTimeOrIdentityOverflow", "粒子寿命末端超过 Int64。");
                    }
                    episodes.Add(episode); selected++;
                }
                first = last + 1;
            }
        }
        var bursts = CompileBursts(plan, sources, owner, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return new(owner, budget.MaximumBits, episodes.OrderBy(static item => item.Range.StartUs)
            .ThenBy(static item => item.Plan.Id.Value, StringComparer.Ordinal)
            .ThenBy(static item => item.HeadOrder, StringComparer.Ordinal).ToArray(), bursts);
    }

    private sealed class Episode(CursorEmissionPlan plan, ParticleHead head, TimeRange range, CompiledRateTrack rate,
        ExactQuantity prefix, ulong total, EmissionSourceInterval[] sources)
    {
        internal CursorEmissionPlan Plan { get; } = plan;
        internal ParticleHead Head { get; } = head;
        internal string HeadOrder { get; } = ParticleIdentityEncoding.Head(head);
        internal TimeRange Range { get; } = range;
        internal ulong Total { get; } = total;
        internal BigInteger Count(long timeUs, ExactParticleContext context)
            => timeUs < Range.StartUs ? BigInteger.Zero
                : rate.IntegralAt(Math.Min(timeUs, Range.EndUs - 1), context).Subtract(prefix, context).Floor;
        internal long Solve(ulong sequence, ExactParticleContext context)
        {
            long low = Range.StartUs, high = Range.EndUs - 1;
            while (low < high)
            {
                context.Inversion(); long middle = low + (high - low) / 2;
                if (rate.IntegralAt(middle, context).Subtract(prefix, context).AtLeast(sequence, context)) high = middle;
                else low = middle + 1;
            }
            return low;
        }
        internal ParticleSource SourceAt(long timeUs, ExactParticleContext context)
        {
            int low = 0, high = sources.Length;
            while (low < high)
            {
                context.Cancel(); int middle = low + (high - low) / 2;
                if (sources[middle].Range.StartUs <= timeUs) low = middle + 1; else high = middle;
            }
            if (low == 0 || !sources[low - 1].Range.Contains(timeUs)) throw Invalid(context.Owner, "出生时刻没有真实 Current 来源。");
            return sources[low - 1].Source;
        }
    }

    private sealed record Burst(EmitterId Emitter, int PlanOrdinal, TimelineEvent Event, ParticleSource Source,
        BurstEventRow Row, CursorAnchorMode AnchorMode);

    private static List<TimeRange> MergeEnabled(ImmutableArray<TimeRange> enabled)
    {
        List<TimeRange> result = [];
        foreach (var range in enabled)
        {
            if (result.Count != 0 && result[^1].EndUs == range.StartUs) result[^1] = new(result[^1].StartUs, range.EndUs);
            else result.Add(range);
        }
        return result;
    }
    private static MakoKaraGenerator.Rendering.EffectFailureException Invalid(ParticleOwner owner, string message)
        => owner.Failure("PrepareParticles", 0, "InvalidParticleSource", message);
}
