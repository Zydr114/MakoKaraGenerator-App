using System.Text.Json.Serialization;

namespace MakoKaraGenerator.Backend.Contracts;

/// <summary>固定授权歌词资源再次导入。</summary>
[JsonConverter(typeof(ProjectCommandJsonConverter))]
public sealed record ImportAssetCommandDto(string? AssetId, LyricsImportDestinationDto? Destination, LyricsImportOptionsDto? Options = null)
    : ProjectCommandDto("lyrics.importAsset");

/// <summary>向明确来源追加完整文本；新翻译来源使用 FollowPrimary。</summary>
[JsonConverter(typeof(ProjectCommandJsonConverter))]
public sealed record AppendTextCommandDto(string? Text, LyricsImportDestinationDto? Destination)
    : ProjectCommandDto("lyrics.appendText");

/// <summary>导入或追加的显式目标；具体字段由严格判别联合限定。</summary>
[JsonConverter(typeof(LyricsImportDestinationJsonConverter))]
public abstract record LyricsImportDestinationDto(string Kind);
/// <summary>已有来源的完整替换或追加目标。</summary>
public sealed record ExistingLyricsDestinationDto(string? BranchId) : LyricsImportDestinationDto("existing");
/// <summary>文件导入创建新的独立时间来源。</summary>
public sealed record NewBranchLyricsDestinationDto(string? Name, ProjectLyricBranchRole Role) : LyricsImportDestinationDto("newBranch");
/// <summary>粘贴创建新的翻译来源。</summary>
public sealed record NewTranslationLyricsDestinationDto(string? Name) : LyricsImportDestinationDto("newTranslation");
/// <summary>把解析文档中的全部分支追加为新来源；不替换工程里已有的来源。</summary>
public sealed record AllSourcesLyricsDestinationDto() : LyricsImportDestinationDto("allSources");
/// <summary>格式、编码与完整共享切分选项；未知导入器或编码沿原自动回退。</summary>
public sealed record LyricsImportOptionsDto(string? ImporterId = null, string? EncodingName = null,
    LyricsImportSplitRule SplitRule = LyricsImportSplitRule.None, bool EstimateSplitTimes = false, string? SourceBranchId = null);

/// <summary>导入或追加的实际行数与稳定选择；只有文件导入携带完整报告。</summary>
public sealed record LyricsImportCommandResultDto(int LineCount, string SelectedBranchId, string SelectedLineId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] LyricsImportReportDto? Report = null);

/// <summary>原共享导入报告的完整投影；偏移微秒使用十进制字符串。</summary>
public sealed record LyricsImportReportDto(string ImporterId, string ImporterName, string SourceFormat,
    int SourceLineCount, int LineCount, int TokenCount, int TimedTokenCount, int MissingTokenCount, int EstimatedTokenCount,
    int SkippedLineCount, int MergedContinuationCount, int DerivedLineEndCount, int FallbackLineEndCount,
    string OffsetUs, string EncodingName, bool HadByteOrderMark, LyricsImportNewLineKind NewLineKind,
    LyricsImportIssueDto[] Issues, string? DetectionReason, string[] CandidateNames, bool IsFormatAmbiguous,
    string[] DiscardedFeatures, int SplitLineCount, int SplitCreatedTokenCount, int SplitEstimatedTokenCount,
    int IssueCount, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] LyricsImportIssueSeverity? MaxSeverity);
/// <summary>完整原始诊断；源行号零表示不适用。</summary>
public sealed record LyricsImportIssueDto(LyricsImportIssueKindDto Kind, LyricsImportIssueSeverity Severity,
    int SourceLineNumber, string RawText, string Message);

/// <summary>共享导入语义的命名枚举。</summary>
public enum LyricsImportSplitRule
{
    /// <summary>不切分：保持格式原有的元素粒度。</summary>
    None = 0,

    /// <summary>混合（默认推荐）：CJK 逐字，拉丁字母与数字按词，标点与空白依附前一个元素。</summary>
    Mixed = 1,

    /// <summary>每个 Unicode 文本元素一个元素（拉丁字母也逐字母；与手动拆分的手感一致）。</summary>
    PerTextElement = 2,

    /// <summary>按词：连续的拉丁字母/数字算一个元素，连续的 CJK 也算一个元素（适合"整句一元素"）。</summary>
    LatinWords = 3,

    /// <summary>按标点：遇到标点后在下一个元素处切（逗号、句号、顿号等）。</summary>
    ByPunctuation = 4,

    /// <summary>按空白：遇到空白后在下一个元素处切。</summary>
    ByWhitespace = 5,
}


/// <summary>共享导入语义的命名枚举。</summary>
public enum LyricsImportNewLineKind
{
    /// <summary>文本里没有换行符。</summary>
    None = 0,

    /// <summary><c>\n</c>（Unix）。</summary>
    LineFeed = 1,

    /// <summary><c>\r\n</c>（Windows）。</summary>
    CarriageReturnLineFeed = 2,

    /// <summary><c>\r</c>（老式 Mac）。</summary>
    CarriageReturn = 3,

    /// <summary>多种换行符混用。</summary>
    Mixed = 4,
}


/// <summary>共享导入语义的命名枚举。</summary>
public enum LyricsImportIssueKindDto
{
    /// <summary>标签无法识别，已忽略；原文保留在问题记录里。</summary>
    UnknownTag = 0,

    /// <summary>标签能识别成形，但内容无法解析（例如时间格式不对）。</summary>
    MalformedTag = 1,

    /// <summary>有文本但没有时间标签，无法定位。</summary>
    TextWithoutTimestamp = 2,

    /// <summary>有时间标签但没有文本，已跳过。</summary>
    EmptyText = 3,

    /// <summary>同一个时间戳出现多次。</summary>
    DuplicateTimestamp = 4,

    /// <summary>原文件的时间顺序与行顺序不一致。</summary>
    OutOfOrder = 5,

    /// <summary>偏移后时间为负，已钳制到 0。</summary>
    NegativeTimeClamped = 6,

    /// <summary>逐字时间标签落在所属行的时间范围之外，已钳制进行范围。</summary>
    WordTimeOutsideLine = 7,

    /// <summary>逐字时间标签的顺序不是递增的。</summary>
    WordTimesNotIncreasing = 8,

    /// <summary>文件的编码不是 UTF-8 且无法确定，已按近似方式解码。</summary>
    EncodingAssumed = 9,

    /// <summary>行结束时间无法由下一行推导，使用了兜底时长。</summary>
    LineEndFallback = 10,

    /// <summary>来源格式里有信息本格式表达不了，已丢弃（音高、翻译、和声、样式标签等）。</summary>
    DiscardedFeature = 11,

    /// <summary>多个导入器都能处理这个文件，已按优先级选择；理由写在消息里。</summary>
    FormatAmbiguous = 12,

    /// <summary>导入后按规则重新切分了文本元素（见 原业务值）。</summary>
    TokenSplitApplied = 13,

    /// <summary>解析出的元素文本拼起来与行文本不一致，已按文本元素重建（时间置为缺失）。</summary>
    TokenTextMismatch = 14,
}


/// <summary>共享导入语义的命名枚举。</summary>
public enum LyricsImportIssueSeverity
{
    /// <summary>提示：不影响输出，只是建议。</summary>
    Info = 0,

    /// <summary>警告：允许继续，但可能产生非预期结果。</summary>
    Warning = 1,

    /// <summary>错误：无法继续导出。</summary>
    Error = 2,
}
