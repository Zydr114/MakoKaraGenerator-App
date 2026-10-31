using MakoKaraGenerator.Core.Lyrics.Import;

namespace MakoKaraGenerator.Core.Lyrics.Lrc;

/// <summary>
/// 普通 LRC 的行级解析器。
/// </summary>
/// <remarks>
/// <para>
/// 只解析语法：时间标签、元数据标签、行文本与诊断。不做以下事情：
/// </para>
/// <list type="bullet">
///   <item><description>不推导行结束时间（LRC 里没有，属于导入器的推导并在报告里说明）；</description></item>
///   <item><description>不创建逐字元素（普通 LRC 不含逐字时间）；</description></item>
///   <item><description>不估算任何时间（docs/development.md §7.2 禁止把估算冒充实测）。</description></item>
/// </list>
/// </remarks>
public static class LrcParser
{
    /// <summary>解析 LRC 文本。</summary>
    /// <param name="text">已经解码为字符串的 LRC 内容。</param>
    /// <param name="options">解析选项；为 <see langword="null" /> 时使用默认选项。</param>
    /// <returns>解析结果。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text" /> 为 <see langword="null" /> 时抛出。</exception>
    public static LrcDocument Parse(string text, LrcParserOptions? options = null) => Parse(text, options, null);

    /// <summary>解析 LRC 文本，在保留中间对象前执行可选累计逻辑工作配额。</summary>
    /// <param name="text">完整解码文本。</param>
    /// <param name="options">原解析策略。</param>
    /// <param name="preparationGuard">可选准备配额。</param>
    public static LrcDocument Parse(string text, LrcParserOptions? options, LyricsImportPreparationGuard? preparationGuard)
    {
        ArgumentNullException.ThrowIfNull(text);

        options ??= LrcParserOptions.Default;

        preparationGuard?.Lines(text);
        List<string> lines = SplitLines(text);
        BudgetedImportList<LrcEntry> entries = new(preparationGuard);
        BudgetedImportList<LrcMetadataEntry> metadata = new(preparationGuard);
        BudgetedImportList<LrcDiagnostic> diagnostics = new(preparationGuard);
        HashSet<long> seenTimes = [];
        HashSet<string> reportedTags = new(StringComparer.Ordinal);

        long offsetUs = 0L;
        int firstOutOfOrderLine = -1;
        long previousStartUs = -1L;

        for (int index = 0; index < lines.Count; index++)
        {
            int lineNumber = index + 1;
            string line = lines[index].Trim();

            if (line.Length == 0)
            {
                continue;
            }

            BudgetedImportList<long> times = new(preparationGuard);
            int position = 0;

            // 行首连续的 [..] 标签：既可能是时间，也可能是元数据。
            while (position < line.Length && line[position] == '[')
            {
                int close = line.IndexOf(']', position + 1);

                if (close < 0)
                {
                    AddUnknownTag(line[position..], lineNumber);
                    position = line.Length;
                    break;
                }

                ReadOnlySpan<char> inner = line.AsSpan(position + 1, close - position - 1);
                string rawTag = line[position..(close + 1)];

                if (LrcTimeTagParser.TryParse(inner, out long timeUs))
                {
                    times.Add(timeUs);
                }
                else if (TryParseMetadata(inner, out string key, out string value))
                {
                    metadata.Add(new LrcMetadataEntry(key, value, lineNumber));

                    if (string.Equals(key, "offset", StringComparison.Ordinal))
                    {
                        if (TryParseOffset(value, out long parsed))
                        {
                            offsetUs = parsed;
                        }
                        else
                        {
                            diagnostics.Add(new LrcDiagnostic(
                                LrcDiagnosticKind.MalformedMetadata,
                                lineNumber,
                                rawTag,
                                $"offset 标签无法解析为毫秒数：'{value}'"));
                        }
                    }
                }
                else
                {
                    AddUnknownTag(rawTag, lineNumber);
                }

                position = close + 1;
            }

            string content = (position < line.Length ? line[position..] : string.Empty).Trim();

            if (times.Count == 0)
            {
                if (content.Length == 0)
                {
                    continue;
                }

                // 没有时间标签的文本行：当作上一条歌词的续行（多行歌词的常见写法）。
                if (options.MergeContinuationLines && entries.Count > 0)
                {
                    LrcEntry last = entries[^1];
                    preparationGuard?.Reserve(64L + 2L * (last.Text.Length + 1L + content.Length));
                    entries[^1] = last with
                    {
                        Text = string.Concat(last.Text, "\n", content),
                        IsContinuation = true,
                    };
                }
                else
                {
                    diagnostics.Add(new LrcDiagnostic(
                        LrcDiagnosticKind.TextWithoutTimestamp,
                        lineNumber,
                        line,
                        $"该行有文本但没有时间标签，已跳过：'{content}'"));
                }

                continue;
            }

            if (content.Length == 0)
            {
                diagnostics.Add(new LrcDiagnostic(
                    LrcDiagnosticKind.EmptyText,
                    lineNumber,
                    line,
                    "该行只有时间标签、没有文本，已跳过。"));
                continue;
            }

            foreach (long timeUs in times)
            {
                if (!seenTimes.Add(timeUs))
                {
                    diagnostics.Add(new LrcDiagnostic(
                        LrcDiagnosticKind.DuplicateTimestamp,
                        lineNumber,
                        line,
                        $"时间戳 {Core.Time.TimeUs.FormatClockShort(timeUs)} 重复出现。"));
                }

                if (firstOutOfOrderLine < 0 && previousStartUs > timeUs)
                {
                    firstOutOfOrderLine = lineNumber;
                }

                previousStartUs = timeUs;
                entries.Add(new LrcEntry(timeUs, content, lineNumber, times.Count));
            }
        }

        ApplyOffset(entries, options, offsetUs, diagnostics);
        SortIfNeeded(entries, options, firstOutOfOrderLine, diagnostics);

        return new LrcDocument(entries, metadata, diagnostics, offsetUs, lines.Count);

        void AddUnknownTag(string rawTag, int lineNumber)
        {
            // 同一个未知标签在文件里出现很多次时，只报一次，避免诊断被淹没；
            // 但同一次出现仍然保留原始文本（§7.2 要求保留无法识别的标记）。
            if (reportedTags.Add(rawTag))
            {
                diagnostics.Add(new LrcDiagnostic(
                    LrcDiagnosticKind.UnknownTag,
                    lineNumber,
                    rawTag,
                    $"无法识别的标签 '{rawTag}'，已忽略。"));
            }
        }
    }

    /// <summary>按 <c>\r\n</c>、<c>\n</c>、<c>\r</c> 切分文本，保留空行。</summary>
    /// <param name="text">文本。</param>
    /// <returns>行序列。</returns>
    public static List<string> SplitLines(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        List<string> lines = [];
        int start = 0;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];

            if (c != '\n' && c != '\r')
            {
                continue;
            }

            lines.Add(text[start..i]);
            start = i + 1;

            // \r\n 视为一个换行符。
            if (c == '\r' && start < text.Length && text[start] == '\n')
            {
                start++;
                i++;
            }
        }

        lines.Add(text[start..]);
        return lines;
    }

    /// <summary>解析 <c>[key:value]</c> 形状的元数据标签。</summary>
    private static bool TryParseMetadata(ReadOnlySpan<char> inner, out string key, out string value)
    {
        key = string.Empty;
        value = string.Empty;

        int colon = inner.IndexOf(':');

        if (colon <= 0)
        {
            return false;
        }

        ReadOnlySpan<char> rawKey = inner[..colon];

        // 标签名必须是纯 ASCII 字母：这样 "00:12.34" 不会被误判成元数据。
        foreach (char c in rawKey)
        {
            if (!char.IsAsciiLetter(c))
            {
                return false;
            }
        }

        key = rawKey.ToString().ToLowerInvariant();
        value = inner[(colon + 1)..].Trim().ToString();
        return true;
    }

    /// <summary>解析 <c>[offset:]</c> 的毫秒值（允许带正负号）。</summary>
    private static bool TryParseOffset(string value, out long offsetUs)
    {
        offsetUs = 0L;
        ReadOnlySpan<char> span = value.AsSpan().Trim();

        if (span.IsEmpty)
        {
            return false;
        }

        bool negative = false;

        if (span[0] is '+' or '-')
        {
            negative = span[0] == '-';
            span = span[1..];
        }

        if (span.IsEmpty
            || !long.TryParse(
                span,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out long milliseconds))
        {
            return false;
        }

        Int128 total = (Int128)milliseconds * Core.Time.TimeUs.PerMillisecond;

        if (total > long.MaxValue)
        {
            return false;
        }

        offsetUs = negative ? -(long)total : (long)total;
        return true;
    }

    /// <summary>应用 <c>[offset:]</c>，并把变成负数的时间钳制到 0（同时记诊断）。</summary>
    private static void ApplyOffset(
        BudgetedImportList<LrcEntry> entries,
        LrcParserOptions options,
        long offsetUs,
        BudgetedImportList<LrcDiagnostic> diagnostics)
    {
        if (!options.ApplyOffset || offsetUs == 0L)
        {
            return;
        }

        for (int i = 0; i < entries.Count; i++)
        {
            LrcEntry entry = entries[i];
            long shifted = entry.StartUs + offsetUs;

            if (shifted < 0L)
            {
                diagnostics.Add(new LrcDiagnostic(
                    LrcDiagnosticKind.NegativeTimeClamped,
                    entry.SourceLineNumber,
                    entry.Text,
                    $"应用 offset 后时间为负（{Core.Time.TimeUs.FormatClock(shifted)}），已钳制到 0。"));

                shifted = 0L;
            }

            entries[i] = entry with { StartUs = shifted };
        }
    }

    /// <summary>按时间稳定重排，并记一条诊断说明原文件是乱序的。</summary>
    private static void SortIfNeeded(
        BudgetedImportList<LrcEntry> entries,
        LrcParserOptions options,
        int firstOutOfOrderLine,
        BudgetedImportList<LrcDiagnostic> diagnostics)
    {
        if (firstOutOfOrderLine < 0)
        {
            return;
        }

        if (!options.SortByTime)
        {
            diagnostics.Add(new LrcDiagnostic(
                LrcDiagnosticKind.OutOfOrder,
                firstOutOfOrderLine,
                string.Empty,
                "原文件的时间顺序与行顺序不一致；按选项保留了原始顺序。"));
            return;
        }

        // 稳定排序：同一时间戳的多条歌词保持它们在文件里的先后。
        entries.Guard?.Reserve(64L + 32L * entries.Count);
        List<LrcEntry> ordered = [.. entries.OrderBy(entry => entry.StartUs)];
        entries.Clear();
        entries.AddRange(ordered);

        diagnostics.Add(new LrcDiagnostic(
            LrcDiagnosticKind.OutOfOrder,
            firstOutOfOrderLine,
            string.Empty,
            "原文件的时间顺序与行顺序不一致，已按时间重排。"));
    }
}
