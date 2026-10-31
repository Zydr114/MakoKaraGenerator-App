namespace MakoKaraGenerator.Core.Lyrics.Lrc;

/// <summary>LRC 解析过程中的问题种类。</summary>
public enum LrcDiagnosticKind
{
    /// <summary>方括号内容既不是时间标签也不是已知的元数据标签。</summary>
    UnknownTag = 0,

    /// <summary>有文本但没有时间标签，且没有可追加的上一行。</summary>
    TextWithoutTimestamp = 1,

    /// <summary>只有时间标签、没有文本。</summary>
    EmptyText = 2,

    /// <summary>同一个时间戳出现了多次。</summary>
    DuplicateTimestamp = 3,

    /// <summary>时间顺序与原文件不一致，需要重排。</summary>
    OutOfOrder = 4,

    /// <summary>应用偏移后时间变成负数，已被钳制到 0。</summary>
    NegativeTimeClamped = 5,

    /// <summary>元数据标签存在但值无法解析（例如 <c>[offset:abc]</c>）。</summary>
    MalformedMetadata = 6,
}
