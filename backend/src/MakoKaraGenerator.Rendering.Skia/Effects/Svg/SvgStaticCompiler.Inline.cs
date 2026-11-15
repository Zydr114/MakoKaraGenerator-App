using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using MakoKaraGenerator.Core.Effects;

namespace MakoKaraGenerator.Rendering.Skia.Effects.Svg;

internal sealed partial class SvgStaticCompiler
{
    private const string InlineCountdownSourcePath = "inline-countdown.svg";
    private const double DefaultInlineViewport = 24d;
    private static readonly XNamespace SvgXmlNamespace = "http://www.w3.org/2000/svg";
    private static readonly HashSet<string> ColorBearingAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "fill", "stroke", "stop-color", "flood-color", "lighting-color", "color", "style",
    };

    private static readonly Regex CurrentColorToken = new(@"\bcurrentColor\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static (string SafeXml, double ViewportWidth, double ViewportHeight) CompileInline(
        string rawXml, SvgResourceBudget limits, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rawXml);
        ArgumentNullException.ThrowIfNull(limits);
        if (Encoding.UTF8.GetByteCount(rawXml) > limits.MaximumSourceBytes)
            throw new EffectPackageException("PackageSvgBudgetExceeded", InlineCountdownSourcePath, "内联 SVG 源字节超过预算。");

        var (normalizedXml, viewportWidth, viewportHeight) = PrepareInlineSvg(rawXml);
        if (Encoding.UTF8.GetByteCount(normalizedXml) > limits.MaximumSourceBytes)
            throw new EffectPackageException("PackageSvgBudgetExceeded", InlineCountdownSourcePath, "规范化后的内联 SVG 字节超过预算。");

        var usage = new Usage(limits);
        usage.Prepare(checked((long)normalizedXml.Length * 4), InlineCountdownSourcePath);
        var compiler = new SvgStaticCompiler(InlineCountdownSourcePath, normalizedXml, viewportWidth, viewportHeight, limits, usage, token);
        Node root = compiler.ReadXml();
        compiler.ValidateClipContent();
        foreach (Node node in compiler._nodes)
        {
            token.ThrowIfCancellationRequested();
            compiler.ValidateNode(node, root);
        }
        compiler.ReadStyles();
        compiler.ApplyStyles(root);
        compiler.NormalizeClipRules();
        compiler.ValidateReferences(root);
        compiler.CountExpanded(root, [], 0, true);
        compiler.ValidateFilters();
        var (safeXml, _, _) = compiler.WriteSafeXml(root);
        return (safeXml, viewportWidth, viewportHeight);
    }

    private static (string NormalizedXml, double ViewportWidth, double ViewportHeight) PrepareInlineSvg(string rawXml)
    {
        XDocument document;
        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersFromEntities = 1024,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true,
            };
            using var reader = XmlReader.Create(new StringReader(rawXml), settings);
            document = XDocument.Load(reader, LoadOptions.None);
        }
        catch (XmlException error)
        {
            throw new EffectPackageException("PackageSvgXmlUnsafe", InlineCountdownSourcePath,
                $"内联 SVG XML 无效或包含受禁结构：第 {error.LineNumber} 行第 {error.LinePosition} 列。", error);
        }

        var root = document.Root ?? throw new EffectPackageException("PackageSvgXmlUnsafe", InlineCountdownSourcePath, "内联 SVG 缺少根元素。");
        if (!string.Equals(root.Name.LocalName, "svg", StringComparison.Ordinal)
            || (root.Name.Namespace != XNamespace.None && root.Name.Namespace != SvgXmlNamespace))
        {
            throw new EffectPackageException("PackageSvgXmlUnsafe", InlineCountdownSourcePath, "内联 SVG 根元素必须是 <svg>。");
        }

        foreach (var element in document.Descendants().ToArray())
        {
            if (string.Equals(element.Name.LocalName, "image", StringComparison.OrdinalIgnoreCase))
                throw new EffectPackageException("PackageSvgImageUnsafe", InlineCountdownSourcePath, "内联 SVG 禁止包含 <image> 元素。");
            if (element.Name.Namespace != XNamespace.None && element.Name.Namespace != SvgXmlNamespace)
                throw new EffectPackageException("PackageSvgXmlUnsafe", InlineCountdownSourcePath, $"内联 SVG 包含不受支持的命名空间：{element.Name.NamespaceName}。");
            if (element.Name.Namespace == XNamespace.None)
                element.Name = SvgXmlNamespace + element.Name.LocalName;

            foreach (var attr in element.Attributes().ToArray())
            {
                if (attr.IsNamespaceDeclaration && attr.Name.LocalName == "xmlns" && attr.Value != SvgXmlNamespace.NamespaceName)
                    attr.Remove();
                else if (ColorBearingAttributes.Contains(attr.Name.LocalName) && CurrentColorToken.IsMatch(attr.Value))
                    attr.Value = CurrentColorToken.Replace(attr.Value, "#ffffff");
            }
        }

        var viewBoxAttr = root.Attribute("viewBox");
        double? vbW = null;
        double? vbH = null;
        if (viewBoxAttr is not null)
        {
            if (!TryParseViewBox(viewBoxAttr.Value, out double parsedVbW, out double parsedVbH))
                throw new EffectPackageException("PackageSvgSyntaxInvalid", InlineCountdownSourcePath, "内联 SVG 的 viewBox 无效。");
            vbW = parsedVbW;
            vbH = parsedVbH;
        }

        var widthAttr = root.Attribute("width");
        double? explicitW = null;
        if (widthAttr is not null)
        {
            if (!TryParsePixelLength(widthAttr.Value, out double parsedW))
                throw new EffectPackageException("PackageSvgSyntaxInvalid", InlineCountdownSourcePath, "内联 SVG 的 width 无效。");
            explicitW = parsedW;
        }

        var heightAttr = root.Attribute("height");
        double? explicitH = null;
        if (heightAttr is not null)
        {
            if (!TryParsePixelLength(heightAttr.Value, out double parsedH))
                throw new EffectPackageException("PackageSvgSyntaxInvalid", InlineCountdownSourcePath, "内联 SVG 的 height 无效。");
            explicitH = parsedH;
        }

        double vw = explicitW ?? vbW ?? DefaultInlineViewport;
        double vh = explicitH ?? vbH ?? DefaultInlineViewport;
        if (viewBoxAttr is null)
            root.SetAttributeValue("viewBox", string.Create(CultureInfo.InvariantCulture, $"0 0 {vw} {vh}"));
        root.SetAttributeValue("width", vw.ToString(CultureInfo.InvariantCulture));
        root.SetAttributeValue("height", vh.ToString(CultureInfo.InvariantCulture));

        return (document.ToString(SaveOptions.DisableFormatting), vw, vh);
    }

    private static bool TryParseViewBox(string raw, out double width, out double height)
    {
        width = 0;
        height = 0;
        string[] parts = raw.Split([' ', '\t', '\r', '\n', ','], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 4) return false;
        if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double minX) || !double.IsFinite(minX)) return false;
        if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double minY) || !double.IsFinite(minY)) return false;
        if (!double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out width) || !double.IsFinite(width) || width <= 0) return false;
        if (!double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out height) || !double.IsFinite(height) || height <= 0) return false;
        return true;
    }

    private static bool TryParsePixelLength(string raw, out double value)
    {
        value = 0;
        var trimmed = raw.AsSpan().Trim();
        if (trimmed.EndsWith("px", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[..^2].TrimEnd();
        return double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
            && double.IsFinite(value) && value > 0;
    }
}
