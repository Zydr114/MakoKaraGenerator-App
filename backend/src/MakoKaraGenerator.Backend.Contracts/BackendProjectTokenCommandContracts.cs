using System.Text.Json;
using System.Text.Json.Serialization;

namespace MakoKaraGenerator.Backend.Contracts;

/// <summary>命令解码已超过公开的段数预算；须映射为资源超限而非形状错误。</summary>
public sealed class ProjectCommandBudgetExceededException : JsonException
{
    /// <summary>创建一个在字符串数组分配之前触发的预算错误。</summary>
    public ProjectCommandBudgetExceededException() : base("segments 超过单次操作允许的 20000 项预算。") { }
}

/// <summary>单次元素结构操作的宿主预算，不是歌曲、桌面或持久化格式上限。</summary>
public static class TokenEditingLimits
{
    /// <summary>手工或规则重切的单行最大段数，最终仍须通过完整行读预算。</summary>
    public const int MaxSegments = ProjectReadModelLimits.MaxItems;
    /// <summary>整个来源操作累计改变行的完整单行投影预算。</summary>
    public const long MaxPreparedBranchBytes = 16 * 1024 * 1024;
}

/// <summary>沿 Core 保留的六个切分规则；wire 只接受对应的字符串名称。</summary>
public enum ProjectTokenSplitRule
{
    /// <summary>保持原粒度。</summary>
    None,
    /// <summary>CJK 逐字、拉丁按词的混合规则。</summary>
    Mixed,
    /// <summary>按完整 Unicode 文本元素。</summary>
    PerTextElement,
    /// <summary>按连续文字或数字。</summary>
    LatinWords,
    /// <summary>按标点之后的边界。</summary>
    ByPunctuation,
    /// <summary>按空白之后的边界。</summary>
    ByWhitespace,
}

/// <summary>将已有有效时间的元素按 Unicode 中点拆分。</summary>
[JsonConverter(typeof(ProjectCommandJsonConverter))]
public sealed record SplitTokenCommandDto(LyricTimingTargetDto? Target) : ProjectCommandDto("lyrics.splitToken", Target);

/// <summary>将元素与同一行的相邻后元素合并。</summary>
[JsonConverter(typeof(ProjectCommandJsonConverter))]
public sealed record MergeTokenCommandDto(LyricTimingTargetDto? Target) : ProjectCommandDto("lyrics.mergeToken", Target);

/// <summary>移动稳定元素到精确修订下的最终位置；重分时间槽须显式选择。</summary>
[JsonConverter(typeof(ProjectCommandJsonConverter))]
public sealed record ReorderTokenCommandDto(LyricTimingTargetDto? Target, int? DestinationIndex, bool? ReassignTimeSlots)
    : ProjectCommandDto("lyrics.reorderToken", Target);

/// <summary>提交完整 Unicode 边界的手工段序列。</summary>
[JsonConverter(typeof(ProjectCommandJsonConverter))]
public sealed record ResplitLineCommandDto(LyricTimingTargetDto? Target, string[]? Segments, bool? EstimateTimes)
    : ProjectCommandDto("lyrics.resplitLine", Target);

/// <summary>原子重切一个明确来源；规则与估算选择均显式提供。</summary>
[JsonConverter(typeof(ProjectCommandJsonConverter))]
public sealed record ResplitBranchCommandDto(string? BranchId, ProjectTokenSplitRule? Rule, bool? EstimateTimes)
    : ProjectCommandDto("lyrics.resplitBranch");

/// <summary>元素操作的建议选择；省略整个选择表示保留客户端选择。</summary>
public sealed record LyricTokenSelectionDto(string BranchId, string LineId, bool HasTokenSelection,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TokenId = null);

/// <summary>原用例的真实改变行数、选择与切分计数；不是完整身份差集。</summary>
public sealed record LyricTokenCommandResultDto(int ChangedLineCount,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] LyricTokenSelectionDto? Selection = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? PreservedTokenCount = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? CreatedTokenCount = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? EstimatedTokenCount = null);
