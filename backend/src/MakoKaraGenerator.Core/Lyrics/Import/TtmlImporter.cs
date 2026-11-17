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
    private const string MetadataNamespace = "http://www.w3.org/ns/ttml#metadata";
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
            if (!project && absolute) NormalizeEmptyNamespace(root, issues, discarded);
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
            Builder builder = new(context, issues, discarded, times, project, absolute, root);
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
            if (element.Name.NamespaceName == MetadataNamespace && element.Name.LocalName is not "agent" and not "name")
                features.Add("TTML head metadata");
            if (element.Name.NamespaceName == TtmlNamespace && (element.Name.LocalName is "layout" or "region"
                || element.Name.LocalName == "style" && HasUnmappedStyleAttributes(element)))
                features.Add("TTML 布局/样式资源");
            if (element.Name.NamespaceName == ItunesNamespace && element.Name.LocalName == "iTunesMetadata")
                features.Add("Apple translation/transliteration head sidecar");
            if (element.Name.NamespaceName == "http://www.example.com/ns/amll") features.Add("AMLL metadata 扩展");
            if (element.Name.NamespaceName is not TtmlNamespace and not StylingNamespace and not ItunesNamespace
                and not ProjectNamespace and not MetadataNamespace and not "http://www.example.com/ns/amll")
                features.Add("未知命名空间元素（保留可识别正文）");
            foreach (XAttribute attribute in element.Attributes())
            {
                if (attribute.Name.NamespaceName == ItunesNamespace && attribute.Name.LocalName is "song-part" or "songPart") features.Add("itunes:song-part 歌曲段落");
                if (attribute.Name.NamespaceName == StylingNamespace && attribute.Name.LocalName is not "ruby" and not "rubyAlign") features.Add("TTML 样式覆盖");
                if (attribute.Name.NamespaceName == MetadataNamespace && attribute.Name.LocalName == "role"
                    && attribute.Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Any(token => token is not "x-bg" and not "x-translation" and not "x-roman"))
                    features.Add("ttm:role 旁轨角色");
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
                        or "subFrameRate" or "tickRate" or "timeBase" or "dropMode" or "markerMode" or "xmlns" or "style"
                        || element.Name.NamespaceName == MetadataNamespace && element.Name.LocalName is "agent" or "name" && name == "type"
                        || element.Name.NamespaceName == "http://www.example.com/ns/amll" && element.Name.LocalName == "meta" && name is "key" or "value",
                    "http://www.w3.org/XML/1998/namespace" => name is "id" or "lang" or "space",
                    "http://www.w3.org/ns/ttml#parameter" => name is "frameRate" or "frameRateMultiplier" or "subFrameRate"
                        or "tickRate" or "timeBase" or "dropMode" or "markerMode",
                    ItunesNamespace => name is "key" or "timing" or "song-part" or "songPart",
                    StylingNamespace => name is "ruby" or "rubyAlign",
                    MetadataNamespace => name is "agent" or "role",
                    _ => false,
                };
                if (!known) features.Add("未映射 XML 属性（含未知扩展；正文保留）");
            }
        }
        foreach (string feature in features) discarded.Add(feature);
    }

    private static bool HasUnmappedStyleAttributes(XElement style) => style.Attributes().Any(attribute =>
        !attribute.IsNamespaceDeclaration
        && !(attribute.Name.NamespaceName == "http://www.w3.org/XML/1998/namespace" && attribute.Name.LocalName == "id")
        && !(attribute.Name.NamespaceName.Length == 0 && attribute.Name.LocalName == "style")
        && !(attribute.Name.NamespaceName == StylingNamespace && attribute.Name.LocalName is "ruby" or "rubyAlign"));

    /// <summary>
    /// AMLL/Apple 标记明确、且空命名空间元素就是 TTML 内容或头结构词表时，只在内存里把它们恢复回 TTML 命名空间。
    /// 项目 profile、没有标记的文档、以及认不出的本地名保持严格。
    /// </summary>
    private static void NormalizeEmptyNamespace(XElement root, BudgetedImportList<LyricsImportIssue> issues, BudgetedImportList<string> discarded)
    {
        int count = 0;
        void Walk(XElement element)
        {
            if (element.Name.NamespaceName.Length == 0 && element.Parent is { } parent && parent.Name.NamespaceName == TtmlNamespace)
            {
                string local = element.Name.LocalName;
                bool allowed = (parent.Name.LocalName, local) switch
                {
                    ("tt", "head" or "body") => true,
                    ("head", "metadata" or "styling" or "layout") => true,
                    ("styling", "style") => true,
                    ("layout", "region") => true,
                    ("body" or "div" or "p" or "span", "div" or "p" or "span" or "br") => true,
                    _ => false,
                };
                if (allowed)
                {
                    element.Name = XName.Get(local, TtmlNamespace);
                    count++;
                }
            }
            foreach (XElement child in element.Elements().ToArray()) Walk(child);
        }
        Walk(root);
        if (count == 0) return;
        const string feature = "空命名空间正文已按 AMLL/Apple 标记在内存中恢复为 TTML 内容元素";
        discarded.Add(feature);
        issues.Add(new LyricsImportIssue(LyricsImportIssueKind.DiscardedFeature, IssueSeverity.Info, 0,
            "xmlns=\"\"", "兼容处理：" + feature + "，未改源文件。"));
    }

    internal static string? Extension(XElement element, string name) => (string?)element.Attribute(XName.Get(name, ProjectNamespace));
    private static string? Language(XElement element) => element.AncestorsAndSelf().Select(node => (string?)node.Attribute(XNamespace.Xml + "lang")).FirstOrDefault(value => value is not null);

    private sealed class Builder(LyricsImportContext context, BudgetedImportList<LyricsImportIssue> issues,
        BudgetedImportList<string> discarded, TtmlTimeTree times, bool project, bool absolute, XElement root)
    {
        private readonly HashSet<string> _ids = new(StringComparer.Ordinal);
        private readonly HashSet<string> _discardedSeen = new(discarded, StringComparer.Ordinal);
        private readonly Dictionary<string, string> _agents = new(StringComparer.Ordinal);
        private readonly Dictionary<string, XElement> _styles = new(StringComparer.Ordinal);
        private readonly List<Placed?> _placed = [];
        private readonly HashSet<string> _undefinedAgents = new(StringComparer.Ordinal);
        private int _next;
        private string? _paragraphAgent;
        private string? _paragraphRole;
        private string? _paragraphLanguage;
        private TimeRange? _paragraphTime;
        private TimingStatus _paragraphStatus;
        private XNode? _consume;
        private bool _inlineSplit;
        private bool _alignmentFromStyle;
        internal int DerivedLineEndCount { get; private set; }
        internal int FallbackLineEndCount { get; private set; }
        private sealed record Placed(string? Agent, string? Role, string? Language, LyricLine Line);
        private void Discard(string feature) { if (_discardedSeen.Add(feature)) discarded.Add(feature); }

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
            IndexMetadata();
            foreach (XElement paragraph in body.Descendants(XName.Get("p", TtmlNamespace))) ParseLine(paragraph);
            return Assemble(Language(body.Descendants(XName.Get("p", TtmlNamespace)).FirstOrDefault() ?? root) ?? context.Language);
        }

        private LyricLine ParseLine(XElement paragraph)
        {
            context.PreparationGuard?.Item(paragraph.Value);
            _paragraphAgent = project ? null : AgentOf(paragraph);
            _paragraphRole = project ? null : SidecarOf(paragraph);
            _paragraphLanguage = Language(paragraph);
            List<LyricToken> tokens = [];
            List<RubyAnnotation> ruby = [];
            (TimeRange? time, TimingStatus status) = ReadTime(paragraph, true);
            _paragraphTime = time;
            _paragraphStatus = status;
            int slot = -1;
            if (!project) { slot = _placed.Count; _placed.Add(null); }
            foreach (XNode node in Nodes(paragraph))
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
            LyricLine line = LyricLine.Create(Id(paragraph, "line-id", (string?)paragraph.Attribute(XName.Get("key", ItunesNamespace))),
                text, time, status, tokens) with { PrimaryLineId = project ? Extension(paragraph, "primary-line-id") : null,
                    RubyAnnotations = [.. ruby] };
            if (!project && (tokens.Count > 0 || ruby.Count > 0))
                _placed[slot] = new(_paragraphAgent, _paragraphRole, _paragraphLanguage, line);
            return line;
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
            if (element.Name.NamespaceName is MetadataNamespace or ItunesNamespace or "http://www.example.com/ns/amll") return;
            if (element.Name.NamespaceName.Length == 0 && !TtmlTimeTree.IsContent(element))
            {
                Discard("未确认的空命名空间元素（未当作歌词）");
                return;
            }
            if (element.Name.NamespaceName == TtmlNamespace && element.Name.LocalName is "head" or "metadata") return;
            if (!project && TrySimpleRuby(element, timingOwner, tokens, ruby)) return;
            if (!project && TtmlTimeTree.IsContent(element) && IsOverride(element))
            {
                Extract(element);
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
            foreach (XNode child in Nodes(element)) Visit(child, owner, tokens, ruby);
        }

        private void ParseRuby(XElement container, XElement? timingOwner, List<LyricToken> tokens, List<RubyAnnotation> rubies)
        {
            if (container.Descendants().Any(element => RubyKind(element) == "delimiter"))
                Discard("ruby delimiter 未进入歌词模型");
            XElement[] bases = container.Descendants().Where(element => RubyKind(element) == "base").ToArray();
            XElement[] textContainers = container.Descendants().Where(element => RubyKind(element) == "textContainer").ToArray();
            XElement[] baseContainers = container.Descendants().Where(element => RubyKind(element) == "baseContainer").ToArray();
            bool nested = container.Descendants().Any(element => RubyKind(element) == "container");
            if (!nested && textContainers.Length <= 1 && baseContainers.Length <= 1)
            {
                if (project)
                {
                    XElement[] texts = textContainers.Length == 1
                        ? textContainers[0].Descendants().Where(element => RubyKind(element) == "text").ToArray()
                        : container.Elements().Where(element => RubyKind(element) == "text").ToArray();
                    if (bases.Length == 1 && (textContainers.Length == 1 || texts.Length > 0))
                    {
                        XElement textSource = textContainers.Length == 1 ? textContainers[0] : texts[0];
                        FinishRuby(container, bases[0], textSource, texts, timingOwner, tokens, rubies);
                        return;
                    }
                }
                else if (baseContainers.Length == 1 && textContainers.Length == 1)
                {
                    XElement[] bcBases = baseContainers[0].Descendants().Where(element => RubyKind(element) == "base").ToArray();
                    XElement[] tcTexts = textContainers[0].Descendants().Where(element => RubyKind(element) == "text").ToArray();
                    if (bcBases.Length == bases.Length && tcTexts.Length > 0)
                    {
                        XElement? bcOwner = times.TryGet(baseContainers[0], out TtmlTimeTree.Interval bcInt) && bcInt.Explicit ? baseContainers[0] : timingOwner;
                        if (bcBases.Length == 1)
                        {
                            FinishRuby(container, bcBases[0], textContainers[0], tcTexts, bcOwner, tokens, rubies);
                            return;
                        }
                        if (bcBases.Length == tcTexts.Length && tcTexts.All(text => text.Value.Length > 0))
                        {
                            for (int i = 0; i < bcBases.Length; i++)
                                FinishRuby(container, bcBases[i], tcTexts[i], [tcTexts[i]], bcOwner, tokens, rubies);
                            return;
                        }
                    }
                }
                else if (baseContainers.Length == 0 && TryExtractDirectRubyPairs(container, out List<(XElement Base, XElement TextSource, XElement[] Texts)> pairs))
                {
                    foreach (var pair in pairs)
                        FinishRuby(container, pair.Base, pair.TextSource, pair.Texts, timingOwner, tokens, rubies);
                    return;
                }
                else if (baseContainers.Length == 0 && textContainers.Length == 0 && bases.Length == 1)
                {
                    XElement[] directTexts = container.Elements().Where(element => RubyKind(element) == "text").ToArray();
                    if (directTexts.Length > 0)
                    {
                        FinishRuby(container, bases[0], directTexts[0], directTexts, timingOwner, tokens, rubies);
                        return;
                    }
                }
            }
            Discard("rubyAssociationAmbiguous（基础正文保留，注音关联未映射）");
            foreach (XElement baseElement in bases)
            {
                XElement tokenNode = times.TryGet(baseElement, out TtmlTimeTree.Interval baseInterval) && baseInterval.Explicit ? baseElement
                    : times.TryGet(container, out TtmlTimeTree.Interval containerInterval) && containerInterval.Explicit ? container
                    : timingOwner ?? baseElement;
                (TimeRange? baseTime, TimingStatus baseStatus) = times.TryGet(tokenNode, out _) || project
                    ? ReadTime(tokenNode, false) : (null, TimingStatus.Missing);
                LyricToken token = Token(NewId("token"), baseElement.Value, baseTime, baseStatus);
                string? explicitAgent = OwnAgent(baseElement);
                string? explicitRole = OwnSidecar(baseElement);
                if (!project && (explicitAgent is not null && explicitAgent != _paragraphAgent || explicitRole is not null && explicitRole != _paragraphRole))
                {
                    _inlineSplit = true;
                    TimeRange? lineTime = _paragraphTime ?? baseTime;
                    TimingStatus lineStatus = _paragraphTime is not null ? _paragraphStatus : baseStatus;
                    LyricLine line = LyricLine.Create(NewId("line"), token.Text, lineTime, lineTime is null ? TimingStatus.Missing : lineStatus, [token]);
                    _placed.Add(new(explicitAgent ?? _paragraphAgent, explicitRole ?? _paragraphRole, Language(baseElement) ?? _paragraphLanguage, line));
                }
                else
                    tokens.Add(token);
            }
        }

        private bool TryExtractDirectRubyPairs(XElement container, out List<(XElement Base, XElement TextSource, XElement[] Texts)> pairs)
        {
            pairs = [];
            XElement? pendingBase = null;
            foreach (XNode node in container.Nodes())
            {
                if (node is XText text)
                {
                    if (!string.IsNullOrWhiteSpace(text.Value)) return false;
                    continue;
                }
                if (node is not XElement child) continue;
                if (child.Name.NamespaceName is MetadataNamespace or ItunesNamespace or "http://www.example.com/ns/amll") continue;
                string? kind = RubyKind(child);
                if (kind == "delimiter") continue;
                if (pendingBase is null)
                {
                    if (kind != "base") return false;
                    pendingBase = child;
                    continue;
                }
                if (kind == "text")
                {
                    pairs.Add((pendingBase, child, [child]));
                    pendingBase = null;
                }
                else if (kind == "textContainer")
                {
                    XElement[] tcTexts = child.Descendants().Where(element => RubyKind(element) == "text").ToArray();
                    pairs.Add((pendingBase, child, tcTexts));
                    pendingBase = null;
                }
                else
                    return false;
            }
            return pendingBase is null && pairs.Count > 0;
        }

        private void FinishRuby(XElement container, XElement baseNode, XElement textSource, IEnumerable<XElement> textNodes,
            XElement? timingOwner, List<LyricToken> tokens, List<RubyAnnotation> rubies)
        {
            XElement tokenNode = project && timingOwner is not null ? timingOwner
                : times.TryGet(baseNode, out TtmlTimeTree.Interval baseInterval) && baseInterval.Explicit ? baseNode
                : times.TryGet(container, out TtmlTimeTree.Interval containerInterval) && containerInterval.Explicit ? container
                : timingOwner ?? baseNode;
            (TimeRange? baseTime, TimingStatus baseStatus) = times.TryGet(tokenNode, out _) || project
                ? ReadTime(tokenNode, false) : (null, TimingStatus.Missing);
            List<RubyTextSegment> segments = [];
            foreach (XElement segment in textNodes)
            {
                if (segment.Value.Length == 0) continue;
                context.PreparationGuard?.Item(segment.Value);
                (TimeRange? time, TimingStatus status) = ReadTime(segment, false);
                segments.Add(RubyTextSegment.Create(Id(segment, "segment-id"), segment.Value, time, status));
            }
            if (!project && textNodes.Select(Language).Where(value => value is not null).Distinct(StringComparer.Ordinal).Skip(1).Any())
                Discard("ruby 片段语言不同（当前注音组仅保留一个 Language）");
            if (segments.Count == 0 && textSource.Value.Length > 0)
                segments.Add(RubyTextSegment.Create(NewId("segment"), textSource.Value));
            if (segments.Count == 0) { Discard("rubyAssociationAmbiguous（空注音）"); return; }
            if (!project && absolute && baseStatus == TimingStatus.Missing && segments.All(segment => segment.Time is { IsValid: true }))
            {
                long start = segments.Min(segment => segment.Time!.Value.StartUs);
                long end = segments.Max(segment => segment.Time!.Value.EndUs);
                if (end > start)
                {
                    baseTime = new TimeRange(start, end);
                    baseStatus = TimingStatus.Estimated;
                    issues.Add(new LyricsImportIssue(LyricsImportIssueKind.DiscardedFeature, IssueSeverity.Info, 0, baseNode.Value,
                        "原生 AMLL 只有注音片段时间，已按有界片段范围推导正文时间并标记为 Estimated。"));
                }
            }
            context.PreparationGuard?.Item(textSource.Value);
            string tokenId = Id(tokenNode, "token-id");
            string? explicitAgent = OwnAgent(baseNode);
            string? explicitRole = OwnSidecar(baseNode);
            bool redirect = !project && (explicitAgent is not null && explicitAgent != _paragraphAgent || explicitRole is not null && explicitRole != _paragraphRole);
            LyricToken token = Token(tokenId, baseNode.Value, baseTime, baseStatus);
            RubyAlignment alignment = project ? EnumValue<RubyAlignment>(Required(container, "alignment")) : AlignmentOf(container);
            RubyAnnotation ruby = new()
            {
                Id = Id(container, "ruby-id"), BaseTokenId = tokenId, Text = string.Concat(segments.Select(segment => segment.Text)),
                Segments = [.. segments], Language = project ? Extension(container, "language") : (string?)textSource.Attribute(XNamespace.Xml + "lang")
                    ?? textSource.Descendants().Select(element => (string?)element.Attribute(XNamespace.Xml + "lang")).FirstOrDefault(value => value is not null) ?? Language(container),
                RelativeScale = project ? Double(container, "scale") : 0.5,
                GapPx = project ? Double(container, "gap") : 2,
                Alignment = alignment,
            };
            if (!ruby.IsValid) throw new FormatException("TTML ruby 样式或时间不合法。");
            if (!project) Discard(_alignmentFromStyle
                ? "ruby 使用项目默认字号比例/间距（对齐取自 tts:rubyAlign）"
                : "ruby 使用项目默认字号比例/间距/对齐（源未提供项目样式）");
            if (project && Required(container, "base-token-id") != tokenId) throw new FormatException("TTML project 注音基础元素关联错误。");
            if (!redirect)
            {
                tokens.Add(token);
                rubies.Add(ruby);
                return;
            }
            _inlineSplit = true;
            TimeRange? lineTime = _paragraphTime ?? baseTime;
            TimingStatus lineStatus = _paragraphTime is not null ? _paragraphStatus : baseStatus;
            LyricLine line = LyricLine.Create(NewId("line"), token.Text, lineTime, lineTime is null ? TimingStatus.Missing : lineStatus, [token])
                with { RubyAnnotations = [ruby] };
            _placed.Add(new(explicitAgent ?? _paragraphAgent, explicitRole ?? _paragraphRole, Language(baseNode) ?? _paragraphLanguage, line));
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

        private void IndexMetadata()
        {
            foreach (XElement style in root.Descendants(XName.Get("style", TtmlNamespace)))
            {
                string? id = (string?)style.Attribute(XNamespace.Xml + "id");
                if (!string.IsNullOrWhiteSpace(id)) _styles.TryAdd(id, style);
            }
            foreach (XElement agent in root.Descendants(XName.Get("agent", MetadataNamespace)))
            {
                string? id = (string?)agent.Attribute(XNamespace.Xml + "id");
                if (string.IsNullOrWhiteSpace(id))
                {
                    issues.Add(new LyricsImportIssue(LyricsImportIssueKind.MalformedTag, IssueSeverity.Warning, 0,
                        agent.Value, "ttm:agent 缺少 xml:id，不能作为歌唱者分支。"));
                    continue;
                }
                string? name = agent.Elements(XName.Get("name", MetadataNamespace)).Select(item => item.Value.Trim()).FirstOrDefault(value => value.Length > 0);
                _agents.TryAdd(id, string.IsNullOrEmpty(name) ? id : name);
            }
        }

        private LyricsDocument Assemble(string? language)
        {
            Placed[] placed = [.. _placed.Where(item => item is not null).Select(item => item!)];
            bool divided = root.Descendants(XName.Get("div", TtmlNamespace)).Skip(1).Any();
            bool attributed = placed.Any(item => item.Agent is not null || item.Role is not null);
            if (divided) Discard(attributed
                ? "多个 div 按文档顺序归入歌唱者或主歌词来源（div 本身不是工程分支）"
                : "多个 div 章节合并到同一歌词来源（未声明 project 分支）");
            ReportAgents(placed);
            if (!attributed)
                return LyricsDocument.Create(placed.Select(item => item.Line), language);
            List<string> order = [];
            Dictionary<string, List<Placed>> buckets = new(StringComparer.Ordinal);
            foreach (Placed item in placed)
            {
                string key = (item.Agent, item.Role) switch
                {
                    (not null, not null) => "a:" + item.Agent + "|r:" + item.Role,
                    (not null, null) => "a:" + item.Agent,
                    (null, not null) => "r:" + item.Role,
                    (null, null) => "main",
                };
                if (!buckets.TryGetValue(key, out List<Placed>? list))
                {
                    list = [];
                    buckets.Add(key, list);
                    order.Add(key);
                }
                list.Add(item);
            }
            string primaryKey = buckets.ContainsKey("main") ? "main"
                : order.Where(key => key.StartsWith("a:", StringComparison.Ordinal) && buckets[key][0].Role is null)
                    .OrderByDescending(key => buckets[key].Count).ThenBy(key => order.IndexOf(key)).FirstOrDefault()
                ?? order.FirstOrDefault(key => buckets[key][0].Role is null)
                ?? order[0];
            List<LyricBranch> branches = [];
            foreach (string key in order)
            {
                List<Placed> items = buckets[key];
                Placed first = items[0];
                string id = Reserve(key == "main" ? "primary" : BranchToken(key));
                string? branchLanguage = items.Select(item => item.Language).FirstOrDefault(value => value is not null) ?? language;
                branches.Add(LyricBranch.Create(id, NameOf(first), key == primaryKey ? LyricBranchRole.Primary : RoleOf(first),
                    items.Select(item => item.Line), branchLanguage, LyricBranchTimingMode.Independent));
            }
            if (placed.Any(item => item.Role == "x-bg")) Discard("和声（x-bg）已映射为具名分支");
            if (placed.Any(item => item.Role is "x-translation" or "x-roman")) Discard("翻译/罗马音旁轨已映射为具名分支");
            if (_inlineSplit) Discard("行内歌唱者或旁轨覆盖已拆成独立行");
            ReportUndefinedAgents();
            LyricBranch primary = branches.First(branch => branch.Role == LyricBranchRole.Primary);
            return new LyricsDocument
            {
                PrimaryBranchId = primary.Id,
                Branches = [.. branches.Where(branch => branch.Role == LyricBranchRole.Primary), .. branches.Where(branch => branch.Role != LyricBranchRole.Primary)],
                SchemaVersion = LyricsDocument.CurrentSchemaVersion,
            };
        }

        private void ReportAgents(Placed[] placed)
        {
            if (placed.Any(item => item.Agent is not null)) Discard("ttm:agent 已映射为具名分支");
            HashSet<string> used = new(placed.Where(item => item.Agent is not null).SelectMany(item => item.Agent!.Split(' ')), StringComparer.Ordinal);
            if (_agents.Keys.Any(id => !used.Contains(id))) Discard("ttm:agent 定义未引用");
        }

        private void ReportUndefinedAgents()
        {
            foreach (string id in _undefinedAgents)
                issues.Add(new LyricsImportIssue(LyricsImportIssueKind.DiscardedFeature, IssueSeverity.Warning, 0, id,
                    "未定义歌唱者引用：" + id + "。正文已保留在同名分支，未并入其他歌唱者。"));
        }

        private void Extract(XElement element)
        {
            string? savedAgent = _paragraphAgent;
            string? savedRole = _paragraphRole;
            string? savedLanguage = _paragraphLanguage;
            TimeRange? savedTime = _paragraphTime;
            TimingStatus savedStatus = _paragraphStatus;
            _paragraphAgent = AgentOf(element);
            _paragraphRole = SidecarOf(element);
            _paragraphLanguage = Language(element) ?? savedLanguage;
            (TimeRange? own, TimingStatus ownStatus, bool ownExplicit) = ExplicitInterval(element);
            if (ownExplicit)
            {
                _paragraphTime = own;
                _paragraphStatus = own is null ? TimingStatus.Missing : ownStatus;
            }
            else
            {
                _paragraphTime = savedTime;
                _paragraphStatus = savedStatus;
            }
            int slot = _placed.Count;
            _placed.Add(null);
            List<LyricToken> tokens = [];
            List<RubyAnnotation> ruby = [];
            if (RubyKind(element) == "container")
                ParseRuby(element, null, tokens, ruby);
            else
                foreach (XNode child in Nodes(element)) Visit(child, element, tokens, ruby);
            _inlineSplit = true;
            if (tokens.Count > 0 || ruby.Count > 0)
            {
                string text = string.Concat(tokens.Select(token => token.Text));
                if (tokens.All(token => token.Time is null) && ruby.Count == 0)
                {
                    tokens.Clear();
                    foreach (TextElementRange grapheme in GraphemeSplitter.Enumerate(text))
                        tokens.Add(Token(NewId("token"), grapheme.Text, null, TimingStatus.Missing));
                }
                LyricLine line = LyricLine.Create(NewId("line"), text,
                    _paragraphTime, _paragraphTime is null ? TimingStatus.Missing : _paragraphStatus, tokens) with { RubyAnnotations = [.. ruby] };
                _placed[slot] = new(_paragraphAgent, _paragraphRole, _paragraphLanguage, line);
            }
            _paragraphAgent = savedAgent;
            _paragraphRole = savedRole;
            _paragraphLanguage = savedLanguage;
            _paragraphTime = savedTime;
            _paragraphStatus = savedStatus;
        }

        private (TimeRange? Time, TimingStatus Status, bool Explicit) ExplicitInterval(XElement element)
        {
            if (!times.TryGet(element, out TtmlTimeTree.Interval interval) || !interval.Explicit)
                return (null, TimingStatus.Missing, false);
            if (!interval.Valid || interval.EndUs is not long end || end <= interval.StartUs)
            {
                if (!interval.Valid)
                    issues.Add(new LyricsImportIssue(LyricsImportIssueKind.WordTimeOutsideLine, IssueSeverity.Warning, 0,
                        element.Value, "TTML 活动区间在父时间裁剪后为空，文本以缺失时间保留。"));
                return (null, TimingStatus.Missing, true);
            }
            if (interval.Start * 1_000_000m != decimal.Truncate(interval.Start * 1_000_000m)
                || interval.End is { } preciseEnd && preciseEnd * 1_000_000m != decimal.Truncate(preciseEnd * 1_000_000m))
                issues.Add(new LyricsImportIssue(LyricsImportIssueKind.DiscardedFeature, IssueSeverity.Info, 0,
                    element.Name.LocalName, "TTML 时间精度损失：有效区间舍入到整数微秒，半微秒向远离零方向舍入。"));
            return (new TimeRange(interval.StartUs, end), TimingStatus.Imported, true);
        }

        private bool TrySimpleRuby(XElement element, XElement? timingOwner, List<LyricToken> tokens, List<RubyAnnotation> ruby)
        {
            if (RubyKind(element) != "base") return false;
            if (element.Parent is XElement parent && RubyKind(parent) is "container" or "baseContainer" or "textContainer") return false;
            XElement? next = NextElement(element);
            if (next is null || RubyKind(next) is not ("text" or "textContainer")) return false;
            _consume = next;
            XElement[] texts = RubyKind(next) == "text" ? [next] : next.Descendants().Where(child => RubyKind(child) == "text").ToArray();
            FinishRuby(element, element, next, texts, timingOwner, tokens, ruby);
            return true;
        }

        private string? RubyKind(XElement element)
        {
            string? direct = (string?)element.Attribute(XName.Get("ruby", StylingNamespace));
            return direct ?? StyleValue(element, "ruby");
        }

        private RubyAlignment AlignmentOf(XElement element)
        {
            _alignmentFromStyle = false;
            string? value = (string?)element.Attribute(XName.Get("rubyAlign", StylingNamespace)) ?? StyleValue(element, "rubyAlign");
            if (value is null) return RubyAlignment.Center;
            _alignmentFromStyle = true;
            return value switch
            {
                "center" => RubyAlignment.Center,
                "withBase" or "start" => RubyAlignment.Start,
                "end" => RubyAlignment.End,
                _ => ReportUnmappedAlign(value),
            };
        }

        private RubyAlignment ReportUnmappedAlign(string value)
        {
            Discard("tts:rubyAlign=" + value + " 无法映射，已使用居中");
            return RubyAlignment.Center;
        }

        private string? StyleValue(XElement element, string local)
        {
            string? references = (string?)element.Attribute("style");
            if (string.IsNullOrWhiteSpace(references)) return null;
            HashSet<string> visiting = new(StringComparer.Ordinal);
            string? resolved = null;
            foreach (string id in references.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string? value = LookupStyle(id, local, visiting);
                if (value is not null) resolved = value;
            }
            return resolved;
        }

        private string? LookupStyle(string id, string local, HashSet<string> visiting)
        {
            if (!visiting.Add(id))
            {
                Discard("样式引用存在循环");
                return null;
            }
            try
            {
                if (!_styles.TryGetValue(id, out XElement? style))
                {
                    Discard("未定义样式引用");
                    return null;
                }
                string? direct = (string?)style.Attribute(XName.Get(local, StylingNamespace));
                if (direct is not null) return direct;
                string? references = (string?)style.Attribute("style");
                if (string.IsNullOrWhiteSpace(references)) return null;
                string? resolved = null;
                foreach (string next in references.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    string? value = LookupStyle(next, local, visiting);
                    if (value is not null) resolved = value;
                }
                return resolved;
            }
            finally
            {
                visiting.Remove(id);
            }
        }

        private bool IsOverride(XElement element) => AgentOf(element) != _paragraphAgent || SidecarOf(element) != _paragraphRole;
        private static string? AgentOf(XElement element)
        {
            for (XElement? node = element; node is not null; node = node.Parent)
            {
                string? value = OwnAgent(node);
                if (value is not null) return value;
            }
            return null;
        }
        private static string? OwnAgent(XElement element)
        {
            string? value = (string?)element.Attribute(XName.Get("agent", MetadataNamespace));
            if (string.IsNullOrWhiteSpace(value)) return null;
            string[] ids = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            return ids.Length == 0 ? null : string.Join(' ', ids);
        }
        private string? SidecarOf(XElement element)
        {
            for (XElement? node = element; node is not null; node = node.Parent)
            {
                string? value = OwnSidecar(node);
                if (value is not null) return value;
            }
            return null;
        }
        private string? OwnSidecar(XElement element)
        {
            string? role = (string?)element.Attribute(XName.Get("role", MetadataNamespace));
            if (string.IsNullOrWhiteSpace(role)) return null;
            string[] matches = role.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(token => token is "x-bg" or "x-translation" or "x-roman")
                .Distinct(StringComparer.Ordinal).ToArray();
            if (matches.Length > 1) Discard("ttm:role 同时声明多个旁轨角色（已取首个）");
            return matches.FirstOrDefault();
        }
        private string NameOf(Placed item)
        {
            string? roleLabel = item.Role switch { "x-bg" => "和声", "x-translation" => "翻译", "x-roman" => "罗马音", _ => null };
            if (item.Agent is { } agent)
            {
                List<string> names = [];
                foreach (string id in agent.Split(' '))
                {
                    if (_agents.TryGetValue(id, out string? name)) names.Add(name);
                    else { _undefinedAgents.Add(id); names.Add("未定义歌唱者 " + id); }
                }
                string singer = string.Join("、", names);
                return roleLabel is null ? singer : singer + " · " + roleLabel;
            }
            return roleLabel ?? "主歌词";
        }
        private static LyricBranchRole RoleOf(Placed item) => item.Role switch
        {
            "x-translation" => LyricBranchRole.Translation,
            "x-roman" => LyricBranchRole.Annotation,
            _ => LyricBranchRole.Custom,
        };
        private static string BranchToken(string key)
        {
            string raw;
            if (key.StartsWith("a:", StringComparison.Ordinal))
            {
                int split = key.IndexOf("|r:", StringComparison.Ordinal);
                raw = split >= 0
                    ? "agent-" + key[2..split].Replace(' ', '+') + "-" + key[(split + 3)..]
                    : "agent-" + key[2..].Replace(' ', '+');
            }
            else
                raw = "role-" + key[2..];
            return new string(raw.Where(character => !char.IsWhiteSpace(character)).ToArray());
        }
        private string Reserve(string raw)
        {
            if (raw.Length > 100) raw = raw[..100];
            string id = raw;
            int suffix = 2;
            while (!_ids.Add(id)) id = raw + "-" + suffix++.ToString(CultureInfo.InvariantCulture);
            return id;
        }
        private IEnumerable<XNode> Nodes(XElement parent)
        {
            foreach (XNode node in parent.Nodes())
            {
                if (ReferenceEquals(node, _consume)) { _consume = null; continue; }
                yield return node;
            }
        }
        private static XElement? NextElement(XElement element)
        {
            bool seen = false;
            foreach (XNode node in element.Parent?.Nodes() ?? [])
            {
                if (!seen) { if (ReferenceEquals(node, element)) seen = true; continue; }
                if (node is XElement next) return next;
                if (node is XText text && text.Value.Any(character => !char.IsWhiteSpace(character))) return null;
            }
            return null;
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
