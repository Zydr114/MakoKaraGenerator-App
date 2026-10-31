namespace MakoKaraGenerator.Core.Lyrics.Import;

/// <summary>导入过程中发现的问题种类（与具体格式无关的中间表示）。</summary>
public enum LyricsImportIssueKind
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

    /// <summary>导入后按规则重新切分了文本元素（见 <see cref="TokenSplitRule" />）。</summary>
    TokenSplitApplied = 13,

    /// <summary>解析出的元素文本拼起来与行文本不一致，已按文本元素重建（时间置为缺失）。</summary>
    TokenTextMismatch = 14,
}
