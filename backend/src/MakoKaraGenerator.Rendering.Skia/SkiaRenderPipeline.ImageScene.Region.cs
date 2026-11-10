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

    /// <summary>按对象实际区域收紧材质框（O7）；仅测试关闭以对照整屏框的像素。</summary>
    internal bool TightObjectRegions { get; set; } = true;

    /// <summary>
    /// 本帧对象（行或文字标记）的实际绘制范围：各元素、注音、字簇的墨迹与 Advance 框加描边、阴影、辉光外扩，
    /// 经最终世界矩阵或网格映射到画布，再加抗锯齿余量并与画布相交。没有可绘制墨迹时返回 null，调用方沿用程序框。
    /// </summary>
    private GraphRect? ObjectTargetRegion(RenderRequest request, SceneDrawItem item, ImageSurfaceBudgetFrame resources)
    {
        if (!TightObjectRegions) return null;
        var prepared = request.PreparedFrame!;
        var projection = _warpProjectionBudgets.GetValue(resources, static _ => new());
        GraphRect? region = null;
        if (item.Line is { } line)
        {
            var sources = StaticStyleProjection.LoadedSourceStyles(request.Project, prepared.HierarchyScene);
            foreach (var token in prepared.FinalGeometry.Where(token => token.WithRuby.BindingId.Value == line.BindingId))
            {
                request.CancellationToken.ThrowIfCancellationRequested();
                var family = prepared.TokenStyles.Any(output => output.Target == token.WithRuby)
                    ? prepared.GetTokenDrawingStyle(token.WithRuby) : sources[token.WithRuby];
                var basic = prepared.BaseAppearances.ContainsKey(token.BaseOnly) ? prepared.GetBaseDrawingStyle(token.BaseOnly) : family;
                Add(basic, token.BaseGeometry);
                foreach (var ruby in token.Ruby) Add(family, ruby.Geometry);
                foreach (var cluster in token.Clusters) Add(cluster.Target.RubyId is null ? basic : family, cluster.Geometry);
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
        var expanded = MaterialTargetBounds(region, program).Expand(program.Graph.Plan.Definition.MaximumExpandPx);
        GraphRect clipped = new(Math.Max(expanded.Left, program.Bounds.Left), Math.Max(expanded.Top, program.Bounds.Top),
            Math.Min(expanded.Right, program.Bounds.Right), Math.Min(expanded.Bottom, program.Bounds.Bottom));
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
    /// <remarks>
    /// 图导出仍按程序框（整张画布）执行。合同上图根框也来自目标区域，但现有图测试以目标区外的探针输出检验下层与组合，
    /// 图路径本身也不慢、1440p 不超预算，收紧另行安排。
    /// </remarks>
    private static GraphRect MaterialTargetBounds(GraphRect? region, SceneMaterialProgram program)
    {
        var programBounds = program.Bounds;
        if (region is not { } value || program.Graph.Plan.Nodes.Any(node => node is ImageGraphNode.Transform)) return programBounds;
        GraphRect clipped = new(Math.Max(value.Left, programBounds.Left), Math.Max(value.Top, programBounds.Top),
            Math.Min(value.Right, programBounds.Right), Math.Min(value.Bottom, programBounds.Bottom));
        return clipped.IsValid ? clipped : programBounds;
    }
}
