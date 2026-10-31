using MakoKaraGenerator.Core.Lyrics;

namespace MakoKaraGenerator.Application.Editing;

/// <summary>歌词编辑的稳定目标；整行操作不携带元素标识。</summary>
/// <param name="BranchId">明确的歌词来源标识，不隐式改变主来源。</param>
/// <param name="LineId">来源中的行标识。</param>
/// <param name="TokenId">注音所属或拆分／合并的基础元素标识；整行操作为空。</param>
public sealed record LyricContentEditTarget(string BranchId, string LineId, string? TokenId = null);

/// <summary>即时正文输入；只以原正文为输入基线，保留当前未改区域的领域数据。</summary>
/// <param name="ExpectedWorkspace">输入捕获时的工作区实例。</param>
/// <param name="ExpectedWorkspaceGeneration">输入捕获时的工作区代次。</param>
/// <param name="Target">明确的行目标。</param>
/// <param name="ExpectedText">捕获时的正文，必须仍与目标相同。</param>
/// <param name="Text">新正文；为空时按原编辑语义使用空字符串。</param>
/// <remarks>工作区引用仅用于进程内身份门禁；宿主须自行解析会话与修订，客户端负责输入法和焦点生命周期。</remarks>
public sealed record LyricLineTextEditRequest(
    ProjectWorkspace ExpectedWorkspace, long ExpectedWorkspaceGeneration,
    LyricContentEditTarget Target, string ExpectedText, string? Text);

/// <summary>基础元素的注音输入；完整原注音记录用于拒绝旧输入。</summary>
/// <param name="ExpectedWorkspace">输入捕获时的工作区实例。</param>
/// <param name="ExpectedWorkspaceGeneration">输入捕获时的工作区代次。</param>
/// <param name="Target">明确的行与基础元素目标。</param>
/// <param name="ExpectedRuby">捕获时的第一条对应注音；不存在时为空。</param>
/// <param name="Text">新注音文字；空字符串删除该基础元素的注音。</param>
public sealed record LyricRubyTextEditRequest(
    ProjectWorkspace ExpectedWorkspace, long ExpectedWorkspaceGeneration,
    LyricContentEditTarget Target, RubyAnnotation? ExpectedRuby, string Text);

/// <summary>行结构、平移或元素结构操作的输入基线，完整原行记录防止消费旧选择。</summary>
/// <param name="ExpectedWorkspace">捕获时的工作区实例。</param>
/// <param name="ExpectedWorkspaceGeneration">捕获时的工作区代次。</param>
/// <param name="Target">明确的行目标。</param>
/// <param name="ExpectedLine">捕获时的完整行记录。</param>
public sealed record LyricContentEditBaseline(
    ProjectWorkspace ExpectedWorkspace, long ExpectedWorkspaceGeneration,
    LyricContentEditTarget Target, LyricLine ExpectedLine);

/// <summary>在稳定行之后新增空行；没有选中行时追加到指定来源末尾。</summary>
/// <param name="ExpectedWorkspace">捕获时的工作区实例。</param>
/// <param name="ExpectedWorkspaceGeneration">捕获时的工作区代次。</param>
/// <param name="BranchId">明确的歌词来源标识。</param>
/// <param name="AfterLineId">前一行的稳定标识；为空时追加。</param>
/// <param name="ExpectedAfterLine">捕获时的前一行记录；指定前一行时必需。</param>
public sealed record LyricLineInsertRequest(
    ProjectWorkspace ExpectedWorkspace, long ExpectedWorkspaceGeneration,
    string BranchId, string? AfterLineId, LyricLine? ExpectedAfterLine = null);
