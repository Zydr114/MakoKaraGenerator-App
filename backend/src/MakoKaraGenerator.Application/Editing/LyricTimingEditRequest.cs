using MakoKaraGenerator.Core.Time;

namespace MakoKaraGenerator.Application.Editing;

/// <summary>一次完整时间组提交的输入；时间均为整数微秒，不包含客户端原文或输入法状态。</summary>
/// <param name="ExpectedWorkspace">捕获输入时所属的工作区实例；与代次一起限定同一编辑会话。</param>
/// <param name="ExpectedWorkspaceGeneration">捕获输入时的工作区代次，防止换工程后复用旧目标。</param>
/// <param name="Target">明确的分支、行与可选元素目标。</param>
/// <param name="ExpectedTime">捕获输入时的原时间；提交时必须仍与目标时间相同。</param>
/// <param name="StartUs">新起点；为空时保留原时间的精确起点。</param>
/// <param name="EndUs">新终点；为空时保留原时间的精确终点。</param>
/// <param name="ContentDurationUs">已准备的媒体时长；小于或等于零表示时长未知，不施加媒体终点上限。</param>
/// <remarks>
/// 没有原时间时必须提供两个端点；请求不修正、截断或换算任何端点。
/// 工作区引用仅用于进程内身份门禁，不能作为传输对象；独立宿主须先解析自己的会话与修订。
/// </remarks>
public sealed record LyricTimingEditRequest(
    ProjectWorkspace ExpectedWorkspace,
    long ExpectedWorkspaceGeneration,
    LyricTimingEditTarget Target,
    TimeRange? ExpectedTime,
    long? StartUs,
    long? EndUs,
    long ContentDurationUs = 0);
