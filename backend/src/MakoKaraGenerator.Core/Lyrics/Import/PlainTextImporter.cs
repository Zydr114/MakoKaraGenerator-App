using MakoKaraGenerator.Core.Text;
using MakoKaraGenerator.Core.Diagnostics;

namespace MakoKaraGenerator.Core.Lyrics.Import;

/// <summary>逐行导入纯文本，保留缺失时间，不产生估算时间。</summary>
public sealed class PlainTextImporter : ILyricsImporter
{
    /// <inheritdoc/>
    public string Id => "text.plain";
    /// <inheritdoc/>
    public string DisplayName => "纯文本（未打轴）";
    /// <inheritdoc/>
    public string SourceFormat => "纯文本，每个非空行作为一行歌词";
    /// <inheritdoc/>
    public bool CanImport(string fileName, ReadOnlySpan<byte> header) => fileName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)
        && !PlainLrcImporter.LooksLikeLrc(header) && header.IndexOf("#BPM"u8) < 0 && header.IndexOf("-->"u8) < 0;
    /// <inheritdoc/>
    public string MatchReason(string fileName, ReadOnlySpan<byte> header) => "扩展名 .txt，未识别到时间标签，按未打轴文本导入";
    /// <inheritdoc/>
    public LyricsImportResult Import(Stream input, LyricsImportContext context)
    {
        ArgumentNullException.ThrowIfNull(input); ArgumentNullException.ThrowIfNull(context);
        using var bytes = new MemoryStream(); input.CopyTo(bytes);
        var text = LyricsTextDecoder.Decode(bytes.ToArray(), context.EncodingOverride);
        context.PreparationGuard?.Lines(text.Text);
        string[] rows = text.Text.ReplaceLineEndings("\n").Split('\n');
        int lineId = 0, tokenId = 0;
        LyricLine[] lines = [.. rows.Where(row => !string.IsNullOrWhiteSpace(row)).Select(row =>
            LyricLine.Create("line-" + ++lineId, row, tokens: GraphemeSplitter.Enumerate(row)
                .Select(element => LyricsImportPreparationGuard.Token(context.PreparationGuard, "token-" + ++tokenId, element.Text))))];
        return new(LyricsDocument.Create(lines, context.Language), new()
        {
            ImporterId = Id, ImporterName = DisplayName, SourceFormat = SourceFormat,
            SourceLineCount = rows.Length, LineCount = lines.Length, TokenCount = tokenId,
            TimedTokenCount = 0, MissingTokenCount = tokenId, EstimatedTokenCount = 0,
            SkippedLineCount = rows.Length - lines.Length, MergedContinuationCount = 0,
            DerivedLineEndCount = 0, FallbackLineEndCount = 0, OffsetUs = 0,
            EncodingName = text.EncodingName, HadByteOrderMark = text.HadByteOrderMark,
            NewLineKind = text.NewLineKind,
            Issues = text.IsFallbackDecoding ? [new(LyricsImportIssueKind.EncodingAssumed, IssueSeverity.Warning, 0,
                text.EncodingName, $"无法确定原始编码，已按 {text.EncodingName} 解码；请检查文字。")] : [],
        });
    }
}
