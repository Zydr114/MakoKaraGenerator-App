using System.Collections.Immutable;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering.Skia;

public sealed partial class SkiaRenderPipeline
{
    /// <summary>逻辑像素的抗锯齿与取整余量。</summary>
    private const double ObjectRegionMarginPx = 2;
    /// <summary>Skia 默认斜接上限；描边按 2 倍宽外扩绘制，尖角最远伸出 MiterLimit × 宽。</summary>
    private const double StrokeMiterLimit = 4;

    /// <summary>按对象实际区域收紧材质框（O7）与图导出的目标框（第 12 条）；仅测试关闭以对照整屏框的像素。</summary>
    internal bool TightObjectRegions { get; set; } = true;

    /// <summary>
    /// 本帧对象（行或文字标记）的实际绘制范围：各元素、注音、字簇的墨迹与 Advance 框加描边、阴影、辉光外扩，
    /// 经最终世界矩阵或网格映射到画布，再加抗锯齿余量并与画布相交。没有可绘制墨迹时返回 null，调用方沿用程序框。
    /// 给出 family 时只取该元素族（基础字、注音与字簇），否则取整行或整个标记。
    /// </summary>
    private GraphRect? ObjectTargetRegion(RenderRequest request, SceneDrawItem item, ImageSurfaceBudgetFrame resources, TokenFamilyTarget? family = null)
    {
        if (!TightObjectRegions) return null;
        var prepared = request.PreparedFrame!;
        var projection = _warpProjectionBudgets.GetValue(resources, static _ => new());
        GraphRect? region = null;
        if (item.Line is { } line)
        {
            var sources = StaticStyleProjection.LoadedSourceStyles(request.Project, prepared.HierarchyScene);
            foreach (var token in prepared.FinalGeometry.Where(sample => sample.WithRuby.BindingId.Value == line.BindingId
                && (family is null || sample.WithRuby == family)))
            {
                request.CancellationToken.ThrowIfCancellationRequested();
                var familyStyle = prepared.TokenStyles.Any(output => output.Target == token.WithRuby)
                    ? prepared.GetTokenDrawingStyle(token.WithRuby) : sources[token.WithRuby];
                var basic = prepared.BaseAppearances.ContainsKey(token.BaseOnly) ? prepared.GetBaseDrawingStyle(token.BaseOnly) : familyStyle;
                Add(basic, token.BaseGeometry);
                foreach (var ruby in token.Ruby) Add(familyStyle, ruby.Geometry);
                foreach (var cluster in token.Clusters) Add(cluster.Target.RubyId is null ? basic : familyStyle, cluster.Geometry);
            }
        }
        else
        {
            var mark = item.Mark!;
            var final = prepared.FinalMarkGeometry.Single(value => value.Target.NodeId.Value == mark.NodeId
                && value.Target.CueId.Value == mark.CueId && value.Target.BindingId.Value == mark.Geometry.BindingId);
            var style = prepared.MarkStyles.Any(output => output.Target == final.Target)
                ? prepared.GetMarkDrawingStyle(final.Target) : mark.Appearance.ToLyricStyle();
            Add(style, final.Geometry);
        }
        if (region is not { } value) return null;
        var composition = request.Project.Composition;
        double left = Math.Max(0, value.Left - ObjectRegionMarginPx), top = Math.Max(0, value.Top - ObjectRegionMarginPx);
        double right = Math.Min(composition.CanvasWidth, value.Right + ObjectRegionMarginPx), bottom = Math.Min(composition.CanvasHeight, value.Bottom + ObjectRegionMarginPx);
        if (!(right > left && bottom > top)) return null; // 完全在画布外：沿用程序框，行为与收紧前相同。
        return new GraphRect(left, top, right, bottom);

        void Add(LyricStyle style, GeometrySample geometry)
        {
            var source = geometry.InkBounds is { } ink ? UnionRect(ink, geometry.AdvanceBounds) : geometry.AdvanceBounds;
            double extent = RegionDecorationExtent(style);
            Rect2 decorated = new(source.Left - extent, source.Top - extent, source.Right + extent, source.Bottom + extent);
            var corners = geometry.Mapping is { } mapping ? mapping.MapRectangle(decorated, projection)
                : new[] { new Point2(decorated.Left, decorated.Top), new(decorated.Right, decorated.Top), new(decorated.Right, decorated.Bottom), new(decorated.Left, decorated.Bottom) }
                    .Select(geometry.BaseWorldMatrix.Map).ToImmutableArray();
            var mapped = new GraphRect(corners.Min(point => point.X), corners.Min(point => point.Y), corners.Max(point => point.X), corners.Max(point => point.Y));
            if (!mapped.IsValid) return;
            region = region is { } current ? Union(current, mapped) : mapped;
        }
    }

    /// <summary>描边（斜接按上限）＋阴影（偏移＋3σ）与辉光（扩张＋3σ）中较大者，单位为元素本地逻辑像素。</summary>
    private static double RegionDecorationExtent(LyricStyle style)
    {
        double stroke = style.Stroke is { IsEnabled: true } outline ? outline.WidthPx * (outline.Join == MakoKaraGenerator.Core.Projects.StrokeJoin.Miter ? StrokeMiterLimit : 1) : 0;
        double shadow = style.Shadow is { IsEnabled: true } shade
            ? Math.Max(Math.Abs(shade.OffsetXPx), Math.Abs(shade.OffsetYPx)) + 3d * shade.BlurRadiusPx : 0;
        double glow = style.Glow is { IsEnabled: true } halo ? halo.SpreadPx + 3d * halo.BlurRadiusPx : 0;
        return Math.Max(stroke + shadow, glow);
    }

    /// <summary>材质工作框：目标框加图声明外扩，不超出程序框（与收紧前整框一致的上限）。</summary>
    private static GraphRect MaterialWorkBounds(GraphRect? region, SceneMaterialProgram program)
    {
        if (region is null) return program.Bounds;
        var clipped = Intersect(MaterialTargetBounds(region, program).Expand(program.Graph.Plan.Definition.MaximumExpandPx), program.Bounds);
        return clipped.IsValid ? clipped : program.Bounds;
    }

    /// <summary>
    /// 把逻辑工作框按实际输出密度外扩到整像素边界，与 <c>RasterSceneSurface</c> 分配的像素框一致。
    /// 透明度合成等步骤按逻辑框整面绘制（不抗锯齿），框边落在像素中心之前会漏掉边缘一行／列，再以 Src 写回成透明。
    /// </summary>
    private static GraphRect AlignToOutputPixels(RenderRequest request, GraphRect bounds)
    {
        var output = request.ResolveTarget(); var composition = request.Project.Composition;
        double dx = output.ScaleXOf(composition), dy = output.ScaleYOf(composition);
        return new(Math.Floor(bounds.Left * dx) / dx, Math.Floor(bounds.Top * dy) / dy,
            Math.Ceiling(bounds.Right * dx) / dx, Math.Ceiling(bounds.Bottom * dy) / dy);
    }

    private static Rect2 UnionRect(Rect2 a, Rect2 b)
        => new(Math.Min(a.Left, b.Left), Math.Min(a.Top, b.Top), Math.Max(a.Right, b.Right), Math.Max(a.Bottom, b.Bottom));

    /// <summary>
    /// 材质本次执行的目标框：对象区域与程序框相交。材质输出受区域覆盖遮罩，图节点按目标框加声明外扩计算，
    /// 因此区域内像素与整屏框一致。含 Transform 节点的材质图保留程序框，因其静态输出框按画布写定，须落在「目标框＋最大外扩」内。
    /// </summary>
    private static GraphRect MaterialTargetBounds(GraphRect? region, SceneMaterialProgram program)
    {
        var programBounds = program.Bounds;
        if (region is not { } value || program.Graph.Plan.Nodes.Any(node => node is ImageGraphNode.Transform)) return programBounds;
        var clipped = Intersect(value, programBounds);
        return clipped.IsValid ? clipped : programBounds;
    }

    /// <summary>
    /// 图导出各次执行的目标框（第 12 条）：绑定目标（整行、元素族或标记）的实际区域，加同一对象内先于它执行、
    /// 产物会流入它的图的最大外扩之和，再与程序框相交。
    /// </summary>
    /// <remarks>
    /// <para>元素族图按工程顺序依次读取同一元素先前的图产物，整行图读取全部元素图产物与先前的整行图产物；
    /// 按声明的最大外扩累计，前序产物（外扩不超过声明）不会被裁掉。</para>
    /// <para>只由本帧几何与图声明决定，不看调度器给的工作框，独立对象与组合调度两条路径结果一致。</para>
    /// <para>图中有模糊时按 Skia GPU 的缩小倍数对齐到画布像素网格（见 <see cref="AlignToBlurGrid"/>）。</para>
    /// <para>本图含 Transform 节点（静态输出框按画布写定）、目标没有可绘制墨迹或收紧关闭时沿用程序框；
    /// 一旦某个图沿用程序框，产物会流入的后续图也沿用。</para>
    /// <para>sequence 为本对象的图绑定，按实际执行顺序：各元素族（按行内顺序）在前，整行或标记在后。</para>
    /// </remarks>
    private Dictionary<PreparedImageEffectBinding, GraphRect> GraphTargetBounds(RenderRequest request, SceneDrawItem item, ImageEffectTarget objectTarget,
        IEnumerable<(PreparedImageEffectBinding Binding, SceneImageGraphProgram Program)> sequence, ImageSurfaceBudgetFrame resources)
    {
        var output = request.ResolveTarget(); var composition = request.Project.Composition;
        double dx = output.ScaleXOf(composition), dy = output.ScaleYOf(composition);
        Dictionary<PreparedImageEffectBinding, GraphRect> result = [];
        Dictionary<ImageEffectTarget, GraphRect?> regions = [];
        Dictionary<ImageEffectTarget, (double Expand, bool Whole)> families = [];
        (double Expand, bool Whole) whole = (0, false);
        foreach (var (binding, program) in sequence)
        {
            var member = binding.Binding.Target;
            bool isObject = member == objectTarget;
            var (prior, fallback) = isObject ? whole : families.GetValueOrDefault(member);
            if (!regions.TryGetValue(member, out var region))
                regions.Add(member, region = ObjectTargetRegion(request, item, resources, (member as ImageEffectTarget.Token)?.Target));
            var bounds = program.Bounds;
            bool tight = false;
            if (!fallback && region is { } value && !program.Graph.Plan.Nodes.Any(node => node is ImageGraphNode.Transform))
            {
                var clipped = Intersect(AlignToBlurGrid(value.Expand(prior), program, binding.Binding.Parameters, dx, dy), program.Bounds);
                if (clipped.IsValid) { bounds = clipped; tight = true; }
            }
            result[binding] = bounds;
            (double Expand, bool Whole) next = (prior + program.Graph.Plan.Definition.MaximumExpandPx, !tight);
            if (isObject) whole = next;
            else
            {
                families[member] = next;
                whole = (Math.Max(whole.Expand, next.Expand), whole.Whole || next.Whole);
            }
        }
        return result;
    }

    /// <summary>
    /// Skia GPU 对 σ 大于 4 物理像素的模糊先按 2 的幂缩小再模糊，缩小网格随表面原点的奇偶变化。
    /// 把目标框外扩到「缩小倍数」物理像素的画布网格上，节点表面原点便与整屏框同余，GPU 结果与整屏框一致
    /// （实测 Vulkan σ8：不对齐差 9／255，对齐后为 0）；CPU 光栅不受影响。没有模糊或 σ 不超过 4 时不改。
    /// </summary>
    private static GraphRect AlignToBlurGrid(GraphRect bounds, SceneImageGraphProgram program, ParameterValues parameters, double dx, double dy)
    {
        double sigma = program.Graph.Plan.Nodes.OfType<ImageGraphNode.Blur>()
            .Select(blur => ImageGraphBindings.Number(blur.SigmaPx, parameters)).DefaultIfEmpty(0).Max();
        double gx = BlurDownscale(sigma * dx), gy = BlurDownscale(sigma * dy);
        if (gx == 1 && gy == 1) return bounds;
        return new(Math.Floor(bounds.Left * dx / gx) * gx / dx, Math.Floor(bounds.Top * dy / gy) * gy / dy,
            Math.Ceiling(bounds.Right * dx / gx) * gx / dx, Math.Ceiling(bounds.Bottom * dy / gy) * gy / dy);

        static double BlurDownscale(double sigma)
        {
            double factor = 1;
            while (sigma > 4 && factor < 1024) { factor *= 2; sigma /= 2; }
            return factor;
        }
    }

    private static GraphRect Intersect(GraphRect a, GraphRect b)
        => new(Math.Max(a.Left, b.Left), Math.Max(a.Top, b.Top), Math.Min(a.Right, b.Right), Math.Min(a.Bottom, b.Bottom));
}
