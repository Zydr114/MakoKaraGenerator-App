using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MakoKaraGenerator.Core.Diagnostics;

namespace MakoKaraGenerator.Core.Lyrics.Import;

/// <summary>
/// ASS / SSA 导入器：<c>[Events]</c> 的 <c>Dialogue:</c> 行，卡拉 OK 标签给出逐字时长。
/// </summary>
/// <remarks>
/// <para>
/// ASS 的逐字单位是<b>厘秒</b>（<c>\k50</c> = 0.5 秒），从行起点开始累加，
/// 因此每个音节的时间都是格式明确给出的实测值（不是估算）。
/// </para>
/// <para>
/// <c>\kt</c>（v4++ 的绝对时间）首期不支持，遇到就跳过并写进报告。
/// 行内的样式与特效标签（<c>\pos</c>、<c>\fad</c>、<c>{\b1}</c> 等）与时间无关，剥掉并记进报告。
/// </para>
/// </remarks>
public sealed class AssImporter : ParsedLyricsImporterBase
{
    /// <summary>导入器标识。</summary>
    public const string IdValue = "ass";

    private static readonly Regex KaraokeTag = new(@"\\(kf|ko|kt|k|K)(\d*)", RegexOptions.Compiled);

    /// <inheritdoc/>
    public override string Id => IdValue;

    /// <inheritdoc/>
    public override string DisplayName => "ASS / SSA 卡拉 OK 字幕（可含逐字）";

    /// <inheritdoc/>
    public override string SourceFormat => "ASS 事件行 Dialogue: … 与卡拉 OK 标签 \\k\\kf\\ko（厘秒）";

    /// <inheritdoc/>
    public override bool CanImport(string fileName, ReadOnlySpan<byte> header)
    {
        ArgumentNullException.ThrowIfNull(fileName);

        if (fileName.EndsWith(".ass", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".ssa", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return header.IndexOf("[Script Info]"u8) >= 0 || header.IndexOf("Dialogue:"u8) >= 0;
    }

    /// <inheritdoc/>
    public override string MatchReason(string fileName, ReadOnlySpan<byte> header)
    {
        ArgumentNullException.ThrowIfNull(fileName);

        return fileName.EndsWith(".ass", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".ssa", StringComparison.OrdinalIgnoreCase)
            ? "扩展名为 .ass / .ssa，按 ASS 事件行解析"
            : "扩展名未登记，但内容含 ASS 的 [Script Info] / Dialogue: 结构";
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

        bool inEvents = false;
        bool sawStyleTags = false;
        bool sawKaraoke = false;
        bool sawUnsupportedKt = false;
        string[]? columns = null;

        for (int i = 0; i < rawLines.Length; i++)
        {
            string trimmed = rawLines[i].Trim();

            if (trimmed.StartsWith('['))
            {
                inEvents = trimmed.StartsWith("[Events]", StringComparison.OrdinalIgnoreCase);
                columns = null;
                continue;
            }

            if (!inEvents)
            {
                continue;
            }

            if (trimmed.StartsWith("Format:", StringComparison.OrdinalIgnoreCase))
            {
                columns = [.. trimmed["Format:".Length..].Split(',', StringSplitOptions.TrimEntries)];
                continue;
            }

            if (!trimmed.StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            int startIndex = ColumnIndex(columns, "Start", 1);
            int endIndex = ColumnIndex(columns, "End", 2);
            int textIndex = ColumnIndex(columns, "Text", 9);
            int columnCount = Math.Max(textIndex + 1, columns?.Length ?? 10);
            string[] fields = trimmed["Dialogue:".Length..].Split(',', columnCount);

            if (fields.Length <= textIndex
                || !TryParseTime(fields[startIndex], out long startUs)
                || !TryParseTime(fields[endIndex], out long endUs))
            {
                issues.Add(new LyricsImportIssue(
                    LyricsImportIssueKind.MalformedTag,
                    IssueSeverity.Warning,
                    i + 1,
                    trimmed,
                    "这一行 Dialogue 的列数与时间格式不对，已跳过。"));

                continue;
            }

            DialogueText parsed = ParseDialogueText(
                fields[textIndex],
                startUs,
                i + 1,
                issues,
                ref sawStyleTags,
                ref sawKaraoke,
                ref sawUnsupportedKt);

            if (parsed.Text.Length == 0 || endUs <= startUs)
            {
                issues.Add(new LyricsImportIssue(
                    LyricsImportIssueKind.EmptyText,
                    IssueSeverity.Info,
                    i + 1,
                    trimmed,
                    "这一行 Dialogue 没有可用文本或时间区间不成立，已跳过。"));

                continue;
            }

            lines.Add(new ParsedLyricsLine(parsed.Text, startUs, endUs, parsed.Tokens, i + 1));
        }

        BudgetedImportList<string> discarded = new(context.PreparationGuard);

        if (sawStyleTags)
        {
            discarded.Add("ASS 行内样式与特效标签（\\pos、\\fad、{\\b1} 等）");
        }

        if (sawUnsupportedKt)
        {
            discarded.Add("\\kt 绝对卡拉 OK 时间（首期未支持）");
        }

        if (!sawKaraoke && lines.Count > 0)
        {
            issues.Add(new LyricsImportIssue(
                LyricsImportIssueKind.TextWithoutTimestamp,
                IssueSeverity.Info,
                0,
                string.Empty,
                "事件行里没有卡拉 OK 标签（\\k 家族），因此只导入行级时间。"));
        }

        return new ParsedLyrics([.. lines], rawLines.Length, [.. discarded]);
    }

    /// <summary>一行 Dialogue 的文本解析结果。</summary>
    private sealed record DialogueText(string Text, ImmutableArray<ParsedLyricsToken> Tokens);

    private static int ColumnIndex(string[]? columns, string name, int fallback)
    {
        if (columns is null)
        {
            return fallback;
        }

        for (int i = 0; i < columns.Length; i++)
        {
            if (string.Equals(columns[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return fallback;
    }

    /// <summary>剥掉标签并按 <c>\k</c> 家族切出音节。</summary>
    private static DialogueText ParseDialogueText(
        string raw,
        long lineStartUs,
        int sourceLineNumber,
        IList<LyricsImportIssue> issues,
        ref bool sawStyleTags,
        ref bool sawKaraoke,
        ref bool sawUnsupportedKt)
    {
        BudgetedImportList<(string Text, long? DurationCs)> chunks = new(LyricsImportPreparationGuard.Of(issues));
        BudgetedImportText current = new(LyricsImportPreparationGuard.Of(issues));
        long? pendingCs = null;
        int i = 0;

        while (i < raw.Length)
        {
            char c = raw[i];

            if (c == '{')
            {
                int close = raw.IndexOf('}', i);

                if (close < 0)
                {
                    sawStyleTags = true;
                    break;
                }

                sawStyleTags = true;
                string block = raw[(i + 1)..close];

                foreach (Match match in KaraokeTag.Matches(block))
                {
                    string name = match.Groups[1].Value;

                    if (string.Equals(name, "kt", StringComparison.Ordinal))
                    {
                        sawUnsupportedKt = true;
                        continue;
                    }

                    if (!long.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out long centiseconds))
                    {
                        issues.Add(new LyricsImportIssue(
                            LyricsImportIssueKind.MalformedTag,
                            IssueSeverity.Warning,
                            sourceLineNumber,
                            match.Value,
                            $"卡拉 OK 标签「{match.Value}」没有可解析的时长，已忽略。"));

                        continue;
                    }

                    if (current.Length > 0)
                    {
                        chunks.Add((current.ToString(), pendingCs));
                        current.Clear();
                    }

                    pendingCs = centiseconds;
                    sawKaraoke = true;
                }

                i = close + 1;
                continue;
            }

            if (c == '\\' && i + 1 < raw.Length && (raw[i + 1] is 'N' or 'n'))
            {
                current.Append(' ');
                i += 2;
                continue;
            }

            current.Append(c);
            i++;
        }

        if (current.Length > 0)
        {
            chunks.Add((current.ToString(), pendingCs));
        }

        BudgetedImportText text = new(LyricsImportPreparationGuard.Of(issues));

        if (!sawKaraoke)
        {
            foreach ((string chunkText, _) in chunks)
            {
                text.Append(chunkText);
            }

            return new DialogueText(text.ToString(), default);
        }

        BudgetedImportBuilder<ParsedLyricsToken> tokens = new(LyricsImportPreparationGuard.Of(issues));
        long cursor = lineStartUs;
        string prefix = string.Empty;

        foreach ((string chunkText, long? durationCs) in chunks)
        {
            if (durationCs is not { } centiseconds)
            {
                // 第一个 \k 之前的文本（通常是前奏留白或标点）：并入下一个元素。
                prefix += chunkText;
                continue;
            }

            long durationUs = centiseconds * 10_000L;
            long startUs = cursor;
            long endUs = cursor + durationUs;
            string tokenText = prefix + chunkText;

            if (tokenText.Length > 0)
            {
                tokens.Add(new ParsedLyricsToken(tokenText, startUs, endUs, false, sourceLineNumber));
                text.Append(tokenText);
            }

            prefix = string.Empty;
            cursor = endUs;
        }

        if (prefix.Length > 0 && tokens.Count > 0)
        {
            ParsedLyricsToken last = tokens[^1];
            tokens[^1] = last with { Text = last.Text + prefix };
            text.Append(prefix);
        }

        return new DialogueText(text.ToString(), tokens.ToImmutable());
    }

    /// <summary>解析 ASS 的 <c>h:mm:ss.cc</c>。</summary>
    internal static bool TryParseTime(string text, out long microseconds)
    {
        microseconds = 0L;

        string[] parts = text.Trim().Split(':');

        if (parts.Length != 3
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int hours)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int minutes)
            || !double.TryParse(parts[2].Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double seconds))
        {
            return false;
        }

        microseconds = ((hours * 3600L) + (minutes * 60L)) * 1_000_000L + (long)Math.Round(seconds * 1_000_000d);

        return microseconds >= 0L;
    }
}
