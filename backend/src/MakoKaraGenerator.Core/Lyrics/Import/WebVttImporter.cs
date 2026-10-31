using System.Collections.Immutable;
using System.Net;
using System.Text;
using MakoKaraGenerator.Core.Diagnostics;

namespace MakoKaraGenerator.Core.Lyrics.Import;

/// <summary>
/// WebVTT 导入器：默认行级时间，cue 里带 <c>&lt;00:00.000&gt;</c> 时按逐字导入。
/// </summary>
/// <remarks>
/// <para>
/// WebVTT 标准本身没有逐字语法，但不少工具用"cue 内的时间标签"表达逐字
/// （与增强 LRC 的 <c>&lt;mm:ss.xx&gt;</c> 同族）。本导入器两种都认：
/// 有逐字标签就按逐字导入，没有就按行级导入。
/// </para>
/// <para>
/// <c>&lt;c&gt;</c>、<c>&lt;v&gt;</c>、<c>&lt;i&gt;</c> 这类行内标签本项目表达不了，
/// 剥掉并写进报告；HTML 实体会还原成字符。
/// </para>
/// </remarks>
public sealed class WebVttImporter : ParsedLyricsImporterBase
{
    /// <summary>导入器标识。</summary>
    public const string IdValue = "vtt";

    /// <inheritdoc/>
    public override string Id => IdValue;

    /// <inheritdoc/>
    public override string DisplayName => "WebVTT 字幕（可含逐字）";

    /// <inheritdoc/>
    public override string SourceFormat =>
        "WebVTT 字幕块 hh:mm:ss.mmm --> hh:mm:ss.mmm；cue 内的 <00:00.000> 视为逐字时间";

    /// <inheritdoc/>
    public override bool CanImport(string fileName, ReadOnlySpan<byte> header)
    {
        ArgumentNullException.ThrowIfNull(fileName);

        return fileName.EndsWith(".vtt", StringComparison.OrdinalIgnoreCase) || LooksLikeWebVtt(header);
    }

    /// <inheritdoc/>
    public override string MatchReason(string fileName, ReadOnlySpan<byte> header)
    {
        ArgumentNullException.ThrowIfNull(fileName);

        return fileName.EndsWith(".vtt", StringComparison.OrdinalIgnoreCase)
            ? "扩展名为 .vtt，按 WebVTT 字幕块解析"
            : "扩展名未登记，但文件以 WEBVTT 开头";
    }

    /// <summary>判断一段字节开头是否像 WebVTT。</summary>
    /// <param name="header">文件开头字节。</param>
    /// <returns>像 WebVTT 时为 <see langword="true" />。</returns>
    internal static bool LooksLikeWebVtt(ReadOnlySpan<byte> header) =>
        !header.IsEmpty && header.TrimStart(" \t\r\n\uFEFF"u8).StartsWith("WEBVTT"u8);

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
        bool leadingUntimed = false;
        int index = 0;

        while (index < rawLines.Length)
        {
            string current = rawLines[index];

            if (current.AsSpan().IsWhiteSpace())
            {
                index++;
                continue;
            }

            // 注释与样式块：整块跳过。
            if (current.StartsWith("WEBVTT", StringComparison.Ordinal)
                || current.StartsWith("NOTE", StringComparison.Ordinal)
                || current.StartsWith("STYLE", StringComparison.Ordinal)
                || current.StartsWith("REGION", StringComparison.Ordinal))
            {
                while (index < rawLines.Length && !rawLines[index].AsSpan().IsWhiteSpace())
                {
                    index++;
                }

                continue;
            }

            int cueLineNumber = index + 1;

            // 可选的 cue 标识行（不含 -->）。
            if (!current.Contains("-->", StringComparison.Ordinal) && index + 1 < rawLines.Length)
            {
                index++;
            }

            if (index >= rawLines.Length || !SrtImporter.TryParseTimestampPair(rawLines[index], out long startUs, out long endUs))
            {
                issues.Add(new LyricsImportIssue(
                    LyricsImportIssueKind.MalformedTag,
                    IssueSeverity.Warning,
                    cueLineNumber,
                    current,
                    "这一块不是 WebVTT 字幕块（缺少时间行），已跳过。"));

                index++;
                continue;
            }

            index++;
            BudgetedImportList<string> cueLines = new(context.PreparationGuard);

            while (index < rawLines.Length && !rawLines[index].AsSpan().IsWhiteSpace())
            {
                cueLines.Add(rawLines[index]);
                index++;
            }

            if (endUs <= startUs)
            {
                issues.Add(new LyricsImportIssue(
                    LyricsImportIssueKind.MalformedTag,
                    IssueSeverity.Warning,
                    cueLineNumber,
                    current,
                    "字幕块的终点不晚于起点，已跳过。"));

                continue;
            }

            CueContent content = ParseCueContent(cueLines, context.PreparationGuard);

            if (content.Text.Length == 0)
            {
                issues.Add(new LyricsImportIssue(
                    LyricsImportIssueKind.EmptyText,
                    IssueSeverity.Info,
                    cueLineNumber,
                    current,
                    "字幕块没有文本，已跳过。"));

                continue;
            }

            strippedTags |= content.StrippedTags;

            if (content.HasUntimedLeadingText)
            {
                leadingUntimed = true;
            }

            lines.Add(new ParsedLyricsLine(content.Text, startUs, endUs, content.Tokens, cueLineNumber));
        }

        if (leadingUntimed)
        {
            issues.Add(new LyricsImportIssue(
                LyricsImportIssueKind.TextWithoutTimestamp,
                IssueSeverity.Info,
                0,
                string.Empty,
                "有的字幕块开头有未标时间的文本，已并入该块的第一个元素。"));
        }

        BudgetedImportList<string> discarded = new(context.PreparationGuard);

        if (strippedTags)
        {
            discarded.Add("WebVTT 行内标签（<c>、<v>、<i> 等）");
        }

        return new ParsedLyrics([.. lines], rawLines.Length, [.. discarded]);
    }

    /// <summary>一个 cue 的解析结果。</summary>
    private sealed record CueContent(string Text, ImmutableArray<ParsedLyricsToken> Tokens, bool StrippedTags, bool HasUntimedLeadingText);

    /// <summary>解析 cue 文本：时间标签决定逐字时间，其余标签剥掉。</summary>
    private static CueContent ParseCueContent(IReadOnlyList<string> rawLines, LyricsImportPreparationGuard? guard)
    {
        BudgetedImportList<(long? StartUs, string Text)> segments = new(guard);
        BudgetedImportText current = new(guard);
        long? currentStart = null;
        bool strippedTags = false;
        bool firstLine = true;

        foreach (string rawLine in rawLines)
        {
            if (!firstLine)
            {
                current.Append(' ');
            }

            firstLine = false;

            int i = 0;

            while (i < rawLine.Length)
            {
                char c = rawLine[i];

                if (c != '<')
                {
                    current.Append(c);
                    i++;
                    continue;
                }

                int close = rawLine.IndexOf('>', i);

                if (close < 0)
                {
                    current.Append(rawLine[i..]);
                    break;
                }

                string tag = rawLine[(i + 1)..close];

                if (SrtImporter.TryParseTimestamp(tag, out long timestampUs))
                {
                    Flush(segments, current, currentStart);
                    currentStart = timestampUs;
                }
                else
                {
                    strippedTags = true;
                }

                i = close + 1;
            }
        }

        Flush(segments, current, currentStart);

        if (segments.Count == 0)
        {
            return new CueContent(string.Empty, [], strippedTags, false);
        }

        bool hasTimed = segments.Exists(segment => segment.StartUs is not null);

        if (!hasTimed)
        {
            // 没有逐字标签：整块按行级处理（元素由导入骨架按文本元素切出）。
            string plain = WebUtility.HtmlDecode(string.Concat(segments.Select(segment => segment.Text))).Trim();

            return new CueContent(plain, [], strippedTags, false);
        }

        BudgetedImportText lineText = new(guard);
        BudgetedImportBuilder<ParsedLyricsToken> tokens = new(guard);
        string prefix = string.Empty;
        bool prefixUsed = false;
        BudgetedImportList<(long StartUs, string Text)> timed = new(guard);

        foreach ((long? startUs, string segmentText) in segments)
        {
            if (startUs is { } start)
            {
                timed.Add((start, prefix + segmentText));
                prefix = string.Empty;
            }
            else
            {
                // 还没有遇到时间标签：先攒着，并入后面第一个有时间元素。
                prefix += segmentText;
                prefixUsed = prefix.Length > 0;
            }
        }

        if (prefix.Length > 0 && timed.Count > 0)
        {
            // 结尾剩余的未标时间文本：并入最后一个元素。
            (long lastStart, string lastText) = timed[^1];
            timed[^1] = (lastStart, lastText + prefix);
        }

        foreach ((long start, string segmentText) in timed)
        {
            string decoded = WebUtility.HtmlDecode(segmentText);

            if (decoded.Length == 0)
            {
                continue;
            }

            tokens.Add(new ParsedLyricsToken(decoded, start));
            lineText.Append(decoded);
        }

        return new CueContent(lineText.ToString(), tokens.ToImmutable(), strippedTags, prefixUsed);
    }

    private static void Flush(BudgetedImportList<(long? StartUs, string Text)> segments, BudgetedImportText current, long? startUs)
    {
        if (current.Length == 0)
        {
            return;
        }

        segments.Add((startUs, current.ToString()));
        current.Clear();
    }
}
