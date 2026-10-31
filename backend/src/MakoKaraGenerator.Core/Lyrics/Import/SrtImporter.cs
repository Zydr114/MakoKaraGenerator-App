using System.Globalization;

namespace MakoKaraGenerator.Core.Lyrics.Import;

/// <summary>
/// SRT（SubRip）导入器：只有行级时间。
/// </summary>
/// <remarks>
/// SRT 没有逐字信息，因此元素按 Unicode 文本元素切出来但<b>没有时间</b>，
/// 由用户自己打点，或者导入时显式选择"按规则切分"（ADR-0046）。
/// 行内样式标签（<c>&lt;i&gt;</c>、<c>&lt;font&gt;</c> 等）本项目表达不了，剥掉并写进报告。
/// </remarks>
public sealed class SrtImporter : ParsedLyricsImporterBase
{
    /// <summary>导入器标识。</summary>
    public const string IdValue = "srt";

    /// <inheritdoc/>
    public override string Id => IdValue;

    /// <inheritdoc/>
    public override string DisplayName => "SRT 字幕（仅行级时间）";

    /// <inheritdoc/>
    public override string SourceFormat => "SubRip 字幕块 hh:mm:ss,mmm --> hh:mm:ss,mmm";

    /// <inheritdoc/>
    public override bool CanImport(string fileName, ReadOnlySpan<byte> header)
    {
        ArgumentNullException.ThrowIfNull(fileName);

        return fileName.EndsWith(".srt", StringComparison.OrdinalIgnoreCase) || LooksLikeSrt(header);
    }

    /// <inheritdoc/>
    public override string MatchReason(string fileName, ReadOnlySpan<byte> header)
    {
        ArgumentNullException.ThrowIfNull(fileName);

        return fileName.EndsWith(".srt", StringComparison.OrdinalIgnoreCase)
            ? "扩展名为 .srt，按 SubRip 字幕块解析"
            : "扩展名未登记，但内容含 SRT 时间行（-->）";
    }

    /// <summary>判断一段字节开头是否像 SRT 时间行。</summary>
    /// <param name="header">文件开头字节。</param>
    /// <returns>像 SRT 时为 <see langword="true" />。</returns>
    internal static bool LooksLikeSrt(ReadOnlySpan<byte> header)
    {
        if (header.IsEmpty)
        {
            return false;
        }

        return header.IndexOf("-->"u8) >= 0;
    }

    /// <inheritdoc/>
    protected override ParsedLyrics Parse(string text, LyricsImportContext context, List<LyricsImportIssue> issues) =>
        ParseForPreparation(text, context, issues);

    /// <inheritdoc />
    protected override ParsedLyrics ParseForPreparation(string text, LyricsImportContext context, IList<LyricsImportIssue> issues)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(issues);

        context.PreparationGuard?.Lines(text);
        string[] rawLines = text.ReplaceLineEndings("\n").Split('\n');
        if (context.PreparationGuard is { } fieldsGuard)
            foreach (string rawLine in rawLines) fieldsGuard.Fields(rawLine, ",: \t>");
        BudgetedImportList<ParsedLyricsLine> lines = new(context.PreparationGuard);
        bool strippedTags = false;
        int index = 0;

        while (index < rawLines.Length)
        {
            if (rawLines[index].AsSpan().IsWhiteSpace())
            {
                index++;
                continue;
            }

            int blockLineNumber = index + 1;
            string first = rawLines[index];
            long start;
            long end;

            if (TryParseTimestampPair(first, out start, out end))
            {
                index++;
            }
            else if (int.TryParse(first.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out _)
                && index + 1 < rawLines.Length
                && TryParseTimestampPair(rawLines[index + 1], out start, out end))
            {
                index += 2;
            }
            else
            {
                issues.Add(new LyricsImportIssue(
                    LyricsImportIssueKind.MalformedTag,
                    Diagnostics.IssueSeverity.Warning,
                    blockLineNumber,
                    first,
                    "这一块不是 SRT 字幕块（缺少时间行），已跳过。"));

                index++;
                continue;
            }

            BudgetedImportList<string> textLines = new(context.PreparationGuard);

            while (index < rawLines.Length && !rawLines[index].AsSpan().IsWhiteSpace())
            {
                textLines.Add(rawLines[index]);
                index++;
            }

            BudgetedImportList<string> cleaned = new(context.PreparationGuard);

            foreach (string rawText in textLines)
            {
                string stripped = StripTags(rawText);

                if (!ReferenceEquals(stripped, rawText))
                {
                    strippedTags = true;
                }

                cleaned.Add(stripped);
            }

            string lineText = string.Join(" ", cleaned).Trim();

            if (lineText.Length == 0)
            {
                issues.Add(new LyricsImportIssue(
                    LyricsImportIssueKind.EmptyText,
                    Diagnostics.IssueSeverity.Info,
                    blockLineNumber,
                    first,
                    "字幕块没有文本，已跳过。"));

                continue;
            }

            if (end <= start)
            {
                issues.Add(new LyricsImportIssue(
                    LyricsImportIssueKind.MalformedTag,
                    Diagnostics.IssueSeverity.Warning,
                    blockLineNumber,
                    first,
                    "字幕块的终点不晚于起点，已跳过。"));

                continue;
            }

            lines.Add(new ParsedLyricsLine(lineText, start, end, default, blockLineNumber));
        }

        return new ParsedLyrics(
            [.. lines],
            rawLines.Length,
            strippedTags ? ["SRT 行内样式标签（如 <i>、<font>）"] : default);
    }

    /// <summary>解析 SRT / WebVTT 的时间区间行（右侧的多余设置会被忽略）。</summary>
    internal static bool TryParseTimestampPair(string line, out long startUs, out long endUs)
    {
        startUs = 0L;
        endUs = 0L;

        string[] parts = line.Split("-->", StringSplitOptions.TrimEntries);

        if (parts.Length != 2)
        {
            return false;
        }

        // 右侧可能带定位设置（如 "align:start"），只取第一个词。
        string right = parts[1].Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;

        return TryParseTimestamp(parts[0], out startUs) && TryParseTimestamp(right, out endUs);
    }

    /// <summary>解析 <c>hh:mm:ss,mmm</c>（也接受 <c>.</c> 与省略小时的形式）。</summary>
    internal static bool TryParseTimestamp(string text, out long microseconds)
    {
        microseconds = 0L;

        string[] parts = text.Trim().Split(':');

        if (parts.Length is < 2 or > 3)
        {
            return false;
        }

        int cursor = 0;
        long hours = 0L;

        if (parts.Length == 3)
        {
            if (!long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out hours))
            {
                return false;
            }

            cursor = 1;
        }

        if (!long.TryParse(parts[cursor], NumberStyles.None, CultureInfo.InvariantCulture, out long minutes))
        {
            return false;
        }

        string secondsText = parts[cursor + 1].Replace(',', '.');

        if (!double.TryParse(secondsText, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double seconds))
        {
            return false;
        }

        microseconds = ((hours * 3600L) + (minutes * 60L)) * 1_000_000L + (long)Math.Round(seconds * 1_000_000d);

        return microseconds >= 0L;
    }

    /// <summary>剥掉 SRT 的行内样式标签；没有标签时返回原实例。</summary>
    private static string StripTags(string text)
    {
        int open = text.IndexOf('<', StringComparison.Ordinal);

        if (open < 0)
        {
            return text;
        }

        System.Text.StringBuilder builder = new(text.Length);

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];

            if (c != '<')
            {
                builder.Append(c);
                continue;
            }

            int close = text.IndexOf('>', i);

            if (close < 0)
            {
                builder.Append(text[i..]);
                break;
            }

            i = close;
        }

        return builder.ToString();
    }
}
