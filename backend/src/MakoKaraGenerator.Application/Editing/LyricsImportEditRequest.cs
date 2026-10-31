using MakoKaraGenerator.Core.Lyrics;

namespace MakoKaraGenerator.Application.Editing;

/// <summary>格式、编码及导入后切分选择，不包含客户端目标或文件系统路径。</summary>
public sealed record LyricsImportOptions
{
    /// <summary>强制导入器标识；空或未知标识沿原规则自动识别。</summary>
    public string? ImporterId { get; init; }
    /// <summary>再次导入多来源文档时显式选择的源分支；新建工程仍保留完整文档。</summary>
    public string? SourceBranchId { get; init; }
    /// <summary>强制编码名；支持 UTF-8、GB18030，空或未知值沿原规则自动检测。</summary>
    public string? EncodingName { get; init; }
    /// <summary>导入后的元素重切规则，默认保留格式原有粒度。</summary>
    public TokenSplitRule SplitRule { get; init; } = TokenSplitRule.None;
    /// <summary>是否明确要求按比例估算重切元素时间，默认不估算。</summary>
    public bool EstimateSplitTimes { get; init; }
}

/// <summary>来源替换或追加的原值门禁；新来源使用完整原歌词文档门禁。</summary>
/// <param name="ExpectedWorkspace">输入捕获时的工作区实例。</param>
/// <param name="ExpectedWorkspaceGeneration">输入捕获时的工作区代次。</param>
/// <param name="BranchId">已有目标来源标识；新来源为空。</param>
/// <param name="ExpectedBranch">已有目标来源的完整原记录；新来源为空。</param>
/// <param name="ExpectedLyrics">新来源创建前的完整原歌词；已有来源为空。</param>
/// <remarks>工作区所有者同步提交；客户端另行管理会话、异步操作与取消生命周期。</remarks>
public sealed record LyricsImportEditBaseline(
    ProjectWorkspace ExpectedWorkspace, long ExpectedWorkspaceGeneration, string? BranchId,
    LyricBranch? ExpectedBranch = null, LyricsDocument? ExpectedLyrics = null);
