using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering.Skia;

internal sealed partial class SceneCompositionProgram
{
    /// <summary>
    /// 同一部件按两态拆开的两侧各自的（前景，擦除）对，按扫字遮罩 m 逐像素插值成一对（P3 设计 §10.2 第 2 条）：
    /// (lerp(c<sub>U</sub>, c<sub>S</sub>, m), lerp(k<sub>U</sub>, k<sub>S</sub>, m))。
    /// </summary>
    /// <param name="unsung">未唱一侧。</param>
    /// <param name="sung">已唱一侧。</param>
    /// <param name="glyph">该部件的字形覆盖。</param>
    /// <param name="sungCoverage">该部件的已唱覆盖（过渡带内为字形覆盖 × m）。</param>
    /// <param name="surfaceBudget">共享帧账；为空时自开一帧。</param>
    /// <param name="cancellationToken">取消。</param>
    /// <returns>由调用方拥有的一对；框为两侧框的并集。</returns>
    /// <remarks>
    /// 两种合成模式对下层都是仿射的，comp(below) = c + below·(1 − k)，插值后按替换规则合成一次恰好等于两侧完整结果的交叉淡化。
    /// 借用全部输入；m 取已唱覆盖 ÷ 字形覆盖。字形覆盖为 0 处两侧不重叠，直接相加。
    /// </remarks>
    internal ImageCompositionPayload MixWipePayloads(ImageCompositionPayload unsung, ImageCompositionPayload sung,
        SkiaGraphSurface glyph, SkiaGraphSurface sungCoverage, ImageSurfaceBudgetFrame? surfaceBudget = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(unsung); ArgumentNullException.ThrowIfNull(sung);
        ArgumentNullException.ThrowIfNull(glyph); ArgumentNullException.ThrowIfNull(sungCoverage);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this); cancellationToken.ThrowIfCancellationRequested();
            ValidateCompositionContext(sung.Context, unsung.Context);
            ValidatePayload(unsung, cancellationToken); ValidatePayload(sung, cancellationToken);
            ValidateSurface(glyph, ShaderSurfaceKind.Coverage, "wipe.glyph", cancellationToken);
            ValidateSurface(sungCoverage, ShaderSurfaceKind.Coverage, "wipe.sung", cancellationToken);
            var context = unsung.Context;
            var bounds = Aligned(PayloadUnion(unsung.Color.Descriptor.Bounds, sung.Color.Descriptor.Bounds), context);
            using var ownFrame = surfaceBudget is null ? _surfaceBudget.BeginFrame() : null;
            var resources = surfaceBudget ?? ownFrame!;
            var inputs = new[] { unsung.Color, unsung.Erasure, sung.Color, sung.Erasure, glyph, sungCoverage }
                .DistinctBy(surface => surface.Image.Handle).ToArray();
            using var borrowed = resources.BorrowInputs(inputs);
            CheckBudget(inputs.Length + 2, checked(inputs.Sum(Bytes) + SizeBytes(bounds, context) * 2));
            resources.CheckAdditional(2, checked(SizeBytes(bounds, context) * 2), "MixWipePayloads");
            using var glyphShader = InputShader(glyph, GraphEdgeMode.Transparent); using var sungShader = InputShader(sungCoverage, GraphEdgeMode.Transparent);
            using var unsungColor = InputShader(unsung.Color, GraphEdgeMode.Transparent); using var sungColor = InputShader(sung.Color, GraphEdgeMode.Transparent);
            using var unsungErasure = InputShader(unsung.Erasure, GraphEdgeMode.Transparent); using var sungErasure = InputShader(sung.Erasure, GraphEdgeMode.Transparent);
            using var color = _payloadPrograms.WipeColor.CreateShader(UniformValues.Empty, new Dictionary<string, RuntimeShaderInput>
            {
                ["unsungSide"] = new(unsungColor, ShaderSurfaceKind.Color), ["sungSide"] = new(sungColor, ShaderSurfaceKind.Color),
                ["glyph"] = new(glyphShader, ShaderSurfaceKind.Coverage), ["sung"] = new(sungShader, ShaderSurfaceKind.Coverage),
            }, cancellationToken);
            using var erasure = _payloadPrograms.WipeErasure.CreateShader(UniformValues.Empty, new Dictionary<string, RuntimeShaderInput>
            {
                ["unsungSide"] = new(unsungErasure, ShaderSurfaceKind.Coverage), ["sungSide"] = new(sungErasure, ShaderSurfaceKind.Coverage),
                ["glyph"] = new(glyphShader, ShaderSurfaceKind.Coverage), ["sung"] = new(sungShader, ShaderSurfaceKind.Coverage),
            }, cancellationToken);
            return RasterPayloadPair(bounds, context, color, erasure, resources, 1, 1, cancellationToken);
        }
    }
}
