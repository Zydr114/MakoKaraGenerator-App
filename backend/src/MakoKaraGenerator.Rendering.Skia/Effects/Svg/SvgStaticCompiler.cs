using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using MakoKaraGenerator.Core.Effects;

namespace MakoKaraGenerator.Rendering.Skia.Effects.Svg;

/// <summary>The one application grammar before any Svg.Skia DOM or native compilation.</summary>
internal sealed partial class SvgStaticCompiler
{
    internal const string RulesVersion = "mako-svg-static-1/grammar-1";
    private const string SvgNamespace = "http://www.w3.org/2000/svg";
    private const string XlinkNamespace = "http://www.w3.org/1999/xlink";
    private const double MaximumNumber = 1_000_000;
    private readonly EffectPackageContents _contents;
    private readonly EffectSvgResourceDefinition _definition;
    private readonly SvgResourceBudget _limits;
    private readonly Usage _usage;
    private readonly CancellationToken _token;
    private readonly EffectPackageFile _source;
    private readonly Dictionary<string, Node> _ids = new(StringComparer.Ordinal);
    private readonly List<Node> _nodes = [];
    private readonly List<CssRule> _rules = [];
    private readonly Dictionary<Node, int> _imageOccurrences = [];
    private readonly Dictionary<string, SvgValidatedImage> _validatedImages = new(StringComparer.Ordinal);
    private int _rawCommands, _expandedElements, _expandedCommands, _operations, _cssDeclarations, _selectorSteps;
    private int _filterPasses;
    private long _decodedBytes, _nativeDecodedBytes, _filterBytes;
    private Bounds _expandedGeometry = Bounds.Empty;
    private Matrix _rootTransform = Matrix.Identity;
    private double _maximumDensity = 1;

    private SvgStaticCompiler(EffectPackageContents contents, EffectSvgResourceDefinition definition,
        SvgResourceBudget limits, Usage usage, CancellationToken token)
    {
        _contents = contents; _definition = definition; _limits = limits; _usage = usage; _token = token;
        _source = contents.Files.Single(f => f.Path == definition.SourcePath && f.Role == "svg");
    }

    internal static SvgStaticPackage CompilePackage(EffectPackageContents contents, EffectPackageSvgResources resources,
        SvgResourceBudget limits, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(contents); ArgumentNullException.ThrowIfNull(resources);
        limits.Validate(); token.ThrowIfCancellationRequested();
        // A descriptor from another immutable package must not authorize this package's source bytes.
        var owned = EffectPackageSvgResources.Read(contents, limits, token);
        if (resources.PackageId != contents.PackageId || resources.Revision != contents.Revision
            || resources.ById.Count != owned.ById.Count || owned.ById.Any(pair =>
            !resources.ById.TryGetValue(pair.Key, out var supplied) || !SameDefinition(pair.Value, supplied)))
            throw new EffectPackageException("PackageSvgResourceOwnershipMismatch", "manifest.json", "SVG 描述不属于当前固定内容闭包。");
        Usage usage = new(limits);
        var documents = ImmutableArray.CreateBuilder<SvgStaticDocument>();
        foreach (var definition in owned.ById.Values.OrderBy(d => d.Id, StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            documents.Add(new SvgStaticCompiler(contents, definition, limits, usage, token).Compile());
        }
        token.ThrowIfCancellationRequested();
        return new(new(contents.PackageId, contents.Revision, EffectPackageSvgResources.StaticProfile, RulesVersion,
            [.. documents.Select(d => d.Inspection)], usage.PrepareBytes), documents.ToImmutable());
    }

    private static bool SameDefinition(EffectSvgResourceDefinition left, EffectSvgResourceDefinition right)
        => left.Id == right.Id && left.DescriptorPath == right.DescriptorPath && left.SourcePath == right.SourcePath
            && left.Profile == right.Profile && left.Mode == right.Mode && left.Viewport == right.Viewport
            && left.PaddingPx == right.PaddingPx && left.Mipmaps == right.Mipmaps
            && left.Images.SequenceEqual(right.Images, StringComparer.Ordinal) && left.Variants.SequenceEqual(right.Variants);

    private SvgStaticDocument Compile()
    {
        _token.ThrowIfCancellationRequested();
        if (_source.Content.Length > _limits.MaximumSourceBytes) throw Fail("PackageSvgBudgetExceeded", "SVG 源字节超过预算。");
        // Reserve UTF-16 input plus the application tree/token working copy before creating either.
        _usage.Prepare(checked((long)_source.Content.Length * 4), _source.Path);
        Node root = ReadXml();
        foreach (Node node in _nodes) { _token.ThrowIfCancellationRequested(); ValidateNode(node, root); }
        ReadStyles();
        ApplyStyles(root);
        ValidateReferences(root);
        CountExpanded(root, [], 0, true);
        ValidateFilters();
        // All XML, CSS, paths and typed reference closure have passed before touching any image codec.
        RewriteImages();
        var atlas = AtlasPlans();
        var (safeXml, safeBytes, sourceMap) = WriteSafeXml(root);
        var dependencies = _definition.Images.Append(_definition.SourcePath).Append(_definition.DescriptorPath)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .Select(path => _contents.Files.Single(f => f.Path == path))
            .Select(f => new SvgDependencyInspection(f.Path, f.Role, f.Sha256)).ToImmutableArray();
        var inspection = new SvgResourceInspection(_definition.Id, _definition.Mode, _definition.Viewport,
            _source.Path, _source.Sha256, dependencies, _nodes.Count, _expandedElements, _rawCommands,
            _expandedCommands, _operations, _rules.Count, _cssDeclarations, _selectorSteps,
            _decodedBytes, _nativeDecodedBytes, _filterPasses, _filterBytes, safeBytes, atlas);
        return new(safeXml, sourceMap, inspection, _validatedImages.ToImmutableDictionary(StringComparer.Ordinal));
    }

    private ImmutableArray<SvgAtlasPlan> AtlasPlans()
    {
        var plans = ImmutableArray.CreateBuilder<SvgAtlasPlan>();
        foreach (var variant in _definition.Variants)
        {
            _token.ThrowIfCancellationRequested();
            int width = checked(variant.Width + 2 * _definition.PaddingPx), height = checked(variant.Height + 2 * _definition.PaddingPx);
            var levels = ImmutableArray.CreateBuilder<SvgMipLevelPlan>(); long bytes = 0;
            while (true)
            {
                long size = checked((long)width * height * 4); bytes = checked(bytes + size);
                levels.Add(new(width, height, size));
                if (width == 1 && height == 1) break;
                width = Math.Max(1, width / 2); height = Math.Max(1, height / 2);
            }
            _usage.Atlas(bytes, _source.Path); plans.Add(new(variant.Id, levels.ToImmutable(), bytes));
        }
        return plans.ToImmutable();
    }

    private EffectPackageException Fail(string code, string message, Node? node = null, Exception? inner = null)
        => new(code, _source.Path, node is null ? message : $"第 {node.Line} 行 {node.Column} 列：{message}", inner);
    private void Check([DoesNotReturnIf(false)] bool value, string message, Node? node = null, string code = "PackageSvgSyntaxInvalid")
    { if (!value) throw Fail(code, message, node); }
    private static string F(double value) => value.ToString("G17", CultureInfo.InvariantCulture);

    private sealed class Node(string name, int line, int column, Node? parent)
    {
        internal string Name { get; } = name;
        internal int Line { get; } = line;
        internal int Column { get; } = column;
        internal Node? Parent { get; } = parent;
        internal Dictionary<string, string> Attributes { get; } = new(StringComparer.Ordinal);
        internal Dictionary<string, string> Styles { get; } = new(StringComparer.Ordinal);
        internal List<Node> Children { get; } = [];
        internal List<Reference> References { get; } = [];
        internal string Text { get; set; } = "";
        internal int PathCommands { get; set; }
        internal Matrix Transform { get; set; } = Matrix.Identity;
        internal Bounds Geometry { get; set; } = Bounds.Empty;
        internal bool Has(string key) => Attributes.ContainsKey(key);
        internal string Get(string key, string fallback = "") => Attributes.GetValueOrDefault(key, fallback);
    }
    private sealed record Reference(string Id, ReferenceKind Kind);
    private enum ReferenceKind { Use, Gradient, Paint, Clip, Filter }

    private sealed class Usage(SvgResourceBudget limits)
    {
        private readonly HashSet<string> _rawSources = new(StringComparer.Ordinal);
        private long _elements, _commands, _expandedElements, _expandedCommands, _operations, _rules, _declarations, _selectorSteps;
        private long _imageBytes, _decodedBytes, _atlasBytes, _filterBytes, _safeBytes, _imageReferences;
        internal long PrepareBytes { get; private set; }
        internal void Prepare(long bytes, string path)
        { PrepareBytes = Add(PrepareBytes, bytes, limits.MaximumPrepareBytes, path, "准备活动字节"); }
        internal void Raw(string path, int elements, int commands)
        {
            if (!_rawSources.Add(path)) return;
            _elements = Add(_elements, elements, limits.MaximumElements, path, "原始元素");
            _commands = Add(_commands, commands, limits.MaximumPathCommands, path, "原始路径 command");
        }
        internal void Expanded(string path, int elements, int commands, int operations)
        {
            _expandedElements = Add(_expandedElements, elements, limits.MaximumExpandedElements, path, "展开元素");
            _expandedCommands = Add(_expandedCommands, commands, limits.MaximumExpandedPathCommands, path, "展开路径 command");
            _operations = Add(_operations, operations, limits.MaximumPictureOperations, path, "保守 Picture operation");
        }
        internal void Css(string path, int rules, int declarations, int steps)
        {
            _rules = Add(_rules, rules, limits.MaximumCssRules, path, "CSS rules");
            _declarations = Add(_declarations, declarations, limits.MaximumCssDeclarations, path, "CSS declarations");
            _selectorSteps = Add(_selectorSteps, steps, limits.MaximumSelectorSteps, path, "CSS selector steps");
        }
        internal void EncodedImage(string path, long encoded, int references)
        {
            _imageBytes = Add(_imageBytes, encoded, limits.MaximumTotalImageBytes, path, "图片编码字节");
            _imageReferences = Add(_imageReferences, Math.Max(1, references), limits.MaximumImageReferences, path, "展开图片引用");
        }
        internal void DecodedImage(string path, long validationDecoded, long nativeDecoded)
        {
            _decodedBytes = Add(_decodedBytes, Math.Max(validationDecoded, nativeDecoded), limits.MaximumDecodedImageBytes, path, "完整图片解码字节");
            // Validation bitmap and the later Svg.Skia FromEncodedData image are independent allocations.
            Prepare(checked(validationDecoded + nativeDecoded), path);
        }
        internal void Atlas(long bytes, string path)
        { _atlasBytes = Add(_atlasBytes, bytes, limits.MaximumAtlasMipBytes, path, "atlas mip 字节"); Prepare(bytes, path); }
        internal void Filter(long bytes, string path)
        { _filterBytes = Add(_filterBytes, bytes, limits.MaximumFilterSurfaceBytes, path, "filter 工作面字节"); Prepare(bytes, path); }
        internal void Safe(long bytes, string path)
        { _safeBytes = Add(_safeBytes, bytes, limits.MaximumTotalSafeXmlBytes, path, "受控 XML 累计字节"); Prepare(checked(bytes * 4), path); }
        private static long Add(long current, long increment, long maximum, string path, string label)
        {
            if (increment < 0 || current > maximum - increment)
                throw new EffectPackageException("PackageSvgBudgetExceeded", path, label + "超过明确预算。");
            return current + increment;
        }
    }
}
