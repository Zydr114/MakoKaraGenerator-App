using System.Text.Json.Serialization;

namespace MakoKaraGenerator.Backend.Contracts;

/// <summary>连打、断合行、非破坏性注音与全文快编命令的固定宿主预算。</summary>
public static class LyricRefactorEditingLimits
{
    /// <summary>全文快编单次最多接受的行数。</summary>
    public const int MaxDiffLines = 20000;
    /// <summary>单次非破坏性注音分段数组上限。</summary>
    public const int MaxRubySegments = 20000;
    /// <summary>单条命令意图的最大 UTF-8 JSON 字节数。</summary>
    public const int MaxIntentBytes = 1024 * 1024;
    /// <summary>所有受影响行的完整投影累计上限。</summary>
    public const int MaxPreparedBytes = 16 * 1024 * 1024;
}

/// <summary>拍点目标：省略 rubyId 与 segmentId 表示基础词元；两者同时提供表示注音分段。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LyricBeatTargetDto(
    string BranchId,
    string LineId,
    string TokenId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RubyId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SegmentId = null);

/// <summary>原子右手连打（J/K）：可选闭合上一拍，同时开唱当前拍，并自动初始化或外扩行与多拍父词元包络。</summary>
[JsonConverter(typeof(ProjectCommandJsonConverter))]
public sealed record StrikeBeatCommandDto(
    string? BranchId,
    LyricBeatTargetDto? CloseTarget,
    LyricBeatTargetDto? OpenTarget,
    string? PlayheadUs,
    string? CloseGuardGapUs = null,
    string? FallbackDurationUs = null)
    : ProjectCommandDto("lyrics.strikeBeat");

/// <summary>原子显式收尾（L）：定格目标拍点终点并外扩父词元与行尾包络。</summary>
[JsonConverter(typeof(ProjectCommandJsonConverter))]
public sealed record CloseBeatCommandDto(
    LyricBeatTargetDto? BeatTarget,
    string? PlayheadUs)
    : ProjectCommandDto("lyrics.closeBeat");

/// <summary>翻转实义词元的句中收尾提示覆写值（auto ↔ !auto）。</summary>
[JsonConverter(typeof(ProjectCommandJsonConverter))]
public sealed record TogglePhraseEndHintCommandDto(LyricTimingTargetDto? Target)
    : ProjectCommandDto("lyrics.togglePhraseEndHint", Target);

/// <summary>在指定 tokenIndex 之前将一行拆分为两行。</summary>
[JsonConverter(typeof(ProjectCommandJsonConverter))]
public sealed record SplitLineCommandDto(LyricTimingTargetDto? Target, int? TokenIndex)
    : ProjectCommandDto("lyrics.splitLine", Target);

/// <summary>将当前行与上一行合并为一行。</summary>
[JsonConverter(typeof(ProjectCommandJsonConverter))]
public sealed record MergeLinesCommandDto(LyricTimingTargetDto? Target)
    : ProjectCommandDto("lyrics.mergeLines", Target);

/// <summary>非破坏性按段序号更新基础词元的注音分段列表；空数组删除该词元的注音。</summary>
[JsonConverter(typeof(ProjectCommandJsonConverter))]
public sealed record SetRubySegmentsCommandDto(LyricTimingTargetDto? Target, string[]? Segments)
    : ProjectCommandDto("lyrics.setRubySegments", Target);

/// <summary>全文快编单行输入；可选绑定既有稳定源行 ID。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TextDiffLineDto(
    string Text,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SourceLineId = null);

/// <summary>全文快编多行无损 Diff 应用命令。</summary>
[JsonConverter(typeof(ProjectCommandJsonConverter))]
public sealed record ApplyTextDiffCommandDto(string? BranchId, TextDiffLineDto[]? Lines)
    : ProjectCommandDto("lyrics.applyTextDiff");

/// <summary>原子清除单行全部时间戳（默认连同行起止时间、词元与多拍注音一并清零；keepLineTime=true 时保留行起止时间）。</summary>
[JsonConverter(typeof(ProjectCommandJsonConverter))]
public sealed record ClearLineTimingCommandDto(LyricTimingTargetDto? Target, bool? KeepLineTime = null)
    : ProjectCommandDto("lyrics.clearLineTiming", Target);

/// <summary>原子清除指定来源下所有歌词行、词元与注音分段的时间戳，完整保留文本、分段与注音结构。</summary>
[JsonConverter(typeof(ProjectCommandJsonConverter))]
public sealed record ClearBranchTimingCommandDto(string? BranchId)
    : ProjectCommandDto("lyrics.clearBranchTiming");
