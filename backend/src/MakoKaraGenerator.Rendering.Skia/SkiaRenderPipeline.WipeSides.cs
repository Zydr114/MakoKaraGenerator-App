using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering.Skia;

// 图场景中按两态拆开的填充部件在过渡带内的合并（P3 设计 §10.2）。
public sealed partial class SkiaRenderPipeline
{
    /// <summary>
    /// 把刚加入的未唱、已唱两侧（层表末尾两层）按扫字遮罩合并成一层，占原来两层的位置。
    /// </summary>
    /// <remarks>
    /// 两侧各画本侧硬区 ∪ 过渡带，在带内重叠；依次合成会让下层透出来（两侧都不透明时带内为 已唱·m + 未唱·(1 − m)² + 下层·m(1 − m)）。
    /// 每侧先求（前景 c，擦除 k）：原样一侧 k = α，材质一侧取其链上历史（SourceOver 为 α，ReplaceCoveredBackdrop 为声明覆盖与历史替换范围的并）；
    /// 合并后的一对为 (lerp(c<sub>U</sub>, c<sub>S</sub>, m), lerp(k<sub>U</sub>, k<sub>S</sub>, m))，以闭合对的形式进入层组装或组合导出，
    /// 按替换规则合成一次，恰好等于两侧完整结果的交叉淡化。
    /// 合并层保留两侧的材质历史：后续图读取它时，历史替换范围与不合并时相同。
    /// m 由该部件的已唱覆盖 ÷ 字形覆盖得到，两张覆盖按与部件捕获相同的行／族世界矩阵与扫字分区画出。
    /// </remarks>
    private void MergeWipeSides(RenderRequest request, ObjectRenderPlan plan, ImageObjectState state, ObjectPartStep step)
    {
        int index = state.Layers.Count - 2;
        ColorLayer unsung = state.Layers[index], sung = state.Layers[index + 1];
        PartSource source = new(step.Member, step.Part);
        if (!unsung.Sources.SetEquals([source]) || !sung.Sources.SetEquals([source]))
            throw new PreparedFrameException("SceneWipeSidesMismatch", "扫字拆侧的两层不相邻或不属于同一部件。");
        var target = CaptureTarget(step.Member);
        var excluded = step.Member == plan.Target ? plan.ExcludedTokens : null;
        var resources = state.Resources; var cancellationToken = request.CancellationToken;
        using var glyph = CaptureWipeCoverage(request, target, step.Part, GraphInputSource.GlyphCoverage, state, excluded);
        using var sungCoverage = CaptureWipeCoverage(request, target, step.Part, GraphInputSource.SungCoverage, state, excluded);
        var context = CompositionContext(request, state.Bounds);
        using var unsungPair = SidePayload(unsung);
        using var sungPair = SidePayload(sung);
        var merged = state.Own(_compositionProgram.MixWipePayloads(unsungPair, sungPair, glyph, sungCoverage, resources, cancellationToken));
        var material = sung.History.IsEmpty ? unsung : sung;
        state.Layers.RemoveRange(index, 2);
        state.Layers.Add(new(step.Order, [source], merged.Color, unsung.History.AddRange(sung.History), material.Graph, material.Owner)
            { ClosedPayload = merged });
        state.SetLayerBudget(state.Layers.Count);

        ImageCompositionPayload SidePayload(ColorLayer layer) => layer.History.IsEmpty
            ? _compositionProgram.CaptureCompositionPayload(layer.Surface, context, resources, cancellationToken: cancellationToken)
            : _compositionProgram.CaptureCompositionPayload(layer.History, resources, cancellationToken: cancellationToken);
    }

    /// <summary>扫字遮罩用的一张部件覆盖（字形覆盖或已唱覆盖），只含该部件的字形，范围是对象工作框。</summary>
    private SkiaGraphSurface CaptureWipeCoverage(RenderRequest request, SceneCaptureTarget target, SceneColorPart part, GraphInputSource source,
        ImageObjectState state, HashSet<string>? excludedTokens)
    {
        var recipe = new SceneCaptureRecipe(IsRuby(part), false, false, false, SceneCaptureRegion.Fill, IncludeGlow: false);
        var item = SelectCaptureItems(request.PreparedFrame!.HierarchyScene, target, recipe).Single();
        return RasterSceneSurface(request, state.Bounds, state.Budget, ShaderSurfaceKind.Coverage, state.Resources, "CaptureWipeCoverage",
            (canvas, resources, bytes) =>
            {
                var paint = new CapturePaint(source, recipe, CaptureTokenId(target), resources, bytes, part, excludedTokens);
                if (item.Line is { } line) DrawSceneLine(canvas, request, request.Project.Composition, line, paint);
                else DrawTextMark(canvas, request, request.Project.Composition, item.Mark!, paint);
            });
    }
}
