namespace MakoKaraGenerator.Core.Lyrics.Export;

/// <summary>目标格式无法表达的一类内容。</summary>
/// <param name="Feature">能力名称，例如"逐字时间"。</param>
/// <param name="AffectedCount">受影响的元素或行的数量。</param>
/// <param name="Message">面向用户的说明。</param>
public sealed record LyricsExportLoss(string Feature, int AffectedCount, string Message);
