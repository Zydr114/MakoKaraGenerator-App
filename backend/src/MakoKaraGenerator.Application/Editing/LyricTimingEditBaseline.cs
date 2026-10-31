using MakoKaraGenerator.Core.Time;

namespace MakoKaraGenerator.Application.Editing;

/// <summary>时间操作捕获的工作区与稳定目标基线；代次仅在同一工作区身份内有效。</summary>
/// <param name="ExpectedWorkspace">捕获输入的原工作区对象。</param>
/// <param name="ExpectedWorkspaceGeneration">该工作区的新建／打开／恢复代次，不替代宿主会话修订。</param>
/// <param name="Target">明确分支与稳定行／元素 ID。</param>
/// <param name="ExpectedTime">捕获时的目标时间；未定时时为空。</param>
public sealed record LyricTimingEditBaseline(
    ProjectWorkspace ExpectedWorkspace,
    long ExpectedWorkspaceGeneration,
    LyricTimingEditTarget Target,
    TimeRange? ExpectedTime);
