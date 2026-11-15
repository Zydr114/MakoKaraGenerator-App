using MakoKaraGenerator.Core.Effects;

namespace MakoKaraGenerator.Rendering.Skia.Effects.Svg;

internal sealed partial class SvgStaticCompiler
{
    private void NormalizeClipRules()
    {
        foreach (var clip in _nodes.Where(node => node.Name == "clipPath"))
        {
            // The pinned model otherwise prioritizes the container's explicit rule
            // over each child's rule. Freeze each direct shape's computed inheritance
            // first, then omit the container rule from SafeXml. Ordinary use stays intact.
            foreach (var child in clip.Children)
            {
                _token.ThrowIfCancellationRequested();
                string rule = "nonzero";
                for (Node? ancestor = child; ancestor is not null; ancestor = ancestor.Parent)
                    if (ancestor.Styles.TryGetValue("clip-rule", out var inherited)) { rule = inherited; break; }
                child.Styles["clip-rule"] = rule;
            }
            clip.Styles.Remove("clip-rule");
        }
    }

    private void ValidateClipContent()
    {
        // The pinned native model supports direct clip geometry. Reject containers and
        // use before ordinary content validation, including unused definitions, rather
        // than publishing a resource whose clip silently loses transforms or geometry.
        foreach (var clip in _nodes.Where(node => node.Name == "clipPath"))
            foreach (var child in clip.Children)
            {
                _token.ThrowIfCancellationRequested();
                Check(Shapes.Contains(child.Name) && child.Children.Count == 0,
                    "首期 clipPath 仅接受直接形状，不支持组、symbol 或 use。", child, "PackageSvgClipContentUnsupported");
            }
    }

    private void ValidateReferences(Node root)
    {
        foreach (var node in _nodes)
        {
            _token.ThrowIfCancellationRequested();
            if (node.Has("href") && node.Name != "image")
            {
                string href = node.Get("href"); Check(href.StartsWith('#') && ValidId(href[1..]),
                    "href 只能为同文档的精确 #id。", node, "PackageSvgReferenceUnsafe");
                node.References.Add(new(href[1..], node.Name == "use" ? ReferenceKind.Use : ReferenceKind.Gradient));
            }
            foreach (var pair in node.Styles)
                if (pair.Value.StartsWith("url(#", StringComparison.Ordinal))
                    node.References.Add(new(pair.Value[5..^1], pair.Key switch
                    { "clip-path" => ReferenceKind.Clip, "filter" => ReferenceKind.Filter, _ => ReferenceKind.Paint }));
            foreach (var reference in node.References)
            {
                Check(_ids.TryGetValue(reference.Id, out var target), "本地引用目标缺失：" + reference.Id, node, "PackageSvgReferenceMissing");
                bool supported = reference.Kind switch
                {
                    ReferenceKind.Use => Shapes.Contains(target!.Name) || target.Name is "g" or "symbol" or "use" or "image",
                    ReferenceKind.Gradient or ReferenceKind.Paint => target!.Name is "linearGradient" or "radialGradient",
                    ReferenceKind.Clip => target!.Name == "clipPath",
                    ReferenceKind.Filter => target!.Name == "filter",
                    _ => false,
                };
                Check(supported, "本地引用目标类型不匹配。", node, "PackageSvgReferenceTypeMismatch");
                if (reference.Kind == ReferenceKind.Use && target!.Name == "symbol")
                    Check(node.Has("width") && node.Has("height"), "引用 symbol 的 use 必须有明确 width/height。", node);
            }
        }
        HashSet<Node> complete = []; HashSet<Node> active = [];
        void Visit(Node node, int depth)
        {
            _token.ThrowIfCancellationRequested();
            Check(depth <= _limits.MaximumReferenceDepth, "本地引用深度超过预算。", node, "PackageSvgBudgetExceeded");
            Check(!active.Contains(node), "本地 use/paint/clip/filter 依赖包含循环。", node, "PackageSvgReferenceCycle");
            if (complete.Contains(node)) return;
            active.Add(node);
            foreach (var child in node.Children) Visit(child, depth);
            foreach (var reference in node.References) Visit(_ids[reference.Id], depth + 1);
            active.Remove(node); complete.Add(node);
        }
        // Start from every node, so a malicious unused definition also fails admission.
        foreach (var node in _nodes) Visit(node, 0);
        foreach (var gradient in _nodes.Where(n => n.Name is "linearGradient" or "radialGradient"))
        {
            Node effective = gradient; int depth = 0;
            while (!effective.Children.Any(n => n.Name == "stop") && effective.References.FirstOrDefault(r => r.Kind == ReferenceKind.Gradient) is { } parent)
            { Check(++depth <= _limits.MaximumReferenceDepth, "渐变继承超过预算。", gradient, "PackageSvgBudgetExceeded"); effective = _ids[parent.Id]; }
            var stops = effective.Children.Where(n => n.Name == "stop").ToArray();
            Check(stops.Length > 0, "渐变继承完成后仍没有 stop。", gradient);
            double previous = -1;
            foreach (var stop in stops)
            { double offset = Number(stop.Get("offset"), stop, 0, 1, true); Check(offset >= previous, "stop offset 必须非递减。", stop); previous = offset; }
        }
        _usage.Raw(_sourcePath, _nodes.Count, _rawCommands);
        _ = root;
    }

    private void CountExpanded(Node node, HashSet<Node> active, int referenceDepth, bool top, Matrix? inherited = null)
    {
        _token.ThrowIfCancellationRequested();
        Check(referenceDepth <= _limits.MaximumReferenceDepth, "展开引用深度超过预算。", node, "PackageSvgBudgetExceeded");
        Check(active.Add(node), "展开依赖包含循环。", node, "PackageSvgReferenceCycle");
        _expandedElements++; _expandedCommands = checked(_expandedCommands + node.PathCommands);
        if (node.Name == "image") _imageOccurrences[node] = _imageOccurrences.GetValueOrDefault(node) + 1;
        int cost = node.Name == "path" || node.Name is "polyline" or "polygon" ? Math.Max(1, node.PathCommands)
            : Shapes.Contains(node.Name) || node.Name == "image" ? 1 : node.Name is "g" or "use" or "svg" or "symbol" ? 2 : 1;
        _operations = checked(_operations + cost + node.Styles.Count);
        Check(_expandedElements <= _limits.MaximumExpandedElements && _expandedCommands <= _limits.MaximumExpandedPathCommands
            && _operations <= _limits.MaximumPictureOperations, "展开元素、command 或保守绘制 operation 超过预算。", node, "PackageSvgBudgetExceeded");
        Matrix transform = (inherited ?? (top ? _rootTransform : Matrix.Identity)) * node.Transform; CheckMatrix(transform, node);
        _maximumDensity = Math.Max(_maximumDensity, Math.Max(Math.Abs(transform.A) + Math.Abs(transform.C), Math.Abs(transform.B) + Math.Abs(transform.D)));
        if (!node.Geometry.IsEmpty)
        {
            Bounds bounds = node.Geometry.Map(transform); CheckFinitePoint(bounds.Left, bounds.Top, node); CheckFinitePoint(bounds.Right, bounds.Bottom, node);
            _expandedGeometry = _expandedGeometry.Union(bounds);
        }
        foreach (var child in node.Children) CountExpanded(child, active, referenceDepth, false, transform);
        foreach (var reference in node.References)
        {
            Matrix referenced = transform;
            if (reference.Kind == ReferenceKind.Use)
            {
                double x = node.Has("x") ? Number(node.Get("x"), node) : 0, y = node.Has("y") ? Number(node.Get("y"), node) : 0;
                referenced *= new Matrix(1, 0, 0, 1, x, y);
                if (_ids[reference.Id].Name == "symbol") referenced *= SymbolMatrix(node, _ids[reference.Id]);
            }
            CountExpanded(_ids[reference.Id], active, referenceDepth + 1, false, referenced);
        }
        active.Remove(node);
        if (top) _usage.Expanded(_sourcePath, _expandedElements, _expandedCommands, _operations);
    }
    private Matrix SymbolMatrix(Node use, Node symbol)
    {
        double width = Number(use.Get("width"), use, 0), height = Number(use.Get("height"), use, 0);
        return ViewportMatrix(symbol, width, height);
    }
    private Matrix ViewportMatrix(Node symbol, double width, double height)
    {
        double[] box = Numbers(symbol.Get("viewBox"), symbol, 4, 4);
        double sx = width / box[2], sy = height / box[3], dx = 0, dy = 0;
        string[] aspect = symbol.Get("preserveAspectRatio").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (aspect[0] != "none")
        {
            double scale = aspect.Length > 1 && aspect[1] == "slice" ? Math.Max(sx, sy) : Math.Min(sx, sy); sx = sy = scale;
            dx = (width - box[2] * scale) * (aspect[0].StartsWith("xMin", StringComparison.Ordinal) ? 0 : aspect[0].StartsWith("xMid", StringComparison.Ordinal) ? 0.5 : 1);
            dy = (height - box[3] * scale) * (aspect[0].EndsWith("YMin", StringComparison.Ordinal) ? 0 : aspect[0].EndsWith("YMid", StringComparison.Ordinal) ? 0.5 : 1);
        }
        return new(sx, 0, 0, sy, dx - box[0] * sx, dy - box[1] * sy);
    }

    private void ValidateFilters()
    {
        foreach (var filter in _nodes.Where(n => n.Name == "filter"))
        {
            _token.ThrowIfCancellationRequested();
            Check((filter.Get("filterUnits") is "userSpaceOnUse" or "objectBoundingBox")
                && (filter.Get("primitiveUnits") is "userSpaceOnUse" or "objectBoundingBox"), "filterUnits/primitiveUnits 必须明确。", filter);
            foreach (string key in new[] { "x", "y", "width", "height" }) Check(filter.Has(key), "filter 必须声明有限 region。", filter);
            bool objectRegion = filter.Get("filterUnits") == "objectBoundingBox";
            double x = Number(filter.Get("x"), filter, -MaximumNumber, MaximumNumber, objectRegion);
            double y = Number(filter.Get("y"), filter, -MaximumNumber, MaximumNumber, objectRegion);
            double width = Number(filter.Get("width"), filter, 0, MaximumNumber, objectRegion);
            double height = Number(filter.Get("height"), filter, 0, MaximumNumber, objectRegion);
            Check(width > 0 && height > 0, "filter region 不得退化。", filter); CheckFinitePoint(x + width, y + height, filter);
            double objectWidth = Math.Max(_viewportWidth, _expandedGeometry.IsEmpty ? 0 : _expandedGeometry.Right - _expandedGeometry.Left);
            double objectHeight = Math.Max(_viewportHeight, _expandedGeometry.IsEmpty ? 0 : _expandedGeometry.Bottom - _expandedGeometry.Top);
            if (objectRegion) { width *= objectWidth; height *= objectHeight; }
            HashSet<string> available = new(["SourceGraphic", "SourceAlpha"], StringComparer.Ordinal); string previous = "SourceGraphic";
            int passes = 0; double outsetX = 0, outsetY = 0;
            string Input(Node primitive)
            {
                string input = primitive.Get("in", previous);
                Check(available.Contains(input), "filter 输入必须为 SourceGraphic/SourceAlpha 或先前 result；禁止 forward/外部输入。", primitive, "PackageSvgFilterInputInvalid");
                primitive.Attributes["in"] = input; return input;
            }
            foreach (var primitive in filter.Children)
            {
                _token.ThrowIfCancellationRequested(); int cost = 1;
                switch (primitive.Name)
                {
                    case "feGaussianBlur":
                        _ = Input(primitive); double[] sigma = Numbers(primitive.Get("stdDeviation"), primitive, 1, 2);
                        Check(sigma.All(v => v >= 0), "Gaussian sigma 不得为负。", primitive);
                        outsetX += 3 * sigma[0]; outsetY += 3 * sigma[^1]; break;
                    case "feOffset":
                        _ = Input(primitive);
                        outsetX += Math.Abs(Number(primitive.Get("dx", "0"), primitive)); outsetY += Math.Abs(Number(primitive.Get("dy", "0"), primitive)); break;
                    case "feColorMatrix":
                        _ = Input(primitive); Check(primitive.Get("type") == "matrix", "colorMatrix 只支持显式 matrix。", primitive);
                        _ = Numbers(primitive.Get("values"), primitive, 20, 20); break;
                    case "feMerge":
                        Check(primitive.Children.Count > 0, "feMerge 必须有输入。", primitive);
                        foreach (var child in primitive.Children) { _ = Input(child); }
                        cost = primitive.Children.Count; break;
                    default: throw Fail("PackageSvgFilterUnsupported", "filter primitive 不属于有限子集。", primitive);
                }
                passes = checked(passes + cost);
                Check(passes <= _limits.MaximumFilterPasses, "单 SVG filter pass 超过预算。", filter, "PackageSvgBudgetExceeded");
                string result = primitive.Get("result");
                if (result.Length > 0)
                { Check(ValidId(result) && available.Add(result), "filter result 无效、重复或占用保留输入。", primitive, "PackageSvgFilterInputInvalid"); previous = result; }
                else
                {
                    // Name the specification's previous-result default explicitly for the controlled document.
                    result = "mako_internal_filter_" + _nodes.IndexOf(primitive);
                    Check(available.Add(result) && !filter.Children.Any(n => n.Get("result") == result), "filter result 与内部身份冲突。", primitive);
                    primitive.Attributes["result"] = result; previous = result;
                }
                primitive.Styles["color-interpolation-filters"] = "sRGB";
            }
            _filterPasses = checked(_filterPasses + passes);
            Check(_filterPasses <= _limits.MaximumFilterPasses, "单 SVG 累计 filter pass 超过预算。", filter, "PackageSvgBudgetExceeded");
            Check(passes > 0, "filter 不能为空。", filter);
            if (filter.Get("primitiveUnits") == "objectBoundingBox") { outsetX *= objectWidth; outsetY *= objectHeight; }
            double w = Math.Ceiling((width + 2 * outsetX) * _maximumDensity), h = Math.Ceiling((height + 2 * outsetY) * _maximumDensity);
            Check(double.IsFinite(w) && double.IsFinite(h) && w > 0 && h > 0 && w <= int.MaxValue && h <= int.MaxValue,
                "filter 工作区域超过有限预算。", filter, "PackageSvgBudgetExceeded");
            long bytes;
            try { bytes = checked((long)w * (long)h * 4 * (passes + 2)); }
            catch (OverflowException error) { throw Fail("PackageSvgBudgetExceeded", "filter 工作面字节溢出。", filter, error); }
            _usage.Filter(bytes, _sourcePath); _filterBytes = checked(_filterBytes + bytes);
        }
    }
}
