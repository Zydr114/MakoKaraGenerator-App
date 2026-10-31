using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using MakoKaraGenerator.Core.Diagnostics;
using MakoKaraGenerator.Core.Text;
using MakoKaraGenerator.Core.Time;

namespace MakoKaraGenerator.Core.Lyrics.Import;

/// <summary>有界 TTML2 media/AMLL 导入器；project profile 恢复领域身份与关联。</summary>
public sealed class TtmlImporter : ILyricsImporter
{
    /// <summary>导入器标识。</summary>
    public const string IdValue = "ttml";
    internal const string ProjectNamespace = "urn:makokara:ttml";
    private const string TtmlNamespace = "http://www.w3.org/ns/ttml";
    private const string ItunesNamespace = "http://music.apple.com/lyric-ttml-internal";
    private const string StylingNamespace = "http://www.w3.org/ns/ttml#styling";
    private const int MaximumInputBytes = 4 * 1024 * 1024;
    /// <inheritdoc />
    public string Id => IdValue;
    /// <inheritdoc />
    public string DisplayName => "TTML 逐字歌词（Apple Music / AMLL）";
    /// <inheritdoc />
    public string SourceFormat => "TTML2 media 时间树／AMLL absolute／ttml.project v1";
    /// <inheritdoc />
    public bool CanImport(string fileName, ReadOnlySpan<byte> header) =>
        fileName.EndsWith(".ttml", StringComparison.OrdinalIgnoreCase)
        || Encoding.UTF8.GetString(header).Contains("http://www.w3.org/ns/ttml", StringComparison.Ordinal);
    /// <inheritdoc />
    public string MatchReason(string fileName, ReadOnlySpan<byte> header) =>
        fileName.EndsWith(".ttml", StringComparison.OrdinalIgnoreCase) ? "扩展名为 .ttml" : "内容含 TTML 命名空间";

    /// <inheritdoc />
    public LyricsImportResult Import(Stream input, LyricsImportContext context)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(context);
        using MemoryStream bytes = new();
        byte[] chunk = new byte[8192];
        int read;
        while ((read = input.Read(chunk)) > 0)
        {
            if (bytes.Length + read > MaximumInputBytes) throw new LyricsImportPreparationLimitException();
            context.PreparationGuard?.Reserve(read);
            bytes.Write(chunk, 0, read);
        }
        LyricsTextInfo decoded = LyricsTextDecoder.Decode(bytes.ToArray(), context.EncodingOverride);
        BudgetedImportList<LyricsImportIssue> issues = new(context.PreparationGuard);
        BudgetedImportList<string> discarded = new(context.PreparationGuard);
        string profile = "ttml2-media";
        LyricsDocument document = LyricsDocument.Create(language: context.Language);
        int derivedLineEnds = 0;
        int fallbackLineEnds = 0;
        try
        {
            XElement? root = LoadXml(decoded.Text, context.PreparationGuard, issues);
            if (root is null || root.Name != XName.Get("tt", TtmlNamespace))
                throw new FormatException("根元素必须为 TTML tt。");
            string? declaredProfile = Extension(root, "profile");
            if (declaredProfile is not null and not "ttml.project" and not "ttml.amll")
                throw new FormatException("未知 TTML project profile，禁止降级导入。");
            bool project = declaredProfile == "ttml.project";
            string? explicitProfile = Extension(root, "timing-profile");
            if (explicitProfile is not null and not "ttml2-media" and not "amll-absolute")
                throw new FormatException("未知 TTML timing-profile。");
            string[] appleModes = root.DescendantsAndSelf()
                .Select(element => (string?)element.Attribute(XName.Get("timing", ItunesNamespace)))
                .Where(value => value is not null).Cast<string>().ToArray();
            if (appleModes.Any(value => value is not "Word" and not "Line"))
                throw new FormatException("未知 itunes:timing 模式。");
            bool amllMarker = declaredProfile == "ttml.amll" || appleModes.Length > 0
                || root.Descendants().Any(element => element.Name == XName.Get("meta", "http://www.example.com/ns/amll")
                    || element.Name == XName.Get("iTunesMetadata", ItunesNamespace));
            if (explicitProfile == "ttml2-media" && amllMarker)
                throw new FormatException("TTML media 与 AMLL absolute 声明冲突。");
            bool absolute = explicitProfile == "amll-absolute" || amllMarker;
            if (!project && root.DescendantsAndSelf().Attributes().Any(attribute => attribute.Name.NamespaceName == ProjectNamespace
                && attribute.Name.LocalName is not "profile" and not "timing-profile"))
                throw new FormatException("存在未声明版本的 TTML project 扩展，禁止降级导入。");
            profile = project ? "ttml.project-v1/" + (absolute ? "amll-absolute" : "ttml2-media")
                : absolute ? "amll-absolute" : "ttml2-media";
            if (project && Extension(root, "version") != "1") throw new FormatException("未知 TTML project 扩展版本。");
            XElement? body = root.Element(XName.Get("body", TtmlNamespace));
            if (body is null) throw new FormatException("TTML 缺少 body。");
            TtmlTimeTree times = new(root, absolute);
            times.Resolve(body);
            ScanUnmapped(root, discarded);
            Builder builder = new(context, issues, discarded, times, project, root);
            document = builder.Build(body);
            derivedLineEnds = builder.DerivedLineEndCount;
            fallbackLineEnds = builder.FallbackLineEndCount;
        }
        catch (Exception exception) when (exception is XmlException or FormatException or OverflowException or ArgumentException)
        {
            issues.Add(new LyricsImportIssue(LyricsImportIssueKind.MalformedTag, IssueSeverity.Error, 0,
                string.Empty, "TTML 无法安全映射：" + exception.Message));
        }
        if (decoded.IsFallbackDecoding)
            issues.Add(new LyricsImportIssue(LyricsImportIssueKind.EncodingAssumed, IssueSeverity.Warning, 0,
                decoded.EncodingName, "无法确定编码，已按所报告编码近似解码。"));
        foreach (string feature in discarded.Distinct(StringComparer.Ordinal))
            issues.Add(new LyricsImportIssue(LyricsImportIssueKind.DiscardedFeature, IssueSeverity.Info, 0,
                feature, "未映射特性：" + feature));
        int timed = document.EnumerateTokens().Count(pair => pair.Token.Time is not null);
        LyricsImportReport report = new()
        {
            ImporterId = Id, ImporterName = DisplayName, SourceFormat = SourceFormat + " [" + profile + "]",
            SourceLineCount = decoded.Text.Count(character => character == '\n') + 1,
            LineCount = document.AllLines.Count(), TokenCount = document.TokenCount, TimedTokenCount = timed,
            MissingTokenCount = document.TokenCount - timed,
            EstimatedTokenCount = document.EnumerateTokens().Count(pair => pair.Token.TimingStatus == TimingStatus.Estimated),
            SkippedLineCount = 0, MergedContinuationCount = 0, DerivedLineEndCount = derivedLineEnds, FallbackLineEndCount = fallbackLineEnds,
            OffsetUs = 0, EncodingName = decoded.EncodingName, HadByteOrderMark = decoded.HadByteOrderMark,
            NewLineKind = decoded.NewLineKind, Issues = issues, DiscardedFeatures = discarded,
        };
        return new LyricsImportResult(document, report);
    }

    private static XElement? LoadXml(string text, LyricsImportPreparationGuard? guard, BudgetedImportList<LyricsImportIssue> issues)
    {
        guard?.Lines(text);
        // Before constructing XML nodes/attributes, ID maps, or timing IR. This is
        // a cumulative logical work budget, not an estimate of the CLR heap.
        guard?.Reserve(4L * text.Length + 1536L * text.Count(character => character == '<'));
        XmlReaderSettings settings = new() { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
            IgnoreComments = true, IgnoreProcessingInstructions = true, MaxCharactersInDocument = MaximumInputBytes };
        using StringReader input = new(text);
        using XmlReader reader = XmlReader.Create(input, settings);
        Stack<XElement> parents = new();
        XElement? root = null;
        try
        {
            while (reader.Read())
            {
                if (reader.Depth > 64) throw new XmlException("TTML XML 嵌套超过 64 层。");
                if (reader.NodeType == XmlNodeType.Element)
                {
                    XElement element = new(XName.Get(reader.LocalName, reader.NamespaceURI));
                    if (reader.HasAttributes)
                    {
                        while (reader.MoveToNextAttribute())
                        {
                            XName name = reader.Prefix == "xmlns" ? XNamespace.Xmlns + reader.LocalName
                                : reader.Name == "xmlns" ? XName.Get("xmlns") : XName.Get(reader.LocalName, reader.NamespaceURI);
                            element.Add(new XAttribute(name, reader.Value));
                        }
                        reader.MoveToElement();
                    }
                    if (parents.TryPeek(out XElement? parent)) parent.Add(element);
                    else if (root is null) root = element;
                    else throw new XmlException("TTML 有多个根元素。");
                    if (!reader.IsEmptyElement) parents.Push(element);
                }
                else if (reader.NodeType == XmlNodeType.EndElement) parents.Pop();
                else if (reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace)
                    if (parents.TryPeek(out XElement? parent)) parent.Add(new XText(reader.Value));
            }
        }
        catch (XmlException exception)
        {
            // Retain only completed paragraphs from a well-formed prefix. No
            // entity expansion or partly constructed paragraph reaches the model.
            foreach (XElement partial in parents.Where(element => element.Name.LocalName == "p").ToArray()) partial.Remove();
            issues.Add(new LyricsImportIssue(LyricsImportIssueKind.MalformedTag, IssueSeverity.Error, 0,
                string.Empty, "TTML XML 无效，仅保留已完整读取的段落：" + exception.Message));
        }
        return root;
    }

    private static void ScanUnmapped(XElement root, BudgetedImportList<string> discarded)
    {
        HashSet<string> features = new(StringComparer.Ordinal);
        foreach (XElement element in root.DescendantsAndSelf())
        {
            if (element.Name.NamespaceName == "http://www.w3.org/ns/ttml#metadata")
                features.Add(element.Name.LocalName == "agent" ? "ttm:agent 多歌手元数据" : "TTML head metadata");
            if (element.Name.NamespaceName == TtmlNamespace && element.Name.LocalName is "styling" or "style" or "layout" or "region")
                features.Add("TTML 布局/样式资源");
            if (element.Name.NamespaceName == ItunesNamespace && element.Name.LocalName == "iTunesMetadata")
                features.Add("Apple translation/transliteration head sidecar");
            if (element.Name.NamespaceName == "http://www.example.com/ns/amll") features.Add("AMLL metadata 扩展");
            if (element.Name.NamespaceName is not TtmlNamespace and not StylingNamespace and not ItunesNamespace
                and not ProjectNamespace and not "http://www.w3.org/ns/ttml#metadata" and not "http://www.example.com/ns/amll")
                features.Add("未知命名空间元素（保留可识别正文）");
            foreach (XAttribute attribute in element.Attributes())
            {
                if (attribute.Name.LocalName == "agent") features.Add("ttm:agent 多歌手元数据");
                if (attribute.Name.NamespaceName == ItunesNamespace && attribute.Name.LocalName is "song-part" or "songPart") features.Add("itunes:song-part 歌曲段落");
                if (attribute.Name.NamespaceName == StylingNamespace && attribute.Name.LocalName != "ruby") features.Add("TTML 样式覆盖");
                if (attribute.Name.LocalName == "role") features.Add("ttm:role 旁轨角色");
                if (attribute.IsNamespaceDeclaration) continue;
                string ns = attribute.Name.NamespaceName;
                string name = attribute.Name.LocalName;
                bool known = ns switch
                {
                    ProjectNamespace => name is "profile" or "version" or "timing-profile" or "lyrics-schema" or "primary-branch-id"
                        or "branch-id" or "branch-name" or "branch-role" or "timing-mode" or "line-id" or "line-text" or "primary-line-id"
                        or "token-id" or "timing-status" or "start-us" or "end-us" or "ruby-id" or "base-token-id" or "language"
                        or "scale" or "gap" or "alignment" or "segment-id",
                    "" => name is "begin" or "end" or "dur" or "timeContainer" or "format" or "frameRate" or "frameRateMultiplier"
                        or "subFrameRate" or "tickRate" or "timeBase" or "dropMode",
                    "http://www.w3.org/XML/1998/namespace" => name is "lang" or "space",
                    "http://www.w3.org/ns/ttml#parameter" => name is "frameRate" or "frameRateMultiplier" or "subFrameRate"
                        or "tickRate" or "timeBase" or "dropMode",
                    ItunesNamespace => name is "key" or "timing" or "song-part" or "songPart",
                    StylingNamespace => name == "ruby",
                    "http://www.w3.org/ns/ttml#metadata" => name is "agent" or "role",
                    _ => false,
                };
                if (!known) features.Add("未映射 XML 属性（含未知扩展；正文保留）");
            }
        }
        foreach (string feature in features) discarded.Add(feature);
    }

    internal static string? Extension(XElement element, string name) => (string?)element.Attribute(XName.Get(name, ProjectNamespace));
    private static string? Language(XElement element) => element.AncestorsAndSelf().Select(node => (string?)node.Attribute(XNamespace.Xml + "lang")).FirstOrDefault(value => value is not null);
    private static string? RubyKind(XElement element) => (string?)element.Attribute(XName.Get("ruby", StylingNamespace));

    private sealed class Builder(LyricsImportContext context, BudgetedImportList<LyricsImportIssue> issues,
        BudgetedImportList<string> discarded, TtmlTimeTree times, bool project, XElement root)
    {
        private readonly HashSet<string> _ids = new(StringComparer.Ordinal);
        private int _next;
        internal int DerivedLineEndCount { get; private set; }
        internal int FallbackLineEndCount { get; private set; }

        internal LyricsDocument Build(XElement body)
        {
            if (project)
            {
                // Reserve the complete identity namespace before allocating IDs.
                foreach (XElement element in root.DescendantsAndSelf())
                    foreach (XAttribute attribute in element.Attributes().Where(attribute => attribute.Name.NamespaceName == ProjectNamespace
                        && attribute.Name.LocalName is "branch-id" or "line-id" or "token-id" or "ruby-id" or "segment-id"))
                        if (string.IsNullOrWhiteSpace(attribute.Value) || !_ids.Add(attribute.Value))
                            throw new FormatException("TTML project 身份为空或重复。");
                if (Required(root, "lyrics-schema") != LyricsDocument.CurrentSchemaVersion.ToString(CultureInfo.InvariantCulture))
                    throw new FormatException("TTML project 歌词 schema 不支持。");
                XElement[] divs = body.Elements(XName.Get("div", TtmlNamespace)).ToArray();
                ImmutableArray<LyricBranch>.Builder branches = ImmutableArray.CreateBuilder<LyricBranch>(divs.Length);
                foreach (XElement div in divs)
                {
                    if (div.Descendants(XName.Get("div", TtmlNamespace)).Any())
                        throw new FormatException("TTML project 分支 div 不能嵌套。");
                    string id = Required(div, "branch-id");
                    LyricBranch branch = LyricBranch.Create(id, Required(div, "branch-name"),
                        EnumValue<LyricBranchRole>(Required(div, "branch-role")),
                        div.Descendants(XName.Get("p", TtmlNamespace)).Select(ParseLine), Language(div),
                        EnumValue<LyricBranchTimingMode>(Required(div, "timing-mode")));
                    branches.Add(branch);
                }
                string primary = Required(root, "primary-branch-id");
                if (!branches.Any(branch => branch.Id == primary)) throw new FormatException("TTML project 主分支不存在。");
                LyricsDocument document = new() { PrimaryBranchId = primary, Branches = branches.ToImmutable(), SchemaVersion = LyricsDocument.CurrentSchemaVersion };
                string[] recoveredIds = [.. document.Branches.Select(branch => branch.Id), .. document.AllLines.Select(line => line.Id),
                    .. document.EnumerateTokens().Select(pair => pair.Token.Id),
                    .. document.AllLines.SelectMany(line => line.RubyAnnotations).Select(ruby => ruby.Id),
                    .. document.AllLines.SelectMany(line => line.RubyAnnotations).SelectMany(ruby => ruby.Segments).Select(segment => segment.Id)];
                if (recoveredIds.Length != _ids.Count || recoveredIds.Distinct(StringComparer.Ordinal).Count() != recoveredIds.Length)
                    throw new FormatException("TTML project 有身份未映射或位置无效。");
                HashSet<string> primaryLines = [.. document.Lines.Select(line => line.Id)];
                foreach (LyricLine line in document.AllLines)
                {
                    if (line.PrimaryLineId is not { } id) continue;
                    if (string.IsNullOrWhiteSpace(id)) throw new FormatException("TTML project 主行关联身份为空。");
                    if (!primaryLines.Contains(id))
                        issues.Add(new LyricsImportIssue(LyricsImportIssueKind.DiscardedFeature, IssueSeverity.Warning, 0,
                            line.Id, "SourceBranchLinkUnavailable：原 PrimaryLineId=" + id + " 未包含在文档主分支，已保留原关联供显式重关联。"));
                }
                return document;
            }
            if (body.Descendants(XName.Get("div", TtmlNamespace)).Count() > 1)
                discarded.Add("多个 div 章节合并到同一歌词来源（未声明 project 分支）");
            return LyricsDocument.Create(body.Descendants(XName.Get("p", TtmlNamespace)).Select(ParseLine), Language(body.Descendants(XName.Get("p", TtmlNamespace)).FirstOrDefault() ?? root) ?? context.Language);
        }

        private LyricLine ParseLine(XElement paragraph)
        {
            context.PreparationGuard?.Item(paragraph.Value);
            List<LyricToken> tokens = [];
            List<RubyAnnotation> ruby = [];
            foreach (XNode node in paragraph.Nodes())
            {
                if (project && node is XElement child && (child.Name != XName.Get("span", TtmlNamespace)
                    || Extension(child, "token-id") is null)) throw new FormatException("TTML project p 只能包含有身份的 token span。");
                if (!project || node is XElement) Visit(node, null, tokens, ruby);
            }
            string text = project ? Required(paragraph, "line-text") : string.Concat(tokens.Select(token => token.Text));
            if (!project && tokens.All(token => token.Time is null) && ruby.Count == 0)
            {
                tokens.Clear();
                foreach (TextElementRange element in GraphemeSplitter.Enumerate(text))
                    tokens.Add(Token(NewId("token"), element.Text, null, TimingStatus.Missing));
            }
            (TimeRange? time, TimingStatus status) = ReadTime(paragraph, true);
            return LyricLine.Create(Id(paragraph, "line-id", (string?)paragraph.Attribute(XName.Get("key", ItunesNamespace))),
                text, time, status, tokens) with { PrimaryLineId = project ? Extension(paragraph, "primary-line-id") : null,
                    RubyAnnotations = [.. ruby] };
        }

        private void Visit(XNode node, XElement? timingOwner, List<LyricToken> tokens, List<RubyAnnotation> ruby)
        {
            if (node is XText text)
            {
                // Source indentation is not lyric content. Explicit spaces, tabs
                // and xml:space=preserve remain verbatim.
                if (text.Value.Length == 0 || (text.Value.All(char.IsWhiteSpace) && text.Value.Contains('\n')
                    && text.Parent?.AncestorsAndSelf().Any(element => (string?)element.Attribute(XNamespace.Xml + "space") == "preserve") != true)) return;
                (TimeRange? time, TimingStatus status) = timingOwner is null ? (null, TimingStatus.Missing) : ReadTime(timingOwner, false);
                tokens.Add(Token(NewId("token"), text.Value, time, status));
                return;
            }
            if (node is not XElement element) return;
            if (element.Name.NamespaceName is "http://www.w3.org/ns/ttml#metadata" or ItunesNamespace or "http://www.example.com/ns/amll") return;
            if (element.Name.NamespaceName == TtmlNamespace && element.Name.LocalName is "head" or "metadata") return;
            string? role = (string?)element.Attribute(XName.Get("role", "http://www.w3.org/ns/ttml#metadata"));
            if (role?.Split(' ').Any(value => value is "x-bg" or "x-translation" or "x-roman") == true)
            {
                discarded.Add(role.Contains("x-bg", StringComparison.Ordinal) ? "和声（x-bg）" : "翻译/罗马音旁轨");
                return;
            }
            if (element.Name == XName.Get("br", TtmlNamespace))
            {
                tokens.Add(Token(NewId("token"), "\n", null, TimingStatus.Missing));
                return;
            }
            if (RubyKind(element) == "container") { ParseRuby(element, timingOwner, tokens, ruby); return; }
            if (project && Extension(element, "token-id") is { } tokenId)
            {
                (TimeRange? time, TimingStatus status) = ReadTime(element, false);
                if (element.Elements().Any(child => RubyKind(child) != "container") || element.Elements().Count() > 1)
                    throw new FormatException("TTML project token 子结构不支持，禁止静默展平。");
                XElement? container = element.Elements().SingleOrDefault(child => RubyKind(child) == "container");
                if (container is not null) ParseRuby(container, element, tokens, ruby);
                else tokens.Add(Token(tokenId, element.Value, time, status));
                return;
            }
            XElement? owner = TtmlTimeTree.IsContent(element) && times[element].Explicit ? element : timingOwner;
            foreach (XNode child in element.Nodes()) Visit(child, owner, tokens, ruby);
        }

        private void ParseRuby(XElement container, XElement? timingOwner, List<LyricToken> tokens, List<RubyAnnotation> rubies)
        {
            XElement[] bases = container.Descendants().Where(element => RubyKind(element) == "base").ToArray();
            XElement[] textContainers = container.Descendants().Where(element => RubyKind(element) == "textContainer").ToArray();
            if (bases.Length != 1 || textContainers.Length != 1 || container.Descendants().Any(element => RubyKind(element) == "container"))
            {
                discarded.Add("rubyAssociationAmbiguous（基础正文保留，注音关联未映射）");
                foreach (XElement baseElement in bases)
                    tokens.Add(Token(NewId("token"), baseElement.Value, null, TimingStatus.Missing));
                return;
            }
            XElement baseNode = bases[0];
            XElement tokenNode = project && timingOwner is not null ? timingOwner
                : times[baseNode].Explicit ? baseNode : times[container].Explicit ? container : timingOwner ?? baseNode;
            (TimeRange? baseTime, TimingStatus baseStatus) = ReadTime(tokenNode, false);
            string tokenId = Id(tokenNode, "token-id");
            tokens.Add(Token(tokenId, baseNode.Value, baseTime, baseStatus));
            List<RubyTextSegment> segments = [];
            XElement textContainer = textContainers[0];
            foreach (XElement segment in textContainer.Descendants().Where(element => RubyKind(element) == "text"))
            {
                if (segment.Value.Length == 0) continue;
                context.PreparationGuard?.Item(segment.Value);
                (TimeRange? time, TimingStatus status) = ReadTime(segment, false);
                segments.Add(RubyTextSegment.Create(Id(segment, "segment-id"), segment.Value, time, status));
            }
            if (!project && textContainer.Descendants().Where(element => RubyKind(element) == "text")
                .Select(Language).Distinct(StringComparer.Ordinal).Skip(1).Any())
                discarded.Add("ruby 片段语言不同（当前注音组仅保留一个 Language）");
            if (segments.Count == 0 && textContainer.Value.Length > 0)
                segments.Add(RubyTextSegment.Create(NewId("segment"), textContainer.Value));
            if (segments.Count == 0) { discarded.Add("rubyAssociationAmbiguous（空注音）"); return; }
            context.PreparationGuard?.Item(textContainer.Value);
            RubyAnnotation ruby = new()
            {
                Id = Id(container, "ruby-id"), BaseTokenId = tokenId, Text = string.Concat(segments.Select(segment => segment.Text)),
                Segments = [.. segments], Language = project ? Extension(container, "language") : (string?)textContainer.Attribute(XNamespace.Xml + "lang")
                    ?? textContainer.Descendants().Select(element => (string?)element.Attribute(XNamespace.Xml + "lang")).FirstOrDefault(value => value is not null) ?? Language(container),
                RelativeScale = project ? Double(container, "scale") : 0.5,
                GapPx = project ? Double(container, "gap") : 2,
                Alignment = project ? EnumValue<RubyAlignment>(Required(container, "alignment")) : RubyAlignment.Center,
            };
            if (!ruby.IsValid) throw new FormatException("TTML ruby 样式或时间不合法。");
            if (!project) discarded.Add("ruby 使用项目默认字号比例/间距/对齐（源未提供项目样式）");
            if (project && Required(container, "base-token-id") != tokenId) throw new FormatException("TTML project 注音基础元素关联错误。");
            rubies.Add(ruby);
        }

        private (TimeRange? Time, TimingStatus Status) ReadTime(XElement element, bool line)
        {
            if (project)
            {
                TimingStatus status = EnumValue<TimingStatus>(Required(element, "timing-status"));
                string? start = Extension(element, "start-us");
                string? end = Extension(element, "end-us");
                TimeRange? range = start is null && end is null ? null
                    : new TimeRange(RawUs(start ?? throw new FormatException("缺少 start-us。")),
                        RawUs(end ?? throw new FormatException("缺少 end-us。")));
                if (!LyricToken.IsTimeStatusPairConsistent(range, status)) throw new FormatException("project 时间与状态不一致。");
                if (status == TimingStatus.Invalid)
                    issues.Add(new LyricsImportIssue(LyricsImportIssueKind.WordTimeOutsideLine, IssueSeverity.Warning, 0,
                        element.Name.LocalName, "project raw 非法时间与 Invalid 状态已原样保留，未替换为标准显示区间。"));
                return (range, status);
            }
            TtmlTimeTree.Interval interval = times[element];
            if (!line && !interval.Explicit) return (null, TimingStatus.Missing);
            if (!interval.Valid)
            {
                issues.Add(new LyricsImportIssue(LyricsImportIssueKind.WordTimeOutsideLine, IssueSeverity.Warning, 0,
                    element.Value, "TTML 活动区间在父时间裁剪后为空，文本以缺失时间保留。"));
                return (null, TimingStatus.Missing);
            }
            long? endUs = interval.EndUs;
            TimingStatus source = TimingStatus.Imported;
            if (line && endUs is not null && element.Attribute("end") is null && element.Attribute("dur") is null)
                DerivedLineEndCount++;
            if (endUs is null && line)
            {
                FallbackLineEndCount++;
                endUs = context.MediaDurationUs > interval.StartUs ? context.MediaDurationUs
                    : checked(interval.StartUs + context.FallbackLineDurationUs);
                source = TimingStatus.Estimated;
                issues.Add(new LyricsImportIssue(LyricsImportIssueKind.LineEndFallback, IssueSeverity.Warning, 0,
                    element.Value, "TTML 行时长不确定，已使用媒体终点或显式兜底时长，标为 Estimated。"));
            }
            if (endUs is null || endUs <= interval.StartUs) return (null, TimingStatus.Missing);
            if (interval.Start * 1_000_000m != decimal.Truncate(interval.Start * 1_000_000m)
                || interval.End is { } preciseEnd && preciseEnd * 1_000_000m != decimal.Truncate(preciseEnd * 1_000_000m))
                issues.Add(new LyricsImportIssue(LyricsImportIssueKind.DiscardedFeature, IssueSeverity.Info, 0,
                    element.Name.LocalName, "TTML 时间精度损失：有效区间舍入到整数微秒，半微秒向远离零方向舍入。"));
            return (new TimeRange(interval.StartUs, endUs.Value), source);
        }

        private LyricToken Token(string id, string text, TimeRange? time, TimingStatus status) =>
            LyricsImportPreparationGuard.Token(context.PreparationGuard, id, text, time, status);
        private string Id(XElement element, string field, string? external = null)
        {
            if (project) return Required(element, field);
            if (external is not null)
            {
                if (external.Length <= 256 && !string.IsNullOrWhiteSpace(external) && external.All(character => !char.IsWhiteSpace(character)) && _ids.Add(external)) return external;
                issues.Add(new LyricsImportIssue(LyricsImportIssueKind.DiscardedFeature, IssueSeverity.Info, 0,
                    external, "源标识无效或重复，已分配新的稳定导入标识。"));
            }
            return NewId(field.Replace("-id", "", StringComparison.Ordinal));
        }
        private string NewId(string kind)
        {
            string id;
            do { id = kind + "-ttml-" + (++_next).ToString("D6", CultureInfo.InvariantCulture); } while (!_ids.Add(id));
            context.PreparationGuard?.Item(id);
            return id;
        }
        private static long RawUs(string value) => long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long result)
            && result.ToString(CultureInfo.InvariantCulture) == value ? result : throw new FormatException("project raw 微秒必须为规范 Int64 十进制整数。");
        private static double Double(XElement element, string field) => double.Parse(Required(element, field), CultureInfo.InvariantCulture);
        private static string Required(XElement element, string field) => Extension(element, field)
            ?? throw new FormatException("TTML project 缺少 " + field + "。");
        private static T EnumValue<T>(string value) where T : struct, Enum =>
            Enum.TryParse(value, false, out T result) && Enum.IsDefined(result) && Enum.GetName(result) == value ? result : throw new FormatException("TTML project 枚举值无效。");
    }
}
