using System.Globalization;
using System.Text;
using System.Xml;
using System.Text.Json;
using MakoKaraGenerator.Core.Lyrics;

namespace MakoKaraGenerator.Core.Lyrics.Export;

/// <summary>TTML 导出 profile。</summary>
public enum TtmlExportProfile
{
    /// <summary>项目往返 profile，写出全部分支及 mkg 身份属性。</summary>
    Project,
    /// <summary>AMLL 兼容 profile，只写主分支。</summary>
    Amll,
}

/// <summary>可读的 TTML/AMLL 歌词导出器。</summary>
public sealed class TtmlExporter : ILyricsExporter
{
    /// <summary>项目 TTML profile 标识。</summary>
    public const string ProjectId = "ttml.project";
    /// <summary>AMLL TTML profile 标识。</summary>
    public const string AmllId = "ttml.amll";
    private const string TtmlNamespace = "http://www.w3.org/ns/ttml";
    private const string TtsNamespace = "http://www.w3.org/ns/ttml#styling";
    private const string ItunesNamespace = "http://music.apple.com/lyric-ttml-internal";
    private const string MkgNamespace = "urn:makokara:ttml";

    /// <summary>创建 TTML 导出器。</summary>
    public TtmlExporter(TtmlExportProfile profile = TtmlExportProfile.Project) => Profile = profile;
    /// <summary>按 profile 标识创建 TTML 导出器。</summary>
    public TtmlExporter(string profile) => Profile = profile switch
    {
        ProjectId => TtmlExportProfile.Project,
        AmllId => TtmlExportProfile.Amll,
        _ => throw new ArgumentException("未知 TTML 导出 profile。", nameof(profile)),
    };
    /// <summary>当前 profile。</summary>
    public TtmlExportProfile Profile { get; }
    /// <inheritdoc />
    public string Id => Profile == TtmlExportProfile.Project ? ProjectId : AmllId;
    /// <inheritdoc />
    public string DisplayName => Profile == TtmlExportProfile.Project ? "TTML 项目往返" : "TTML/AMLL";

    /// <inheritdoc />
    public LyricsExportPlan Plan(LyricsDocument document, LyricsExportOptions options)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(options);
        if (Profile == TtmlExportProfile.Project && document.SchemaVersion != LyricsDocument.CurrentSchemaVersion)
            throw new ArgumentException("TTML project 仅支持当前歌词 schema。", nameof(document));
        if (Profile == TtmlExportProfile.Project) ValidateProject(document);
        List<LyricsExportLoss> losses = [];
        List<LyricsExportIssue> issues = [];
        IReadOnlyList<LyricBranch> branches = Profile == TtmlExportProfile.Amll
            ? document.Branches.Where(branch => branch.Id == document.PrimaryBranchId).ToArray()
            : document.Branches;
        int skipped = 0;
        int exportable = 0;

        int invalidRubySegments = 0;
        int invalidTokenTimes = 0;
        int unsupportedBranchMetadata = 0;
        int unsupportedLineLinks = 0;
        int unsupportedStyleOverrides = 0;
        int ambiguousRuby = 0;
        int missingRubyConsumerTimes = 0;
        int consumerLinePrecision = 0, consumerTokenPrecision = 0, consumerRubyPrecision = 0;
        int consumerBaseReplacement = 0;
        int consumerUntimedTokens = 0;
        Dictionary<string, string> locations = new(StringComparer.Ordinal);
        void Locate(string feature, string location) => locations.TryAdd(feature, location);
        foreach (LyricBranch branch in branches)
        {
            if (branch.StyleOverride is { IsEmpty: false })
            {
                unsupportedBranchMetadata++;
                Locate("分支样式覆盖", "branch=" + branch.Id);
            }

            foreach (LyricLine line in branch.Lines)
            {
                if (Profile == TtmlExportProfile.Amll && line.Time is not { IsValid: true })
                {
                    skipped++;
                    Locate("无效行时间", "line=" + line.Id);
                    issues.Add(new LyricsExportIssue(LyricsExportIssueKind.LineWithoutTime, line.Id, "行没有有效时间，TTML 不会写出该行。"));
                    continue;
                }
                exportable++;
                void ConsumerPrecision(MakoKaraGenerator.Core.Time.TimeRange? time, string feature, string path, ref int count)
                {
                    if (Profile == TtmlExportProfile.Amll && time is { IsValid: true } range
                        && (range.StartUs % 1000 != 0 || range.EndUs % 1000 != 0
                            || range.StartUs > 9_007_199_254_740_991_000L || range.EndUs > 9_007_199_254_740_991_000L))
                    {
                        count++;
                        Locate(feature, path);
                    }
                }
                ConsumerPrecision(line.Time, "AMLL消费端行时间毫秒精度", "line=" + line.Id, ref consumerLinePrecision);
                if (Profile == TtmlExportProfile.Project)
                {
                    if (line.PrimaryLineId is { } link && !document.Lines.Any(primaryLine => primaryLine.Id == link))
                        issues.Add(new LyricsExportIssue(LyricsExportIssueKind.SourceProjectionLimited, line.Id,
                            "SourceBranchLinkUnavailable：PrimaryLineId=" + link + " 未包含在主分支；原字段保留。"));
                    void Projection(MakoKaraGenerator.Core.Time.TimeRange? time, string path)
                    {
                        if (time is { } range && (!range.IsValid || line.Time is not { IsValid: true } parent
                            || range.StartUs < parent.StartUs || range.EndUs > parent.EndUs))
                            issues.Add(new LyricsExportIssue(LyricsExportIssueKind.SourceProjectionLimited, line.Id,
                                "raw 原始时间已完整保留；标准 TTML 显示区间受到行时间或非法区间限制。位置：" + path));
                    }
                    Projection(line.Time, "line=" + line.Id);
                    foreach (LyricToken token in line.Tokens) Projection(token.Time, "token=" + token.Id);
                    foreach (RubyAnnotation ruby in line.RubyAnnotations)
                        foreach (RubyTextSegment segment in ruby.Segments) Projection(segment.Time, "ruby=" + ruby.Id + "/segment=" + segment.Id);
                }
                if (Profile == TtmlExportProfile.Amll && line.PrimaryLineId is not null)
                {
                    unsupportedLineLinks++;
                    Locate("主歌词行关联", "line=" + line.Id);
                }
                if (line.StyleOverride is { IsEmpty: false })
                {
                    unsupportedStyleOverrides++;
                    Locate("项目专有样式", "line=" + line.Id);
                }
                foreach (LyricToken token in line.Tokens)
                {
                    if (Profile == TtmlExportProfile.Amll && !line.RubyAnnotations.Any(ruby => ruby.BaseTokenId == token.Id)
                        && (token.Time is not { IsValid: true } || !token.IsTimeStatusConsistent))
                    {
                        consumerUntimedTokens++;
                        Locate("AMLL消费端无时间普通元素", "line=" + line.Id + "/token=" + token.Id);
                    }
                    if (token.IsTimeStatusConsistent)
                        ConsumerPrecision(token.Time, "AMLL消费端元素时间毫秒精度", "line=" + line.Id + "/token=" + token.Id, ref consumerTokenPrecision);
                    if (Profile == TtmlExportProfile.Amll && (token.Time is { IsValid: false } || !token.IsTimeStatusConsistent))
                    {
                        invalidTokenTimes++;
                        Locate("无效元素时间", "line=" + line.Id + "/token=" + token.Id);
                    }
                    if (token.StyleOverride is { IsEmpty: false })
                    {
                        unsupportedStyleOverrides++;
                        Locate("项目专有样式", "line=" + line.Id + "/token=" + token.Id);
                    }
                }
                foreach (RubyAnnotation ruby in line.RubyAnnotations)
                {
                    if (!line.Tokens.Any(token => token.Id == ruby.BaseTokenId)
                        || line.RubyAnnotations.First(annotation => annotation.BaseTokenId == ruby.BaseTokenId) != ruby)
                    {
                        ambiguousRuby++;
                        Locate("注音关联无法唯一映射", "line=" + line.Id + "/ruby=" + ruby.Id);
                    }
                    if (Profile == TtmlExportProfile.Amll)
                    {
                        LyricToken? baseToken = line.Tokens.FirstOrDefault(token => token.Id == ruby.BaseTokenId);
                        bool written = baseToken is not null
                            && line.RubyAnnotations.First(annotation => annotation.BaseTokenId == ruby.BaseTokenId) == ruby;
                        if (written)
                        {
                            var timedSegments = ruby.EffectiveSegments.Where(segment => segment.IsTimeStatusConsistent
                                && segment.Time is { IsValid: true }).Select(segment => segment.Time!.Value).ToArray();
                            decimal ConsumerMicroseconds(long value) => decimal.Floor(value / 1000m + 0.5m) * 1000m;
                            decimal start = timedSegments.Length == 0 ? 0 : timedSegments.Min(time => ConsumerMicroseconds(time.StartUs));
                            decimal end = timedSegments.Length == 0 ? 0 : timedSegments.Max(time => ConsumerMicroseconds(time.EndUs));
                            if (!baseToken!.IsTimeStatusConsistent || baseToken.Time is not { IsValid: true } original
                                || start != original.StartUs || end != original.EndUs)
                            {
                                consumerBaseReplacement++;
                                Locate("AMLL消费端基础元素时间由注音范围替代", "line=" + line.Id + "/token=" + baseToken.Id + "/ruby=" + ruby.Id);
                            }
                        }
                    }
                    if (Profile == TtmlExportProfile.Amll && (ruby.RelativeScale != 0.5 || ruby.GapPx != 2 || ruby.Alignment != RubyAlignment.Center))
                    {
                        unsupportedStyleOverrides++;
                        Locate("项目专有样式", "line=" + line.Id + "/ruby=" + ruby.Id);
                    }
                    foreach (RubyTextSegment segment in ruby.EffectiveSegments)
                    {
                        bool written = line.Tokens.Any(token => token.Id == ruby.BaseTokenId)
                            && line.RubyAnnotations.First(annotation => annotation.BaseTokenId == ruby.BaseTokenId) == ruby;
                        if (Profile == TtmlExportProfile.Amll && written)
                        {
                            if (segment.Time is null || segment.Time is not { IsValid: true } || !segment.IsTimeStatusConsistent)
                            {
                                missingRubyConsumerTimes++;
                                Locate("AMLL消费端无时间注音片段", "line=" + line.Id + "/ruby=" + ruby.Id + "/segment=" + segment.Id);
                            }
                            else ConsumerPrecision(segment.Time, "AMLL消费端注音时间毫秒精度",
                                "line=" + line.Id + "/ruby=" + ruby.Id + "/segment=" + segment.Id, ref consumerRubyPrecision);
                        }
                        if (Profile == TtmlExportProfile.Amll && (segment.Time is { IsValid: false } || !segment.IsTimeStatusConsistent))
                        {
                            invalidRubySegments++;
                            Locate("无效注音片段时间", "line=" + line.Id + "/ruby=" + ruby.Id + "/segment=" + segment.Id);
                        }
                    }
                }
            }
        }
        if (skipped > 0)
            losses.Add(new LyricsExportLoss("无效行时间", skipped, "没有有效行时间的内容无法安全写入 TTML，已列入跳过报告。"));
        if (invalidRubySegments > 0)
            losses.Add(new LyricsExportLoss("无效注音片段时间", invalidRubySegments, "注音片段时间无效或状态不一致，输出时省略该片段时间属性。"));
        if (invalidTokenTimes > 0)
            losses.Add(new LyricsExportLoss("无效元素时间", invalidTokenTimes, "元素时间无效或状态不一致，输出时省略该元素时间属性。"));
        if (unsupportedBranchMetadata > 0)
            losses.Add(new LyricsExportLoss("分支样式覆盖", unsupportedBranchMetadata, "TTML 不保存分支项目样式覆盖。"));
        if (unsupportedLineLinks > 0)
            losses.Add(new LyricsExportLoss("主歌词行关联", unsupportedLineLinks, "TTML 输出没有保存 PrimaryLineId，重新导入时不会恢复跟随主行关联。"));
        if (unsupportedStyleOverrides > 0)
            losses.Add(new LyricsExportLoss("项目专有样式", unsupportedStyleOverrides, "TTML/AMLL 输出不保存行、元素或注音的项目样式覆盖。"));

        if (Profile == TtmlExportProfile.Amll && document.Branches.Length > branches.Count)
            losses.Add(new LyricsExportLoss("其它歌词分支", document.Branches.Length - branches.Count, "AMLL profile 只输出主歌词分支。"));
        if (Profile == TtmlExportProfile.Amll && exportable > 0)
            losses.Add(new LyricsExportLoss("项目身份和时间状态", exportable, "AMLL 只保留 line key；分支/元素/注音/片段身份及编辑时间状态无法恢复。"));
        int mismatches = branches.SelectMany(branch => branch.Lines).Count(line => line.Tokens.Length > 0
            && line.Text != string.Concat(line.Tokens.Select(token => token.Text)));
        if (Profile == TtmlExportProfile.Amll && mismatches > 0)
            losses.Add(new LyricsExportLoss("独立行正文", mismatches, "AMLL 从元素输出正文；与元素拼接不同的权威 line.Text 无法同时表示。"));
        if (ambiguousRuby > 0)
            losses.Add(new LyricsExportLoss("注音关联无法唯一映射", ambiguousRuby, "无法绑定的注音未写出；基础正文保留。"));
        if (consumerUntimedTokens > 0)
            losses.Add(new("AMLL消费端无时间普通元素", consumerUntimedTokens,
                "XML 保留正文且不伪造时间；官方 AMLL TTML parser 不将缺少 begin/end 的普通元素加入 words。与有时间元素混合时该元素不会进入消费端逐字列表；整行未计时时只能保留行正文。"));
        if (consumerBaseReplacement > 0)
            losses.Add(new("AMLL消费端基础元素时间由注音范围替代", consumerBaseReplacement,
                "XML 保留基础元素原时间；官方 AMLL TTML parser 忽略 ruby base/container 时间，按有时间注音片段最早开始/最晚结束的毫秒值建立元素时间，无计时片段时建立 0..0。"));
        if (missingRubyConsumerTimes > 0)
            losses.Add(new("AMLL消费端无时间注音片段", missingRubyConsumerTimes,
                "XML 保留片段正文且不伪造时间；官方 AMLL TTML parser 仅采用同时有 begin/end 的注音，消费端会省略这些片段。"));
        void PrecisionLoss(string feature, int count)
        {
            if (count > 0) losses.Add(new(feature, count,
                "XML 保留微秒原值；官方 AMLL TTML parser 使用 JavaScript Number 并按 Math.round 换算为毫秒，非整毫秒或超出 Number 安全整数毫秒范围的值存在消费精度损失。计数为至少一个端点属于该范围的区间数。"));
        }
        PrecisionLoss("AMLL消费端行时间毫秒精度", consumerLinePrecision);
        PrecisionLoss("AMLL消费端元素时间毫秒精度", consumerTokenPrecision);
        PrecisionLoss("AMLL消费端注音时间毫秒精度", consumerRubyPrecision);
        for (int i = 0; i < losses.Count; i++)
        {
            LyricsExportLoss loss = losses[i];
            string location = locations.GetValueOrDefault(loss.Feature) ?? "document=" + document.PrimaryBranchId;
            losses[i] = loss with { Message = loss.Message + " 首个位置：" + location };
        }
        LyricsExportPlan plan = new(losses, exportable, skipped, issues);
        using BoundedBuffer report = new(64 * 1024);
        JsonSerializer.Serialize(report, plan);
        return plan;
    }

    /// <inheritdoc />
    public LyricsExportReport Export(LyricsDocument document, Stream output, LyricsExportOptions options)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(options);
        LyricsExportPlan plan = Plan(document, options);
        using BoundedBuffer candidate = new(4 * 1024 * 1024);
        XmlWriterSettings settings = new() { Indent = false, OmitXmlDeclaration = false, Encoding = new UTF8Encoding(options.WriteByteOrderMark), NewLineChars = options.NewLine, NewLineHandling = NewLineHandling.Entitize };
        using (XmlWriter writer = XmlWriter.Create(candidate, settings))
        {
            writer.WriteStartDocument();
            writer.WriteStartElement("tt", TtmlNamespace);
            writer.WriteAttributeString("xmlns", "tts", null, TtsNamespace);
            writer.WriteAttributeString("xmlns", "itunes", null, ItunesNamespace);
            if (Profile == TtmlExportProfile.Project)
            {
                writer.WriteAttributeString("xmlns", "mkg", null, MkgNamespace);
                Extension(writer, "profile", ProjectId);
                Extension(writer, "version", "1");
                Extension(writer, "timing-profile", "ttml2-media");
                Extension(writer, "primary-branch-id", document.PrimaryBranchId);
                Extension(writer, "lyrics-schema", document.SchemaVersion.ToString(CultureInfo.InvariantCulture));
            }
            else writer.WriteAttributeString("itunes", "timing", ItunesNamespace, "Word");
            writer.WriteStartElement("head", TtmlNamespace);
            writer.WriteStartElement("metadata", TtmlNamespace);
            writer.WriteAttributeString("format", Profile == TtmlExportProfile.Project ? ProjectId : AmllId);
            writer.WriteEndElement();
            writer.WriteEndElement();
            writer.WriteStartElement("body", TtmlNamespace);
            IEnumerable<LyricBranch> branches = Profile == TtmlExportProfile.Amll
                ? document.Branches.Where(branch => branch.Id == document.PrimaryBranchId)
                : document.Branches;
            int exported = 0;
            int writtenRubySegments = 0;
            int droppedRubyTimes = 0;
            int droppedTokenTimes = 0;
            foreach (LyricBranch branch in branches)
            {
                writer.WriteStartElement("div", TtmlNamespace);
                if (Profile == TtmlExportProfile.Project)
                {
                    Extension(writer, "branch-id", branch.Id);
                    Extension(writer, "branch-name", branch.Name);
                    Extension(writer, "branch-role", branch.Role.ToString());
                    Extension(writer, "timing-mode", branch.TimingMode.ToString());
                }
                if (branch.Language is not null) writer.WriteAttributeString("xml", "lang", XmlNamespace, branch.Language);
                foreach (LyricLine line in branch.Lines)
                {
                    if (Profile == TtmlExportProfile.Amll && line.Time is not { IsValid: true }) continue;
                    writer.WriteStartElement("p", TtmlNamespace);
                    WriteTimeAttributes(writer, line.Time);
                    if (Profile == TtmlExportProfile.Project)
                    {
                        Extension(writer, "line-id", line.Id);
                        Extension(writer, "line-text", line.Text);
                        if (line.PrimaryLineId is not null) Extension(writer, "primary-line-id", line.PrimaryLineId);
                        WriteProjectTime(writer, line.Time, line.TimingStatus);
                    }
                    writer.WriteAttributeString("itunes", "key", ItunesNamespace, line.Id);
                    if (Profile == TtmlExportProfile.Amll) writer.WriteAttributeString("itunes", "timing", ItunesNamespace, "Word");
                    writtenRubySegments += WriteLine(writer, line);
                    if (Profile == TtmlExportProfile.Amll)
                    {
                        droppedTokenTimes += line.Tokens.Count(token => token.Time is not null && (token.Time is not { IsValid: true } || !token.IsTimeStatusConsistent));
                        droppedRubyTimes += line.RubyAnnotations.Where(ruby => line.Tokens.Any(token => token.Id == ruby.BaseTokenId)
                            && line.RubyAnnotations.First(annotation => annotation.BaseTokenId == ruby.BaseTokenId) == ruby)
                            .SelectMany(ruby => ruby.EffectiveSegments).Count(segment => segment.Time is not null
                                && (segment.Time is not { IsValid: true } || !segment.IsTimeStatusConsistent));
                    }
                    writer.WriteEndElement();
                    exported++;
                }
                writer.WriteEndElement();
            }
            writer.WriteEndElement();
            writer.WriteEndElement();
            writer.WriteEndDocument();
            writer.Flush();
            candidate.Position = 0;
            candidate.CopyTo(output);
            return new LyricsExportReport { ExporterId = Id, ExporterName = DisplayName, SourceLineCount = document.AllLines.Count(),
                ExportedLineCount = exported, SkippedLineCount = plan.SkippedLineCount, DroppedTokenTimeCount = droppedTokenTimes,
                WrittenRubySegmentCount = writtenRubySegments,
                SkippedRubySegmentCount = document.AllLines.SelectMany(line => line.RubyAnnotations).Sum(ruby => ruby.EffectiveSegments.Count) - writtenRubySegments,
                DroppedRubySegmentTimeCount = droppedRubyTimes,
                RoundedTimeCount = 0, WrittenByteCount = candidate.Length, MetadataCount = 1, Plan = plan };
        }
    }

    private int WriteLine(XmlWriter writer, LyricLine line)
    {
        int segmentsWritten = 0;
        if (line.Tokens.IsEmpty) { writer.WriteString(line.Text); return 0; }
        foreach (LyricToken token in line.Tokens)
        {
            RubyAnnotation? ruby = line.RubyAnnotations.FirstOrDefault(annotation => annotation.BaseTokenId == token.Id);
            bool tokenWrapper = ruby is null || Profile == TtmlExportProfile.Project;
            if (tokenWrapper) writer.WriteStartElement("span", TtmlNamespace);
            MakoKaraGenerator.Core.Time.TimeRange? lineTime = Profile == TtmlExportProfile.Project && line.Time is { IsValid: true } ? line.Time : null;
            if (ruby is null) WriteTimeAttributes(writer, token.IsTimeStatusConsistent ? token.Time : null, lineTime?.StartUs ?? 0);
            if (Profile == TtmlExportProfile.Project)
            {
                Extension(writer, "token-id", token.Id);
                WriteProjectTime(writer, token.Time, token.TimingStatus);
            }
            if (ruby is null)
            {
                writer.WriteString(token.Text);
            }
            else
            {
                writer.WriteStartElement("span", TtmlNamespace); writer.WriteAttributeString("tts", "ruby", TtsNamespace, "container");
                if (Profile == TtmlExportProfile.Project)
                {
                    Extension(writer, "ruby-id", ruby.Id);
                    if (ruby.Language is not null) Extension(writer, "language", ruby.Language);
                    Extension(writer, "base-token-id", ruby.BaseTokenId);
                    Extension(writer, "scale", ruby.RelativeScale.ToString("R", CultureInfo.InvariantCulture));
                    Extension(writer, "gap", ruby.GapPx.ToString("R", CultureInfo.InvariantCulture));
                    Extension(writer, "alignment", ruby.Alignment.ToString());
                }
                writer.WriteStartElement("span", TtmlNamespace); writer.WriteAttributeString("tts", "ruby", TtsNamespace, "base");
                WriteTimeAttributes(writer, token.IsTimeStatusConsistent ? token.Time : null, lineTime?.StartUs ?? 0);
                writer.WriteString(token.Text); writer.WriteEndElement();
                writer.WriteStartElement("span", TtmlNamespace); writer.WriteAttributeString("tts", "ruby", TtsNamespace, "textContainer");
                if (!string.IsNullOrWhiteSpace(ruby.Language)) writer.WriteAttributeString("xml", "lang", XmlNamespace, ruby.Language);
                foreach (RubyTextSegment segment in ruby.EffectiveSegments)
                {
                    writer.WriteStartElement("span", TtmlNamespace); writer.WriteAttributeString("tts", "ruby", TtsNamespace, "text");
                    long reference = Profile == TtmlExportProfile.Project
                        ? line.Time is { IsValid: true } lineRange ? lineRange.StartUs : 0 : 0;
                    WriteTimeAttributes(writer, segment.IsTimeStatusConsistent ? segment.Time : null, reference);
                    segmentsWritten++;
                    if (Profile == TtmlExportProfile.Project)
                    {
                        Extension(writer, "segment-id", segment.Id);
                        WriteProjectTime(writer, segment.Time, segment.TimingStatus);
                    }
                    writer.WriteString(segment.Text); writer.WriteEndElement();
                }
                writer.WriteEndElement(); writer.WriteEndElement();
            }
            if (tokenWrapper) writer.WriteEndElement();
        }
        return segmentsWritten;
    }

    private static void ValidateProject(LyricsDocument document)
    {
        HashSet<string> ids = new(StringComparer.Ordinal);
        void Identity(string id)
        {
            if (string.IsNullOrWhiteSpace(id) || !ids.Add(id)) throw new ArgumentException("TTML project 身份为空或重复。", nameof(document));
        }
        if (document.PrimaryBranch is null) throw new ArgumentException("TTML project 缺少主分支。", nameof(document));
        foreach (LyricBranch branch in document.Branches)
        {
            Identity(branch.Id);
            if (!branch.IsValid) throw new ArgumentException("TTML project 分支元数据无效。", nameof(document));
            foreach (LyricLine line in branch.Lines)
            {
                Identity(line.Id);
                if (!line.IsTimeStatusConsistent || !Enum.IsDefined(line.TimingStatus)
                    || line.PrimaryLineId is { } primary && string.IsNullOrWhiteSpace(primary))
                    throw new ArgumentException("TTML project 行时间或关联无效。", nameof(document));
                HashSet<string> tokens = [];
                foreach (LyricToken token in line.Tokens)
                {
                    Identity(token.Id); tokens.Add(token.Id);
                    if (!token.IsTimeStatusConsistent || !Enum.IsDefined(token.TimingStatus))
                        throw new ArgumentException("TTML project 元素时间与状态不一致。", nameof(document));
                }
                HashSet<string> bases = [];
                foreach (RubyAnnotation ruby in line.RubyAnnotations)
                {
                    Identity(ruby.Id);
                    if (!ruby.IsValid || !tokens.Contains(ruby.BaseTokenId) || !bases.Add(ruby.BaseTokenId))
                        throw new ArgumentException("TTML project 注音关联或结构无效。", nameof(document));
                    foreach (RubyTextSegment segment in ruby.Segments) Identity(segment.Id);
                }
            }
        }
    }

    private static void WriteTimeAttributes(XmlWriter writer, MakoKaraGenerator.Core.Time.TimeRange? time, long reference = 0)
    {
        if (time is not { IsValid: true } range || range.StartUs < reference) return;
        writer.WriteAttributeString("begin", FormatTime(range.StartUs - reference));
        writer.WriteAttributeString("end", FormatTime(range.EndUs - reference));
    }
    private static void WriteProjectTime(XmlWriter writer, MakoKaraGenerator.Core.Time.TimeRange? time, TimingStatus status)
    {
        Extension(writer, "timing-status", status.ToString());
        if (time is not { } range) return;
        Extension(writer, "start-us", range.StartUs.ToString(CultureInfo.InvariantCulture));
        Extension(writer, "end-us", range.EndUs.ToString(CultureInfo.InvariantCulture));
    }
    private static void Extension(XmlWriter writer, string name, string value) => writer.WriteAttributeString("mkg", name, MkgNamespace, value);
    private static string FormatTime(long microseconds) => (microseconds / 1_000_000m).ToString("0.######", CultureInfo.InvariantCulture) + "s";
    // Buffer a complete candidate before writing any bytes to the caller. The
    // HTTP publisher separately enforces its resource/receipt budgets.
    private sealed class BoundedBuffer(int maximum) : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            if (count > maximum - Length) throw new IOException("TTML 输出或报告超过字节预算。");
            base.Write(buffer, offset, count);
        }
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (buffer.Length > maximum - Length) throw new IOException("TTML 输出或报告超过字节预算。");
            base.Write(buffer);
        }
        public override void WriteByte(byte value)
        {
            if (Length == maximum) throw new IOException("TTML 输出或报告超过字节预算。");
            base.WriteByte(value);
        }
    }
    private const string XmlNamespace = "http://www.w3.org/XML/1998/namespace";
}

/// <summary>TTML 项目 profile 便捷导出器。</summary>
public sealed class TtmlProjectExporter : ILyricsExporter
{
    private readonly TtmlExporter _inner = new(TtmlExportProfile.Project);
    /// <inheritdoc />
    public string Id => _inner.Id;
    /// <inheritdoc />
    public string DisplayName => _inner.DisplayName;
    /// <inheritdoc />
    public LyricsExportPlan Plan(LyricsDocument document, LyricsExportOptions options) => _inner.Plan(document, options);
    /// <inheritdoc />
    public LyricsExportReport Export(LyricsDocument document, Stream output, LyricsExportOptions options) => _inner.Export(document, output, options);
}

/// <summary>TTML/AMLL profile 便捷导出器。</summary>
public sealed class TtmlAmllExporter : ILyricsExporter
{
    private readonly TtmlExporter _inner = new(TtmlExportProfile.Amll);
    /// <inheritdoc />
    public string Id => _inner.Id;
    /// <inheritdoc />
    public string DisplayName => _inner.DisplayName;
    /// <inheritdoc />
    public LyricsExportPlan Plan(LyricsDocument document, LyricsExportOptions options) => _inner.Plan(document, options);
    /// <inheritdoc />
    public LyricsExportReport Export(LyricsDocument document, Stream output, LyricsExportOptions options) => _inner.Export(document, output, options);
}
