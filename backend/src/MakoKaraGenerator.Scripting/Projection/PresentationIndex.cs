using System.Collections.Immutable;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Scripting.V2;

namespace MakoKaraGenerator.Scripting.Projection;

/// <summary>一个真实装载的退出边界；不添加源事件、演唱 occurrence 或播放头。</summary>
/// <param name="Binding">隐藏左侧的真实装载及源父路径。</param>
/// <param name="HideTimeUs">半开可见区间的隐藏边界。</param>
/// <param name="LoadOrdinal">固定事件表内的装载顺序。</param>
public sealed record ExitReplicaOccurrence(FrameLine Binding, long HideTimeUs, int LoadOrdinal);

/// <summary>从固定装载事件建立随机访问退出索引；查询只检查最长三秒窗口，不从零重放。</summary>
public sealed class PresentationIndex
{
    private readonly ImmutableArray<ExitReplicaOccurrence> _exits;
    /// <summary>固定真实隐藏边界；有界索引最多容纳二十万个装载。</summary>
    public PresentationIndex(TimelineProjection timeline, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        token.ThrowIfCancellationRequested();
        const int maximumOccurrences = 200_000;
        Dictionary<BindingId, (long TimeUs, int Ordinal)> enters = [];
        List<BindingLeft> exits = [];
        foreach (var value in timeline.Events)
        {
            token.ThrowIfCancellationRequested();
            if (value is BindingEntered entered)
            {
                if (enters.Count == maximumOccurrences)
                    throw new PreparedFrameException("PresentationIndexBudgetExceeded", "装载退出索引超过二十万个边界。");
                if (!enters.TryAdd(entered.Binding.BindingId, (entered.TimeUs, enters.Count)))
                    throw new PreparedFrameException("InvalidPresentationIndex", "真实装载不能拥有重复进入边界。");
            }
            if (value is BindingLeft left)
            {
                if (exits.Count == maximumOccurrences)
                    throw new PreparedFrameException("PresentationIndexBudgetExceeded", "装载退出索引超过二十万个边界。");
                exits.Add(left);
            }
        }
        exits.Sort(static (left, right) => left.TimeUs != right.TimeUs ? left.TimeUs.CompareTo(right.TimeUs)
            : StringComparer.Ordinal.Compare(left.Binding.BindingId.Value, right.Binding.BindingId.Value));
        HashSet<BindingId> seen = [];
        var indexed = ImmutableArray.CreateBuilder<ExitReplicaOccurrence>(exits.Count);
        foreach (var value in exits)
        {
            token.ThrowIfCancellationRequested();
            if (value.TimeUs <= 0 || !enters.TryGetValue(value.Binding.BindingId, out var enter) || enter.TimeUs >= value.TimeUs
                || !seen.Add(value.Binding.BindingId))
                throw new PreparedFrameException("InvalidPresentationIndex", "退出边界必须对应已进入的真实装载。");
            indexed.Add(new(value.Binding, value.TimeUs, enter.Ordinal));
        }
        _exits = indexed.MoveToImmutable();
    }

    /// <summary>查询 [hide, hide+duration) 内的真实副本；零寿命返回空，超三秒明确拒绝。</summary>
    public ImmutableArray<ExitReplicaOccurrence> Query(long timeUs, long maximumExitDurationUs,
        Func<FrameLine, bool>? eligible = null, CancellationToken token = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(timeUs);
        if (maximumExitDurationUs is < 0 or > 3_000_000) throw new ArgumentOutOfRangeException(nameof(maximumExitDurationUs));
        token.ThrowIfCancellationRequested();
        if (maximumExitDurationUs == 0) return [];
        int start = UpperBound(timeUs - maximumExitDurationUs), end = UpperBound(timeUs);
        var result = ImmutableArray.CreateBuilder<ExitReplicaOccurrence>();
        for (int i = start; i < end; i++)
        {
            token.ThrowIfCancellationRequested();
            if (eligible is null || eligible(_exits[i].Binding))
            {
                result.Add(_exits[i]);
                if (result.Count > 128)
                    throw new PreparedFrameException("ExitReplicaBudgetExceeded", "同时存在的退出副本超过一百二十八个。")
                        { BindingId = _exits[i].Binding.BindingId };
            }
        }
        return result.ToImmutable();
    }

    /// <summary>为每个实例按自己的寿命投影相位；短实例到终点后保持 1，零声明实例为 1。</summary>
    public static ImmutableArray<TextLineLoad> LineLoads(SceneFrame scene, ImmutableArray<FrameLine> lines, long exitDurationUs)
    {
        ArgumentNullException.ThrowIfNull(scene);
        if (exitDurationUs is < 0 or > 3_000_000) throw new ArgumentOutOfRangeException(nameof(exitDurationUs));
        var samples = scene.Lines.ToDictionary(static value => value.BindingId!, StringComparer.Ordinal);
        return [.. lines.Select(line =>
        {
            if (!samples.TryGetValue(line.BindingId.Value, out var sample))
                throw new PreparedFrameException("InvalidSceneBinding", "文字相位缺少实际装载。");
            return sample.ExitReplica is not { } exit ? new TextLineLoad(line)
                : new TextLineLoad(line, TextLinePhase.ExitReplica, exit.HideTimeUs, exitDurationUs,
                    exitDurationUs == 0 ? 1 : Math.Clamp((double)(scene.TimeUs - exit.HideTimeUs) / exitDurationUs, 0, 1));
        })];
    }

    private int UpperBound(long timeUs)
    {
        int low = 0, high = _exits.Length;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            if (_exits[middle].HideTimeUs <= timeUs) low = middle + 1; else high = middle;
        }
        return low;
    }
}
