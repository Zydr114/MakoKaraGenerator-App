using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using MakoKaraGenerator.Core.Diagnostics;

namespace MakoKaraGenerator.Core.Lyrics.Import;

/// <summary>
/// UltraStar 导入器：按 <c>#BPM</c> / <c>#GAP</c> 把"拍"换算成秒，逐字时间来自音符行。
/// </summary>
/// <remarks>
/// <para>
/// UltraStar 的音符行是 <c>: 起始拍 时长 音高 文本</c>；时间单位是<b>拍</b>，
/// 一拍 = <c>60000 / (BPM × 4)</c> 毫秒（<c>#BPM</c> 的值是"四拍"值，实际 BPM = 值 × 4）。
/// </para>
/// <para>
/// 本项目不做音高与打分：音高、音符类型（golden / rap）与第二个声部都会被丢弃并写进报告。
/// </para>
/// </remarks>
public sealed class UltraStarImporter : ParsedLyricsImporterBase
{
    /// <summary>导入器标识。</summary>
    public const string IdValue = "ustar";

    /// <summary>没有 <c>#BPM</c> 时使用的兜底值（头部的 30 ⇒ 实际 120 BPM）。</summary>
    private const double DefaultBpmHeader = 30d;

    /// <inheritdoc/>
    public override string Id => IdValue;

    /// <inheritdoc/>
    public override string DisplayName => "UltraStar 卡拉 OK（逐字 + 音高）";

    /// <inheritdoc/>
    public override string SourceFormat =>
        "UltraStar 文本：#BPM / #GAP 头部 + 音符行「: 起始拍 时长 音高 文本」（拍换算成秒）";

    /// <inheritdoc/>
    public override bool CanImport(string fileName, ReadOnlySpan<byte> header)
    {
        ArgumentNullException.ThrowIfNull(fileName);

        if (header.IndexOf("#BPM"u8) >= 0 || header.IndexOf("#GAP"u8) >= 0)
        {
            return true;
        }

        return header.IndexOf("#TITLE"u8) >= 0 && header.IndexOf("#ARTIST"u8) >= 0;
    }

    /// <inheritdoc/>
    public override string MatchReason(string fileName, ReadOnlySpan<byte> header)
    {
        ArgumentNullException.ThrowIfNull(fileName);

        return header.IndexOf("#BPM"u8) >= 0
            ? "内容含 UltraStar 的 #BPM 头部与音符行"
            : "内容含 UltraStar 的 #TITLE / #ARTIST / #GAP 等头部";
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
        double? bpmHeader = null;
        long gapMs = 0L;
        bool sawPlayer2 = false;

        foreach (string line in rawLines)
        {
            if (!line.StartsWith('#'))
            {
                continue;
            }

            int colon = line.IndexOf(':');

            if (colon < 0)
            {
                continue;
            }

            string name = line[..colon].Trim().ToUpperInvariant();
            string value = line[(colon + 1)..].Trim();

            switch (name)
            {
                case "#BPM":
                    if (TryParseDecimal(value, out double bpm))
                    {
                        bpmHeader = bpm;
                    }

                    break;

                case "#GAP":
                    if (TryParseDecimal(value, out double gap))
                    {
                        gapMs = (long)Math.Round(gap);
                    }

                    break;

                default:
                    if (name.StartsWith("#P", StringComparison.Ordinal)
                        && name.Length > 2
                        && char.IsDigit(name[2]))
                    {
                        sawPlayer2 = true;
                    }

                    break;
            }
        }

        if (bpmHeader is null)
        {
            issues.Add(new LyricsImportIssue(
                LyricsImportIssueKind.MalformedTag,
                IssueSeverity.Warning,
                0,
                string.Empty,
                $"文件没有可用的 #BPM 头部，已按 120 BPM 换算（时间可能不对，请核对）。"));

            bpmHeader = DefaultBpmHeader;
        }

        double microsecondsPerBeat = 60_000_000d / (bpmHeader.Value * 4d);
        BudgetedImportList<ParsedLyricsLine> lines = new(context.PreparationGuard);
        BudgetedImportList<ParsedLyricsToken> current = new(context.PreparationGuard);
        int currentStartLineNumber = 0;
        bool sawPitch = false;
        bool sawNoteTypes = false;

        for (int i = 0; i < rawLines.Length; i++)
        {
            string line = rawLines[i].TrimEnd();

            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            char type = line[0];

            if (type is '-' or 'E')
            {
                FlushLine(lines, current, microsecondsPerBeat, gapMs, currentStartLineNumber);
                current = new(context.PreparationGuard);
                continue;
            }

            if (line.Length < 2)
            {
                continue;
            }

            string[] parts = line[1..].Split((char[]?)null, 4, StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length < 4)
            {
                issues.Add(new LyricsImportIssue(
                    LyricsImportIssueKind.MalformedTag,
                    IssueSeverity.Warning,
                    i + 1,
                    line,
                    "音符行的列数不足（需要「起始拍 时长 音高 文本」），已跳过。"));

                continue;
            }

            if (!TryParseDecimal(parts[0], out double startBeat) || !TryParseDecimal(parts[1], out double lengthBeats))
            {
                issues.Add(new LyricsImportIssue(
                    LyricsImportIssueKind.MalformedTag,
                    IssueSeverity.Warning,
                    i + 1,
                    line,
                    "音符行的起始拍或时长无法解析，已跳过。"));

                continue;
            }

            sawPitch = true;
            sawNoteTypes |= type != ':';

            string tokenText = parts[3].Replace('~', ' ');

            if (tokenText.Length == 0)
            {
                continue;
            }

            if (current.Count == 0)
            {
                currentStartLineNumber = i + 1;
            }

            long startUs = (gapMs * 1000L) + (long)Math.Round(startBeat * microsecondsPerBeat);
            long endUs = startUs + (long)Math.Round(Math.Max(lengthBeats, 0d) * microsecondsPerBeat);

            if (endUs > startUs)
            {
                current.Add(new ParsedLyricsToken(tokenText, Math.Max(0L, startUs), Math.Max(0L, endUs), false, i + 1));
            }
        }

        FlushLine(lines, current, microsecondsPerBeat, gapMs, currentStartLineNumber);

        BudgetedImportList<string> discarded = new(context.PreparationGuard);

        if (sawPitch)
        {
            discarded.Add("音高（pitch）与打分信息");
        }

        if (sawNoteTypes)
        {
            discarded.Add("音符类型（golden / rap / freestyle）");
        }

        if (sawPlayer2)
        {
            discarded.Add("第二个及之后的声部（#P2 …）");
        }

        return new ParsedLyrics([.. lines], rawLines.Length, [.. discarded]);
    }

    private static void FlushLine(
        BudgetedImportList<ParsedLyricsLine> lines,
        BudgetedImportList<ParsedLyricsToken> current,
        double microsecondsPerBeat,
        long gapMs,
        int sourceLineNumber)
    {
        _ = microsecondsPerBeat;
        _ = gapMs;

        if (current.Count == 0)
        {
            return;
        }

        ImmutableArray<ParsedLyricsToken> tokens = [.. current];
        BudgetedImportText text = new(lines.Guard);

        foreach (ParsedLyricsToken token in tokens)
        {
            text.Append(token.Text);
        }

        long startUs = tokens[0].StartUs;
        long endUs = tokens[^1].EndUs ?? startUs + 1;

        lines.Add(new ParsedLyricsLine(text.ToString(), startUs, endUs, tokens, sourceLineNumber));
        current.Clear();
    }

    /// <summary>解析允许用逗号作小数分隔符的十进制数（UltraStar 文件两种都常见）。</summary>
    private static bool TryParseDecimal(string text, out double value) =>
        double.TryParse(text.Replace(',', '.'), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value);
}
