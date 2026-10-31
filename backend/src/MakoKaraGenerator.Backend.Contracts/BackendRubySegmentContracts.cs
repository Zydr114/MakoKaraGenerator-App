using System.Text.Json.Serialization;

namespace MakoKaraGenerator.Backend.Contracts;

/// <summary>独立注音编辑的固定宿主预算。</summary>
public static class RubySegmentEditingLimits
{
    /// <summary>含父链的原子时间批次上限。</summary>
    public const int MaxChanges = 1000;
    /// <summary>完整命令意图最大 UTF-8 JSON 字节数。</summary>
    public const int MaxIntentBytes = 64 * 1024;
    /// <summary>批次所有最终改变行的完整投影累计上限。</summary>
    public const int MaxPreparedBytes = 16 * 1024 * 1024;
}
/// <summary>注音组的完整稳定父链。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RubyGroupTargetDto(string BranchId, string LineId, string TokenId, string RubyId);
/// <summary>独立注音片段的完整稳定父链；不扩展旧 token target。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RubySegmentTargetDto(string BranchId, string LineId, string TokenId, string RubyId, string SegmentId);
/// <summary>设置一个注音片段的完整绝对微秒区间。</summary>
public sealed record SetRubySegmentTimeCommandDto(RubySegmentTargetDto? RubyTarget, string? SegmentStartUs, string? SegmentEndUs)
    : ProjectCommandDto("lyrics.setRubySegmentTime");
/// <summary>清除一个注音片段的时间与来源状态。</summary>
public sealed record ClearRubySegmentTimeCommandDto(RubySegmentTargetDto? RubyTarget) : ProjectCommandDto("lyrics.clearRubySegmentTime");
/// <summary>仅修改一个片段正文，保留其身份与时间；空正文拒绝。</summary>
public sealed record SetRubySegmentTextCommandDto(RubySegmentTargetDto? RubyTarget, string? Text) : ProjectCommandDto("lyrics.setRubySegmentText");
/// <summary>在已有注音组追加或插入一个无时间片段；afterSegmentId 必填且可为 null。</summary>
public sealed record AddRubySegmentCommandDto(RubyGroupTargetDto? RubyTarget, string? Text, string? AfterSegmentId) : ProjectCommandDto("lyrics.addRubySegment");
/// <summary>删除片段；最后一个片段删除整个注音组。</summary>
public sealed record RemoveRubySegmentCommandDto(RubySegmentTargetDto? RubyTarget) : ProjectCommandDto("lyrics.removeRubySegment");
/// <summary>按完整稳定片段身份序列调整文本顺序，保留各自时间。</summary>
public sealed record ReorderRubySegmentsCommandDto(RubyGroupTargetDto? RubyTarget, string[]? OrderedSegmentIds) : ProjectCommandDto("lyrics.reorderRubySegments");
/// <summary>独立注音时间批次条目。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RubySegmentTimeChangeDto(RubySegmentTargetDto? Target, string? StartUs, string? EndUs);
/// <summary>原子设置多个独立注音片段的时间。</summary>
public sealed record SetRubySegmentTimesCommandDto(RubySegmentTimeChangeDto[]? Changes) : ProjectCommandDto("lyrics.setRubySegmentTimes");
/// <summary>有界真实注音编辑反馈；SelectedSegmentId 为空表示没有建议片段选择。</summary>
public sealed record RubySegmentCommandResultDto(int ChangedSegmentCount,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RubyGroupTargetDto? Target = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SelectedSegmentId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CreatedSegmentId = null,
    bool SelectionChanged = false);
