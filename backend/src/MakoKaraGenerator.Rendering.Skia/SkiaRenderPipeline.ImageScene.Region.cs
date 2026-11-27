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
        var composition = request.Project.Composition;
        return ClampObjectRegion(ObjectInkRegion(request, item, resources, family), new(0, 0, composition.CanvasWidth, composition.CanvasHeight));
    }

    /// <summary>
    /// 对象（或一个元素族）本帧墨迹与装饰外扩映射到画布后的范围，未加余量、未裁剪；没有可绘制墨迹时为 null。
    /// <paramref name="projectWarp" /> 为假时遇到网格映射（Warp）即放弃并返回 null，不动用本帧的投影工作账。
    /// </summary>
    private GraphRect? ObjectInkRegion(RenderRequest request, SceneDrawItem item, ImageSurfaceBudgetFrame resources, TokenFamilyTarget? family = null,
        bool projectWarp = true)
    {
        var prepared = request.PreparedFrame!;
        var projection = _warpProjectionBudgets.GetValue(resources, static _ => new());
        GraphRect? region = null;
        bool abandoned = false;
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
                foreach (var ruby in token.Ruby) Add(RubyStyle(familyStyle, line, ruby.RubyId), ruby.Geometry);
                foreach (var cluster in token.Clusters) Add(cluster.Target.RubyId is { } rubyId ? RubyStyle(familyStyle, line, rubyId) : basic, cluster.Geometry);
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
        return abandoned ? null : region;

        void Add(LyricStyle style, GeometrySample geometry)
        {
            if (abandoned) return;
            if (!projectWarp && geometry.Mapping is not null) { abandoned = true; return; }
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

    /// <summary>墨迹范围加抗锯齿余量后与界限相交；完全落在界限外时为 null（调用方沿用原框，行为与收紧前相同）。</summary>
    private static GraphRect? ClampObjectRegion(GraphRect? ink, GraphRect limit)
    {
        if (ink is not { } value) return null;
        double left = Math.Max(limit.Left, value.Left - ObjectRegionMarginPx), top = Math.Max(limit.Top, value.Top - ObjectRegionMarginPx);
        double right = Math.Min(limit.Right, value.Right + ObjectRegionMarginPx), bottom = Math.Min(limit.Bottom, value.Bottom + ObjectRegionMarginPx);
        if (!(right > left && bottom > top)) return null;
        return new GraphRect(left, top, right, bottom);
    }

    /// <summary>
    /// 逐元素几何能否代表整个对象：行的全部绘制单元都要落在某个元素的基础字几何里（与测量侧取整行墨迹的规则相同）。
    /// 元素拼不回行文本、行没有元素时不成立，调用方不收紧。文字标记只有一份几何，总是成立。
    /// </summary>
    private static bool ObjectInkCoversWholeObject(RenderRequest request, SceneDrawItem item)
    {
        if (item.Line is not { } line) return true;
        var prepared = request.PreparedFrame!;
        var whole = prepared.FinalLineGeometry.FirstOrDefault(sample => sample.Target.BindingId.Value == line.BindingId);
        if (whole is null || whole.BaseGeometry.DrawUnits.IsEmpty) return false;
        var units = prepared.FinalGeometry.Where(token => token.WithRuby.BindingId.Value == line.BindingId)
            .SelectMany(token => token.BaseGeometry.DrawUnits).ToHashSet();
        return whole.BaseGeometry.DrawUnits.All(units.Contains);
    }

    /// <summary>
    /// 整体轮廓外扩（各层累计描边宽度，斜接按上限）＋阴影（偏移＋3σ）与辉光（扩张＋3σ）中较大者，单位为元素本地逻辑像素；
    /// 两态不同时取较大者。阴影与辉光都以整体轮廓为来源（D-P3-9，P3 设计 §10.6 第 4 条）。
    /// </summary>
    private static double RegionDecorationExtent(LyricStyle style)
    {
        var sung = style.ForState(true);
        return ReferenceEquals(sung, style) ? StateExtent(style) : Math.Max(StateExtent(style), StateExtent(sung));
    }

    private static double StateExtent(LyricStyle style)
    {
        double outline = OutlineExtent(style.Unsung.Strokes);
        double shadow = style.Shadow is { IsEnabled: true } shade
            ? Math.Max(Math.Abs(shade.OffsetXPx), Math.Abs(shade.OffsetYPx)) + 3d * shade.BlurRadiusPx + shade.SpreadPx : 0;
        double glow = style.Glow is { IsEnabled: true } halo ? halo.SpreadPx + 3d * halo.BlurRadiusPx : 0;
        return outline + Math.Max(Math.Max(shadow, glow), 0);
    }

    /// <summary>注音实际绘制所用的样式：所属元素族样式的装饰按该注音的比例缩放（P3 设计 §7.2）；行里找不到该注音时按 1 计。</summary>
    private static MakoKaraGenerator.Core.Projects.LyricStyle RubyStyle(MakoKaraGenerator.Core.Projects.LyricStyle familyStyle, SceneLineSample line, RubyId rubyId)
        => familyStyle.ScaleDecorations(line.Frame.Line?.RubyAnnotations.FirstOrDefault(annotation => annotation.Id == rubyId.Value) is { } annotation
            ? familyStyle.Ruby.ResolveScale(annotation) : 1d);

    /// <summary>整体轮廓画到字框之外的像素数：各层累计宽度乘该层拐角系数（斜接按上限）的最大值；不绘制时为 0。</summary>
    private static double OutlineExtent(MakoKaraGenerator.Core.Projects.StrokeLayers strokes)
    {
        if (!strokes.IsEnabled) return 0;
        double outline = 0, reach = 0;
        foreach (var layer in strokes)
        {
            reach += layer.WidthPx;
            outline = Math.Max(outline, reach * (layer.Join == MakoKaraGenerator.Core.Projects.StrokeJoin.Miter ? StrokeMiterLimit : 1));
        }
        return outline;
    }

    /// <summary>
    /// 材质工作框：目标框加图声明外扩，不超出程序框（与收紧前整框一致的上限）。
    /// sampleReachPx 是另外并入的取样距离（见 <see cref="GraphSampleReachPx" />）；收紧前的整框不并入。
    /// </summary>
    private static GraphRect MaterialWorkBounds(GraphRect? region, SceneMaterialProgram program, double sampleReachPx = 0)
    {
        if (region is null) return program.Bounds;
        var clipped = Intersect(MaterialTargetBounds(region, program).Expand(program.Graph.Plan.Definition.MaximumExpandPx + sampleReachPx), program.Bounds);
        return clipped.IsValid ? clipped : program.Bounds;
    }

    /// <summary>
    /// 图内各节点声明的最大取样偏移之和：节点可以在这个距离内读取输入，是「输入须覆盖到目标框之外多远」的保守上界。
    /// 它不计入图的最大外扩（外扩只约束输出框）。
    /// 只有声明了下层、背景底色或背景媒体输入（含可选来源）的图才需要：这些输入按工作框捕获，框外另有像素；
    /// 对象自身的捕获（颜色、各类覆盖）在目标框外本来就是透明的，包图片不按工作框裁剪。
    /// </summary>
    private static double GraphSampleReachPx(CompiledImageGraph graph)
        => graph.Plan.Definition.Inputs.Any(input => ReadsOutsideObject(input.Source) || input.Choices.Values.Any(ReadsOutsideObject))
            ? graph.Plan.Nodes.Sum(node => node.Bounds.MaximumSampleOffsetPx) : 0;

    private static bool ReadsOutsideObject(GraphInputSource source)
        => source is GraphInputSource.BackdropBelow or GraphInputSource.BackgroundColor or GraphInputSource.BackgroundMedia;

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

    /// <summary>
    /// 单个成员的原样部件捕获框：即便对象工作框因 Transform 图退回全画布，
    /// 成员的原样部件（辉光／阴影／描边／填充）仍可按自身墨迹与装饰范围收紧在工作框内，
    /// 后续组装、扫字合并与组合导出均按表面自身坐标框绘制。
    /// </summary>
    private GraphRect MemberOriginalCaptureBounds(RenderRequest request, ObjectRenderPlan plan, ImageEffectTarget member,
        ImageSurfaceBudgetFrame resources)
    {
        var bounds = plan.WorkBounds;
        if (!TightObjectRegions) return bounds;
        GraphRect? ink;
        if (member is ImageEffectTarget.Token token)
            ink = ObjectInkRegion(request, plan.Item, resources, token.Target, projectWarp: false);
        else if (ObjectInkCoversWholeObject(request, plan.Item))
            ink = ObjectInkRegion(request, plan.Item, resources, projectWarp: false);
        else return bounds;
        if (ClampObjectRegion(ink, bounds) is not { } tight) return bounds;
        var clipped = Intersect(AlignToOutputPixels(request, tight), bounds);
        return clipped.IsValid ? clipped : bounds;
    }

    /// <summary>共享空表面的最小像素框；收紧关闭时沿用对象工作框。</summary>
    private GraphRect EmptySurfaceBounds(RenderRequest request, GraphRect bounds)
    {
        if (!TightObjectRegions) return bounds;
        var output = request.ResolveTarget(); var composition = request.Project.Composition;
        double dx = output.ScaleXOf(composition), dy = output.ScaleYOf(composition);
        if (!double.IsFinite(dx) || !double.IsFinite(dy) || dx <= 0 || dy <= 0) return bounds;
        double right = Math.Min(bounds.Left + 1d / dx, bounds.Right);
        double bottom = Math.Min(bounds.Top + 1d / dy, bounds.Bottom);
        return right > bounds.Left && bottom > bounds.Top ? new(bounds.Left, bounds.Top, right, bottom) : bounds;
    }
}
