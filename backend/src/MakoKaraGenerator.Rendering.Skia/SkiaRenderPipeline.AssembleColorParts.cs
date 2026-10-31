using MakoKaraGenerator.Core.Effects;
using SkiaSharp;

namespace MakoKaraGenerator.Rendering.Skia;

public sealed partial class SkiaRenderPipeline
{
    /// <summary>按调用者显式顺序重组同目标部件，产出独立 TargetColor；共同 alpha 保留元数据，不烘入。</summary>
    /// <remarks>同一准备帧／源快照／实际输出／质量／时刻；不得重叠提交同一部件或裁掉输入框。不决定未纳入部件的叠放规则。</remarks>
    public SceneColorPartsAssembly AssembleSceneColorParts(RenderRequest request, IReadOnlyList<SceneColorPartResult> orderedParts,
        GraphRect bounds, GraphExecutionBudget budget, ImageSurfaceBudgetFrame? surfaceBudget = null)
    {
        ArgumentNullException.ThrowIfNull(request); ArgumentNullException.ThrowIfNull(orderedParts); ArgumentNullException.ThrowIfNull(budget);
        ObjectDisposedException.ThrowIf(_disposed, this); request.CancellationToken.ThrowIfCancellationRequested();
        PreparedRenderFrame.ValidateRequest(request); ValidateNativePreparedFrame(request.PreparedFrame);
        if (orderedParts.Count == 0 || orderedParts.Any(part => part is null))
            throw new PreparedFrameException("SceneColorPartsEmpty", "重组必须提供实际部件和明确顺序。");
        var first = orderedParts[0];
        if (orderedParts.Select(part => part.Part).Distinct().Count() != orderedParts.Count)
            throw new PreparedFrameException("SceneColorPartDuplicate", "同一目标的同一部件不能重复重组。");
        foreach (var part in orderedParts)
        {
            var source = part.Request;
            if (!ReferenceEquals(source.Project, request.Project) || !ReferenceEquals(source.PreparedFrame, request.PreparedFrame)
                || source.TimeUs != request.TimeUs || source.ResolveTarget() != request.ResolveTarget() || source.TransparentBackground != request.TransparentBackground
                || source.Quality != request.Quality || source.LayoutLineId != request.LayoutLineId || part.Target != first.Target
                || part.CommonOpacity != first.CommonOpacity || part.OpacityByte != first.OpacityByte)
                throw new PreparedFrameException("SceneColorPartFrameMismatch", "不能重组不同源／准备帧／时刻／输出／质量／目标的部件。");
            if (!bounds.IsValid || !bounds.Contains(part.Surface.Descriptor.Bounds))
                throw new PreparedFrameException("SceneColorPartBoundsExceeded", "重组框不能裁掉部件的显式捕获框。");
        }
        var inputs = orderedParts.Select(part => part.Surface).DistinctBy(surface => surface.Image.Handle).ToArray();
        long inputBytes = inputs.Sum(surface => checked((long)surface.Image.Width * surface.Image.Height * 4));
        using var ownFrame = surfaceBudget is null ? _surfaceBudget.BeginFrame() : null;
        var resources = surfaceBudget ?? ownFrame!;
        using var borrowed = resources.BorrowInputs(inputs);
        var output = RasterSceneSurface(request, bounds, budget, ShaderSurfaceKind.Color, resources, "AssembleSceneColorParts", (canvas, _, _) =>
        {
            foreach (var part in orderedParts)
            {
                request.CancellationToken.ThrowIfCancellationRequested();
                DrawGraphSurface(canvas, part.Surface, SKBlendMode.SrcOver);
            }
        }, bytes =>
        {
            if (inputs.Length + 1 > budget.MaximumSurfaces || checked(inputBytes + bytes) > budget.MaximumBytes)
                throw new PreparedFrameException("SceneColorPartsBudgetExceeded", "重组输入与独立产物超过固定预算。");
        });
        return new(first.Target, [.. orderedParts.Select(part => part.Part)], output, request.TimeUs, first.CommonOpacity, first.OpacityByte);
    }
}
