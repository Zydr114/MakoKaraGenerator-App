using System.Collections.Immutable;
using System.Text;
using System.Xml;

namespace MakoKaraGenerator.Rendering.Skia.Effects.Svg;

internal sealed partial class SvgStaticCompiler
{
    private static readonly Dictionary<string, string[]> ElementAttributes = new(StringComparer.Ordinal)
    {
        ["svg"] = ["viewBox", "width", "height", "preserveAspectRatio"],
        ["g"] = [], ["defs"] = [], ["symbol"] = ["viewBox", "preserveAspectRatio"],
        ["use"] = ["href", "x", "y", "width", "height"], ["path"] = ["d"],
        ["rect"] = ["x", "y", "width", "height", "rx", "ry"], ["circle"] = ["cx", "cy", "r"],
        ["ellipse"] = ["cx", "cy", "rx", "ry"], ["line"] = ["x1", "y1", "x2", "y2"],
        ["polyline"] = ["points"], ["polygon"] = ["points"],
        ["linearGradient"] = ["x1", "y1", "x2", "y2", "gradientUnits", "gradientTransform", "spreadMethod", "href"],
        ["radialGradient"] = ["cx", "cy", "r", "fx", "fy", "gradientUnits", "gradientTransform", "spreadMethod", "href"],
        ["stop"] = ["offset"], ["clipPath"] = ["clipPathUnits"],
        ["image"] = ["href", "x", "y", "width", "height", "preserveAspectRatio"],
        ["filter"] = ["x", "y", "width", "height", "filterUnits", "primitiveUnits"],
        ["feGaussianBlur"] = ["in", "stdDeviation", "result"],
        ["feOffset"] = ["in", "dx", "dy", "result"],
        ["feColorMatrix"] = ["in", "type", "values", "result"],
        ["feMerge"] = ["result"], ["feMergeNode"] = ["in"],
        ["style"] = ["type"], ["title"] = [], ["desc"] = [], ["metadata"] = [],
    };
    private static readonly HashSet<string> Shapes = new(["path", "rect", "circle", "ellipse", "line", "polyline", "polygon"], StringComparer.Ordinal);
    private static readonly HashSet<string> Presentation = new([
        "fill", "stroke", "fill-rule", "clip-rule", "stroke-width", "stroke-linecap", "stroke-linejoin", "stroke-miterlimit",
        "stroke-dasharray", "stroke-dashoffset", "fill-opacity", "stroke-opacity", "opacity", "display", "visibility",
        "clip-path", "filter", "stop-color", "stop-opacity", "color-interpolation-filters"], StringComparer.Ordinal);
    private Node ReadXml()
    {
        XmlReaderSettings settings = new()
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, ConformanceLevel = ConformanceLevel.Document,
            MaxCharactersInDocument = _limits.MaximumSourceBytes, MaxCharactersFromEntities = 1,
            IgnoreComments = false, IgnoreProcessingInstructions = false, IgnoreWhitespace = false,
        };
        try
        {
            using var input = new StringReader(ReadSourceText()); using var reader = XmlReader.Create(input, settings);
            Stack<Node> stack = []; Node? root = null; long textCharacters = 0;
            Dictionary<Node, StringBuilder> textBuffers = [];
            while (reader.Read())
            {
                _token.ThrowIfCancellationRequested(); var location = (IXmlLineInfo)reader;
                switch (reader.NodeType)
                {
                    case XmlNodeType.Element:
                        Check(reader.NamespaceURI == SvgNamespace, "元素必须属于唯一 SVG namespace。", code: "PackageSvgNamespaceUnsupported");
                        Check(ElementAttributes.ContainsKey(reader.LocalName), "元素未列入静态 profile：" + reader.LocalName,
                            code: "PackageSvgElementUnsupported");
                        Check(stack.Count < _limits.MaximumXmlDepth, "XML 深度超过预算。", code: "PackageSvgBudgetExceeded");
                        Check(_nodes.Count < _limits.MaximumElements, "原始元素超过预算。", code: "PackageSvgBudgetExceeded");
                        Check(reader.AttributeCount <= _limits.MaximumAttributesPerElement, "元素属性超过预算。", code: "PackageSvgBudgetExceeded");
                        _usage.Prepare(checked(512L + reader.AttributeCount * 128L), _sourcePath);
                        Node node = new(reader.LocalName, location.LineNumber, location.LinePosition, stack.TryPeek(out Node? parent) ? parent : null);
                        if (parent is null) { Check(root is null && node.Name == "svg", "根元素必须为唯一 svg。", node); root = node; }
                        else { Check(node.Name != "svg", "不支持嵌套 svg。", node); parent.Children.Add(node); }
                        if (reader.MoveToFirstAttribute())
                        {
                            do
                            {
                                if (reader.NamespaceURI == "http://www.w3.org/2000/xmlns/")
                                { Check(reader.Value is SvgNamespace or XlinkNamespace, "不支持外部 namespace 声明。", node, "PackageSvgNamespaceUnsupported"); continue; }
                                Check(reader.NamespaceURI.Length == 0 || reader.NamespaceURI == XlinkNamespace && reader.LocalName == "href",
                                    "不支持属性 namespace（包括 xml:base）。", node, "PackageSvgNamespaceUnsupported");
                                string name = reader.LocalName;
                                Check(!name.StartsWith("on", StringComparison.OrdinalIgnoreCase), "禁止事件处理属性。", node, "PackageSvgDynamicUnsupported");
                                Check(node.Attributes.TryAdd(name, reader.Value), "属性或 href/xlink:href 存在歧义重复。", node);
                            } while (reader.MoveToNextAttribute());
                            reader.MoveToElement();
                        }
                        _nodes.Add(node); if (!reader.IsEmptyElement) stack.Push(node);
                        break;
                    case XmlNodeType.EndElement: stack.Pop(); break;
                    case XmlNodeType.Text:
                    case XmlNodeType.CDATA:
                    case XmlNodeType.SignificantWhitespace:
                    case XmlNodeType.Whitespace:
                        textCharacters = checked(textCharacters + reader.Value.Length);
                        Check(textCharacters <= _limits.MaximumSourceBytes, "XML 文本超过预算。", code: "PackageSvgBudgetExceeded");
                        if (stack.TryPeek(out Node? textNode))
                        {
                            if (textNode.Name is "style" or "title" or "desc" or "metadata")
                            {
                                if (!textBuffers.TryGetValue(textNode, out var buffer)) { buffer = new(); textBuffers.Add(textNode, buffer); }
                                buffer.Append(reader.Value);
                            }
                            else Check(string.IsNullOrWhiteSpace(reader.Value) && reader.NodeType != XmlNodeType.CDATA,
                                "绘制元素不允许文字或 CDATA。", textNode, "PackageSvgTextUnsupported");
                        }
                        else Check(string.IsNullOrWhiteSpace(reader.Value), "根元素外不允许文字。");
                        break;
                    case XmlNodeType.XmlDeclaration:
                        Check(reader.GetAttribute("encoding") is null or "utf-8" or "UTF-8", "SVG 源仅支持声明 UTF-8 编码。"); break;
                    case XmlNodeType.Comment: break;
                    case XmlNodeType.ProcessingInstruction:
                    case XmlNodeType.DocumentType:
                    case XmlNodeType.EntityReference:
                        throw Fail("PackageSvgXmlUnsafe", "禁止处理指令、DTD 和实体声明。");
                    default: throw Fail("PackageSvgXmlUnsafe", "不支持的 XML 节点。");
                }
            }
            Check(root is not null && stack.Count == 0, "SVG 文档不完整。");
            foreach (var pair in textBuffers) { _token.ThrowIfCancellationRequested(); pair.Key.Text = pair.Value.ToString(); }
            return root;
        }
        catch (XmlException error) { throw Fail("PackageSvgXmlUnsafe", $"XML 无效或包含 DTD/实体：第 {error.LineNumber} 行 {error.LinePosition} 列。", inner: error); }
    }

    private void ValidateNode(Node node, Node root)
    {
        foreach (var pair in node.Attributes)
        {
            bool transform = pair.Key == "transform" && (Shapes.Contains(node.Name) || node.Name is "g" or "use" or "image" or "clipPath");
            Check(pair.Key is "id" or "class" or "style" || transform || Presentation.Contains(pair.Key)
                || ElementAttributes[node.Name].Contains(pair.Key, StringComparer.Ordinal),
                "属性未列入静态 profile：" + pair.Key, node, "PackageSvgAttributeUnsupported");
        }
        if (node.Has("id"))
        { string id = node.Get("id"); Check(ValidId(id) && _ids.TryAdd(id, node), "ID 无效或重复。", node, "PackageSvgIdInvalid"); }
        if (node.Has("class"))
            Check(node.Get("class").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).All(ValidId), "class 必须为静态 ASCII 标识符。", node);
        if (node.Has("transform")) node.Transform = ReadTransform(node.Get("transform"), node);
        foreach (var pair in node.Attributes.Where(p => Presentation.Contains(p.Key))) node.Styles.Add(pair.Key, ValidateStyleValue(pair.Key, pair.Value, node));
        if (node.Parent is { } parent)
        {
            bool allowed = parent.Name switch
            {
                "svg" or "g" or "defs" => node.Name is not "stop" && !node.Name.StartsWith("fe", StringComparison.Ordinal),
                "symbol" => Shapes.Contains(node.Name) || node.Name is "g" or "use" or "image" or "title" or "desc",
                "linearGradient" or "radialGradient" => node.Name == "stop",
                "clipPath" => Shapes.Contains(node.Name) || node.Name is "use",
                "filter" => node.Name is "feGaussianBlur" or "feOffset" or "feColorMatrix" or "feMerge",
                "feMerge" => node.Name == "feMergeNode",
                _ => false,
            };
            Check(allowed, "元素位置不符合静态内容模型。", node);
        }
        double Get(string key, double fallback = 0, double min = -MaximumNumber)
            => node.Has(key) ? Number(node.Get(key), node, min) : fallback;
        switch (node.Name)
        {
            case "svg":
                Check(ReferenceEquals(node, root), "禁止嵌套 svg。", node);
                ValidateViewBox(node, true);
                if (node.Has("width")) Check(Get("width", min: 0) == _viewportWidth, "根 width 必须匹配 descriptor viewport。", node);
                if (node.Has("height")) Check(Get("height", min: 0) == _viewportHeight, "根 height 必须匹配 descriptor viewport。", node);
                node.Attributes["width"] = F(_viewportWidth); node.Attributes["height"] = F(_viewportHeight);
                Check(NativeNumber(_viewportWidth) && NativeNumber(_viewportHeight), "descriptor viewport 无法由 native float 表示。", node);
                ValidateAspect(node);
                _rootTransform = ViewportMatrix(node, _viewportWidth, _viewportHeight);
                CheckMatrix(_rootTransform, node); break;
            case "symbol": ValidateViewBox(node, true); ValidateAspect(node); break;
            case "path": ReadPath(node); break;
            case "rect":
                double x = Get("x"), y = Get("y"), width = Get("width", min: 0), height = Get("height", min: 0);
                _ = Get("rx", min: 0); _ = Get("ry", min: 0); Check(width > 0 && height > 0, "rect 宽高必须为正。", node);
                node.Geometry = Bounds.Empty.Include(x, y).Include(x + width, y + height); break;
            case "circle":
                double cx = Get("cx"), cy = Get("cy"), r = Get("r", min: 0); Check(r > 0, "circle 半径必须为正。", node);
                node.Geometry = Bounds.Empty.Include(cx - r, cy - r).Include(cx + r, cy + r); break;
            case "ellipse":
                double ex = Get("cx"), ey = Get("cy"), rx = Get("rx", min: 0), ry = Get("ry", min: 0);
                Check(rx > 0 && ry > 0, "ellipse 半径必须为正。", node);
                node.Geometry = Bounds.Empty.Include(ex - rx, ey - ry).Include(ex + rx, ey + ry); break;
            case "line": node.Geometry = Bounds.Empty.Include(Get("x1"), Get("y1")).Include(Get("x2"), Get("y2")); break;
            case "polygon":
            case "polyline":
                double[] points = Numbers(node.Get("points"), node, node.Name == "polygon" ? 6 : 4, _limits.MaximumPathCommands * 2);
                Check(points.Length % 2 == 0, "points 必须为完整坐标对。", node);
                for (int i = 0; i < points.Length; i += 2) { node.Geometry = node.Geometry.Include(points[i], points[i + 1]); CountPath(node); }
                if (node.Name == "polygon") CountPath(node); break;
            case "use":
                Check(node.Has("href"), "use 必须有本地 href。", node);
                _ = Get("x"); _ = Get("y");
                if (node.Has("width")) Check(Get("width", min: 0) > 0, "use width 必须为正。", node);
                if (node.Has("height")) Check(Get("height", min: 0) > 0, "use height 必须为正。", node); break;
            case "image":
                Check(node.Has("href") && Get("width", min: 0) > 0 && Get("height", min: 0) > 0, "image 必须有 href 和正宽高。", node);
                node.Geometry = Bounds.Empty.Include(Get("x"), Get("y")).Include(Get("x") + Get("width"), Get("y") + Get("height")); ValidateAspect(node); break;
            case "linearGradient":
            case "radialGradient": ValidateGradient(node); break;
            case "stop": Check(node.Has("offset"), "stop 必须声明 offset。", node); _ = Number(node.Get("offset"), node, 0, 1, true); break;
            case "clipPath": Check(node.Get("clipPathUnits") is "userSpaceOnUse" or "objectBoundingBox", "clipPathUnits 必须明确。", node); break;
            case "style": Check(node.Get("type", "text/css") == "text/css", "style type 只支持 text/css。", node); break;
        }
        if (!node.Geometry.IsEmpty)
        { CheckFinitePoint(node.Geometry.Left, node.Geometry.Top, node); CheckFinitePoint(node.Geometry.Right, node.Geometry.Bottom, node); }
    }

    private void ValidateViewBox(Node node, bool required)
    {
        Check(!required || node.Has("viewBox"), "必须显式声明 viewBox。", node);
        if (!node.Has("viewBox")) return;
        double[] values = Numbers(node.Get("viewBox"), node, 4, 4);
        Check(values[2] > 0 && values[3] > 0, "viewBox 不得退化。", node);
        CheckFinitePoint(values[0] + values[2], values[1] + values[3], node);
    }
    private void ValidateAspect(Node node)
    {
        string aspect = node.Get("preserveAspectRatio", "xMidYMid meet");
        string[] words = aspect.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        Check(words.Length is 1 or 2 && (words[0] == "none" && words.Length == 1
            || new[] { "xMinYMin", "xMidYMin", "xMaxYMin", "xMinYMid", "xMidYMid", "xMaxYMid", "xMinYMax", "xMidYMax", "xMaxYMax" }.Contains(words[0], StringComparer.Ordinal)
                && (words.Length == 1 || words[1] is "meet" or "slice")), "preserveAspectRatio 无效。", node);
        node.Attributes["preserveAspectRatio"] = string.Join(' ', words);
    }
    private void ValidateGradient(Node node)
    {
        Check(node.Get("gradientUnits") is "userSpaceOnUse" or "objectBoundingBox", "gradientUnits 必须明确。", node);
        Check(node.Get("spreadMethod", "pad") is "pad" or "reflect" or "repeat", "spreadMethod 无效。", node);
        if (node.Has("gradientTransform")) _ = ReadTransform(node.Get("gradientTransform"), node);
        foreach (string key in node.Name == "linearGradient" ? new[] { "x1", "y1", "x2", "y2" } : ["cx", "cy", "r", "fx", "fy"])
            if (node.Has(key)) _ = Number(node.Get(key), node, key == "r" ? 0 : -MaximumNumber, MaximumNumber, true);
        if (node.Name == "radialGradient" && node.Has("r")) Check(Number(node.Get("r"), node, 0, MaximumNumber, true) > 0, "radialGradient 半径必须为正。", node);
    }
    private static bool ValidId(string value) => value.Length is > 0 and <= 128 && char.IsAsciiLetterOrDigit(value[0])
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.');

    private (string Xml, long Bytes, ImmutableArray<SvgSourceLocation> Map) WriteSafeXml(Node root)
    {
        using BoundedXmlStream output = new(this); var map = ImmutableArray.CreateBuilder<SvgSourceLocation>();
        using (var writer = XmlWriter.Create(output, new XmlWriterSettings { Encoding = new UTF8Encoding(false, true), OmitXmlDeclaration = true, CloseOutput = false }))
        {
            void Write(Node node)
            {
                _token.ThrowIfCancellationRequested(); if (node.Name == "style") return;
                map.Add(new(map.Count, _sourcePath, node.Line, node.Column)); writer.WriteStartElement(node.Name, SvgNamespace);
                foreach (var pair in node.Attributes.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    _token.ThrowIfCancellationRequested();
                    if (pair.Key is "style" or "class" || Presentation.Contains(pair.Key)) continue;
                    writer.WriteAttributeString(pair.Key, pair.Value);
                }
                foreach (var pair in node.Styles.OrderBy(p => p.Key, StringComparer.Ordinal)) writer.WriteAttributeString(pair.Key, pair.Value);
                if (node.Text.Length > 0) writer.WriteString(node.Text);
                foreach (var child in node.Children) Write(child);
                writer.WriteEndElement();
            }
            Write(root); writer.Flush();
        }
        _token.ThrowIfCancellationRequested(); return (Encoding.UTF8.GetString(output.GetBuffer(), 0, checked((int)output.Length)), output.Length, map.ToImmutable());
    }

    private sealed class BoundedXmlStream(SvgStaticCompiler owner) : Stream
    {
        private readonly MemoryStream _output = new();
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _output.Length;
        public override long Position { get => _output.Position; set => throw new NotSupportedException(); }
        internal byte[] GetBuffer() => _output.GetBuffer();
        public override void Flush() => _output.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            owner._token.ThrowIfCancellationRequested();
            owner.Check(Length <= owner._limits.MaximumSafeXmlBytes - buffer.Length, "受控 XML 文档超过字节预算。", code: "PackageSvgBudgetExceeded");
            owner._usage.Safe(buffer.Length, owner._sourcePath); _output.Write(buffer);
        }
        public override void WriteByte(byte value) { Span<byte> data = stackalloc byte[1]; data[0] = value; Write(data); }
        protected override void Dispose(bool disposing) { if (disposing) _output.Dispose(); base.Dispose(disposing); }
    }
}
