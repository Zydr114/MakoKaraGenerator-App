using System.Collections.Immutable;
using MakoKaraGenerator.Core.Time;

namespace MakoKaraGenerator.Application.Editing;

/// <summary>一项按稳定 ID 寻址的元素时间变化，不包含时间轴下标。</summary>
/// <param name="Target">明确分支、行与元素 ID；必须是元素目标。</param>
/// <param name="ExpectedTime">批次开始前捕获的时间。</param>
/// <param name="StartUs">精确的整数微秒起点。</param>
/// <param name="EndUs">精确的整数微秒终点。</param>
public sealed record LyricTimingTokenTimeChange(
    LyricTimingEditTarget Target, TimeRange? ExpectedTime, long StartUs, long EndUs);

/// <summary>原时间轴批次的原子请求；全部目标先校验基线，再按输入顺序校验领域约束。</summary>
/// <param name="ExpectedWorkspace">捕获输入的原工作区对象。</param>
/// <param name="ExpectedWorkspaceGeneration">该工作区的代次。</param>
/// <param name="Changes">不可变的元素端点批次；空批次接受但不写入。</param>
/// <remarks>保留原批次入口的媒体边界语义：只执行歌词领域与展示校验，不另加媒体时长上限。</remarks>
public sealed record LyricTimingTokenBatchRequest(
    ProjectWorkspace ExpectedWorkspace,
    long ExpectedWorkspaceGeneration,
    ImmutableArray<LyricTimingTokenTimeChange> Changes);
