using System.Text.Json.Serialization;

namespace MakoKaraGenerator.Backend.Contracts;

/// <summary>提交稳定行正文；空字符串保留可编辑的空行语义。</summary>
[JsonConverter(typeof(ProjectCommandJsonConverter))]
public sealed record SetLineTextCommandDto(LyricTimingTargetDto? Target, string? Text) : ProjectCommandDto("lyrics.setLineText", Target);

/// <summary>提交基础元素的注音文字；空字符串删除对应注音。</summary>
[JsonConverter(typeof(ProjectCommandJsonConverter))]
public sealed record SetRubyTextCommandDto(LyricTimingTargetDto? Target, string? Text) : ProjectCommandDto("lyrics.setRubyText", Target);

/// <summary>在明确来源的稳定行之后新增空行；前行为空时追加到末尾。</summary>
[JsonConverter(typeof(ProjectCommandJsonConverter))]
public sealed record AddLineCommandDto(string? BranchId, string? AfterLineId = null) : ProjectCommandDto("lyrics.addLine");

/// <summary>复制稳定行及其展示覆盖。</summary>
[JsonConverter(typeof(ProjectCommandJsonConverter))]
public sealed record DuplicateLineCommandDto(LyricTimingTargetDto? Target) : ProjectCommandDto("lyrics.duplicateLine", Target);

/// <summary>删除稳定行并保留其余数据的真实关联状态。</summary>
[JsonConverter(typeof(ProjectCommandJsonConverter))]
public sealed record RemoveLineCommandDto(LyricTimingTargetDto? Target) : ProjectCommandDto("lyrics.removeLine", Target);

/// <summary>原子设置画布宽高；合法编辑不要求偶数尺寸。</summary>
[JsonConverter(typeof(ProjectCommandJsonConverter))]
public sealed record SetCanvasSizeCommandDto(int? Width, int? Height) : ProjectCommandDto("composition.setCanvasSize");

/// <summary>设置精确有理数帧率。</summary>
[JsonConverter(typeof(ProjectCommandJsonConverter))]
public sealed record SetFrameRateCommandDto(int? Numerator, int? Denominator) : ProjectCommandDto("composition.setFrameRate");

/// <summary>显式设置四个 RGBA 字节，Alpha 必填。</summary>
[JsonConverter(typeof(ProjectCommandJsonConverter))]
public sealed record SetBackgroundColorCommandDto(byte? Red, byte? Green, byte? Blue, byte? Alpha) : ProjectCommandDto("composition.setBackgroundColor");

/// <summary>设置输出时长；必填的空值表示尚未确定。</summary>
[JsonConverter(typeof(ProjectCommandJsonConverter))]
public sealed record SetOutputDurationCommandDto(string? DurationUs) : ProjectCommandDto("composition.setOutputDuration");

/// <summary>采用当前有效歌词的最后已知终点。</summary>
[JsonConverter(typeof(ProjectCommandJsonConverter))]
public sealed record UseLyricsEndCommandDto() : ProjectCommandDto("composition.useLyricsEnd");

/// <summary>采用服务端已准备的音频时长。</summary>
[JsonConverter(typeof(ProjectCommandJsonConverter))]
public sealed record UseAudioDurationCommandDto() : ProjectCommandDto("composition.useAudioDuration");

/// <summary>行操作的建议选择；HasSelection 为假时清空该来源内的行及元素选择。</summary>
public sealed record LyricContentSelectionDto(string BranchId, bool HasSelection,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? LineId = null);

/// <summary>歌词内容操作的有界真实反馈；省略 Selection 表示不建议改变客户端选择。</summary>
public sealed record LyricContentCommandFeedbackDto(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] LyricContentSelectionDto? Selection = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CreatedLineId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DeletedLineId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? InsertedIndex = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? OverlapsNext = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? PreservedTokenCount = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? ResetTokenCount = null);
