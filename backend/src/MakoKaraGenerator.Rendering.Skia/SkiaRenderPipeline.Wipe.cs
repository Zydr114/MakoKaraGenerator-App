using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Text;
using SkiaSharp;

namespace MakoKaraGenerator.Rendering.Skia;

public sealed partial class SkiaRenderPipeline
{
    /// <summary>
    /// 仅供测试：两态画出来相同时也走过渡带合成（P3 设计 §10.1 互补测试点）。默认关闭，两态相同时按硬切换只分两块。
    /// </summary>
    internal bool ForceWipeBand { get; set; }

    /// <summary>
    /// 一组绘制的扫字分区（P3 设计 §10.1）：已唱硬区、过渡带，以及过渡带内的遮罩 m（1 为已唱，0 为未唱）。
    /// 羽化为 0 时只有硬区，与 <see cref="BuildSungRegions"/> 的结果相同。
    /// </summary>
    private sealed class WipePartition(SKPath sung) : IDisposable
    {
        /// <summary>已唱硬区（m = 1）。</summary>
        public SKPath Sung { get; } = sung;

        /// <summary>过渡带（0 &lt; m &lt; 1）的矩形，未外扩。</summary>
        public List<SKRect> Band { get; } = [];

        /// <summary>遮罩的分段：每段在自己的矩形内取线性或常数的 m。</summary>
        public List<WipePiece> Pieces { get; } = [];

        public void Dispose() => Sung.Dispose();
    }

    /// <summary>
    /// 遮罩的一段：<see cref="Constant"/> 为空时在 <see cref="Rect"/> 内沿横向线性，<see cref="Full"/> 处 m = 1、<see cref="Empty"/> 处 m = 0，
    /// 两端之外夹到 0～1；否则整段取该常数。
    /// </summary>
    private readonly record struct WipePiece(SKRect Rect, float Full, float Empty, float? Constant = null);

    private static TokenSample BridgeSourceToken(TokenUntimedBridge bridge) =>
        new(bridge.SourceTokenIndex, bridge.SourceUtf16Start, bridge.SourceUtf16Length,
            bridge.SourceProgress, ProgressSource.FromToken, IsCurrent: false)
        {
            Time = bridge.SourceTime,
            ActiveSegment = bridge.SourceActiveSegment,
        };

    /// <summary>
    /// 按每个已定时元素的演唱位置建立扫字分区。各元素的羽化比例为 0 时与 <see cref="BuildSungRegions"/> 相同。
    /// </summary>
    /// <param name="sample">时刻采样结果。</param>
    /// <param name="shaped">整行布局。</param>
    /// <param name="originX">整行左端横坐标。</param>
    /// <param name="bounds">当前画布剪裁边界。</param>
    /// <param name="lineEdges">按扫字线延到行两端之外（见 <see cref="BuildSungRegions"/>）。</param>
    /// <param name="featherRatio">元素解析后的羽化比例。</param>
    /// <param name="fontSizePx">行字号；带宽 = 比例 × 行字号。</param>
    /// <returns>由调用方释放的分区。</returns>
    /// <remarks>
    /// 演唱位置不夹值：x(t) = L + (R − L)(t − s)/(e − s)，遮罩 m(x) = clamp(0.5 + d(x(t) − x)/W, 0, 1)，只作用在元素自己的横向区间内；
    /// 行首元素之前、行尾元素之后的延伸部分取区间端点处的 m（P3 设计 §10.1 第 1～5 条）。
    /// 未定时、零宽、零长或时间与进度对不上的元素照硬切换处理。
    /// </remarks>
    private static WipePartition BuildWipe(LyricFrameSample sample, ShapedLine shaped, float originX, SKRect bounds, bool lineEdges,
        Func<TokenSample, double> featherRatio, float fontSizePx)
    {
        if (!sample.HasTokens || !sample.TokensMatchLineText
            || !sample.Tokens.Any(token =>
                token.Source == ProgressSource.FromToken && featherRatio(token) > 0d
                || token.Bridge is { } bridge && featherRatio(BridgeSourceToken(bridge)) > 0d))
            return new(BuildSungRegions(sample, shaped, originX, bounds, lineEdges));

        WipePartition wipe = new(new SKPath());
        foreach (TokenSample token in sample.Tokens)
        {
            if (token.Source != ProgressSource.FromToken)
            {
                if (token.Bridge is not { } bridge) continue;
                float bridgeLeft = originX + shaped.XAt(token.Utf16Start);
                float bridgeRight = originX + shaped.XAt(token.Utf16End);
                float low = MathF.Min(bridgeLeft, bridgeRight), high = MathF.Max(bridgeLeft, bridgeRight);
                bool bridgeFirst = lineEdges && token.Utf16Start == 0, bridgeLast = lineEdges && token.Utf16End == shaped.Text.Length;
                if (bridgeFirst) low = Math.Min(low, bounds.Left);
                if (bridgeLast) high = Math.Max(high, bounds.Right);
                TokenSample sourceToken = BridgeSourceToken(bridge);
                SkiaSceneGeometry.TokenWipeSpan sourceSpan = SkiaSceneGeometry.ResolveTokenWipeSpan(shaped, bridge.SourceUtf16Start, bridge.SourceUtf16End);
                float srcInkLeft = originX + sourceSpan.InkLeft, srcInkRight = originX + sourceSpan.InkRight;
                float srcWidth = (float)(featherRatio(sourceToken) * fontSizePx);
                if (TokenSungPosition(sourceToken, sample.TimeUs, srcInkLeft, srcInkRight, srcWidth) is { } srcPosition)
                {
                    float srcDirection = srcInkRight > srcInkLeft ? 1f : -1f;
                    float anchor = bridge.IsLeading ? srcInkLeft : srcInkRight;
                    float mask = Math.Clamp(.5f + srcDirection * (srcPosition - anchor) / srcWidth, 0f, 1f);
                    AddConstant(wipe, new SKRect(low, bounds.Top, high, bounds.Bottom), mask);
                }
                else if (bridge.IsHardSung && high > low)
                {
                    wipe.Sung.AddRect(new SKRect(low, bounds.Top, high, bounds.Bottom));
                }
                continue;
            }

            SkiaSceneGeometry.TokenWipeSpan span = SkiaSceneGeometry.ResolveTokenWipeSpan(shaped, token.Utf16Start, token.Utf16End);
            float advLeft = originX + span.AdvanceLeft, advRight = originX + span.AdvanceRight;
            float inkLeft = originX + span.InkLeft, inkRight = originX + span.InkRight;
            bool first = lineEdges && token.Utf16Start == 0, last = lineEdges && token.Utf16End == shaped.Text.Length;
            float width = (float)(featherRatio(token) * fontSizePx);
            if (TokenSungPosition(token, sample.TimeUs, inkLeft, inkRight, width) is not { } position)
            {
                // 没有过渡带的元素按硬切换，与 BuildSungRegions 相同。
                if (token.Progress <= 0d) continue;
                float reached = token.IsSung ? advRight : Scripting.TextProgressPath.XAt(inkLeft, inkRight, token.Progress);
                if (first) advLeft = Math.Min(advLeft, bounds.Left);
                if (last && token.IsSung) reached = Math.Max(reached, bounds.Right);
                if (reached > advLeft) wipe.Sung.AddRect(new SKRect(advLeft, bounds.Top, reached, bounds.Bottom));
                continue;
            }
            AddRamp(wipe, bounds, inkLeft, inkRight, position, width);
            float direction = inkRight > inkLeft ? 1f : -1f;
            if (direction > 0f)
            {
                float leftOuter = first ? Math.Min(advLeft, bounds.Left) : advLeft;
                if (inkLeft > leftOuter) AddConstant(wipe, new(leftOuter, bounds.Top, inkLeft, bounds.Bottom), Mask(inkLeft));
                float rightOuter = last ? Math.Max(advRight, bounds.Right) : advRight;
                if (rightOuter > inkRight) AddConstant(wipe, new(inkRight, bounds.Top, rightOuter, bounds.Bottom), Mask(inkRight));
            }
            else
            {
                if (first) AddConstant(wipe, new(inkLeft, bounds.Top, bounds.Right, bounds.Bottom), Mask(inkLeft));
                if (last) AddConstant(wipe, new(bounds.Left, bounds.Top, inkRight, bounds.Bottom), Mask(inkRight));
            }
            float Mask(float x) => Math.Clamp(.5f + direction * (position - x) / width, 0f, 1f);
        }
        return wipe;
    }

    /// <summary>
    /// 文本元素的不夹值演唱位置：支持单段匀速与多拍注音字分段同步（契约 B1）。
    /// </summary>
    private static float? TokenSungPosition(TokenSample token, long timeUs, float inkLeft, float inkRight, float width)
    {
        if (!(width > 0f) || !float.IsFinite(width) || inkLeft == inkRight) return null;
        if (token.ActiveSegment is { } piece)
        {
            if (piece.InGap)
            {
                float gapPosition = inkLeft + (inkRight - inkLeft) * (float)piece.WeightEnd;
                return float.IsFinite(gapPosition) ? gapPosition : null;
            }

            float segLeft = inkLeft + (inkRight - inkLeft) * (float)piece.WeightStart;
            float segRight = inkLeft + (inkRight - inkLeft) * (float)piece.WeightEnd;
            if (segLeft == segRight || piece.SegmentTime.EndUs <= piece.SegmentTime.StartUs) return null;
            double ratio = (double)(timeUs - piece.SegmentTime.StartUs) / (piece.SegmentTime.EndUs - piece.SegmentTime.StartUs);
            float position = segLeft + (segRight - segLeft) * (float)ratio;
            return float.IsFinite(position) ? position : null;
        }

        return SungPosition(token.Time, token.Progress, timeUs, inkLeft, inkRight, width);
    }

    /// <summary>
    /// 不夹值的演唱位置；没有可用时间、带宽为 0、零宽、零长或按时间算出的进度与采样进度对不上时为空（照硬切换）。
    /// </summary>
    /// <remarks>时间段之内与 <see cref="Scripting.TextProgressPath.XAt"/> 是同一单精度插值。</remarks>
    private static float? SungPosition(Core.Time.TimeRange? time, double progress, long timeUs, float left, float right, float width)
    {
        if (time is not { } range || !(width > 0f) || !float.IsFinite(width) || left == right || range.EndUs <= range.StartUs) return null;
        double ratio = (double)(timeUs - range.StartUs) / (range.EndUs - range.StartUs);
        if (Math.Clamp(ratio, 0d, 1d) != progress) return null;
        float position = left + (right - left) * (float)ratio;
        return float.IsFinite(position) ? position : null;
    }

    /// <summary>一个区间 [left, right]（方向由两端决定）按演唱位置分出已唱硬区与过渡带。</summary>
    private static void AddRamp(WipePartition wipe, SKRect bounds, float left, float right, float position, float width)
    {
        float direction = right > left ? 1f : -1f;
        float full = position - direction * width / 2f, empty = position + direction * width / 2f;
        float low = Math.Min(left, right), high = Math.Max(left, right);
        // m = 1 的一侧：从左向右时在 full 左边，从右向左时在 full 右边。
        float sungLow = direction > 0 ? low : Math.Max(low, full), sungHigh = direction > 0 ? Math.Min(high, full) : high;
        if (sungHigh > sungLow) wipe.Sung.AddRect(new SKRect(sungLow, bounds.Top, sungHigh, bounds.Bottom));
        float bandLow = Math.Max(low, Math.Min(full, empty)), bandHigh = Math.Min(high, Math.Max(full, empty));
        if (bandHigh <= bandLow) return;
        wipe.Band.Add(new SKRect(bandLow, bounds.Top, bandHigh, bounds.Bottom));
        // 遮罩铺满整个区间：外扩进来的像素在区间内取夹值后的 0 或 1，与相邻硬区连续。
        wipe.Pieces.Add(new(new SKRect(low, bounds.Top, high, bounds.Bottom), full, empty));
    }

    /// <summary>行两端之外的延伸部分取常数 m：为 1 归入已唱硬区，为 0 归入未唱，介于两者之间归入过渡带。</summary>
    private static void AddConstant(WipePartition wipe, SKRect rect, float mask)
    {
        if (!(rect.Right > rect.Left) || mask <= 0f) return;
        if (mask >= 1f) { wipe.Sung.AddRect(rect); return; }
        wipe.Band.Add(rect);
        wipe.Pieces.Add(new(rect, 0f, 0f, mask));
    }

    /// <summary>已唱区域是否按扫字线延到行两端之外（<see cref="BuildWipe"/> 的 lineEdges）。</summary>
    /// <remarks>
    /// 两态装饰不同时延伸，字框外的装饰随之切换（P3 设计 §4）。描边部件按两态拆侧时也延伸：
    /// 两侧的材质不同而绘制描述里的种子描边相同，不延伸时字框外的描边总归未唱一侧。
    /// 拆侧填充照旧只看装饰，保持已有填充材质的输出。
    /// </remarks>
    private static bool WipeEdges(LyricStyle style, CapturePaint? capture) => style.DecorationsDifferByState
        || capture is { Part: SceneColorPart.BaseStroke or SceneColorPart.RubyStroke } && (capture.RequestedSungPass is not null || capture.SungOnly);

    /// <summary>
    /// 两态是否画得不同：不同才需要过渡带合成，相同时硬切换的两块画出来与合成一样（P3 设计 §10.1）。
    /// </summary>
    /// <remarks>
    /// 拆侧部件的两侧总按不同处理：两侧一边是材质、一边是原样或另一材质，外观相同也不能只按已唱硬区切开，两侧都要画到过渡带。
    /// 只取已唱覆盖时两遍总是不同（带内为字形覆盖 × m）；其他覆盖两遍都画白色，只有两态装饰的形状不同时才不同。
    /// </remarks>
    private static bool WipeStatesDiffer(LyricStyle style, CapturePaint? capture) => capture switch
    {
        { RequestedSungPass: not null } => true,
        { SungOnly: true } => true,
        { IsCoverage: true } => style.DecorationsDifferByState,
        _ => style.Appearance(false) != style.Appearance(true),
    };

    /// <summary>
    /// 按扫字分区画两态：硬区照旧互补裁剪各画一态；过渡带内两态各画进一张层，未唱 ×(1 − m)、已唱 × m 相加后普通叠加到画布（P3 设计 §10.1）。
    /// </summary>
    /// <param name="canvas">目标画布。</param>
    /// <param name="wipe">扫字分区。</param>
    /// <param name="statesDiffer">两态是否画得不同；相同时只按硬区分两块。只在有过渡带时求值。</param>
    /// <param name="capture">显式捕获通道。</param>
    /// <param name="pass">画一态（参数为是否已唱）；调用前已裁好本块区域。</param>
    /// <remarks>
    /// 没有过渡带时与羽化之前完全相同。有过渡带时三块区域都换成整数设备像素区域（CPU 与 Vulkan 同一做法）：
    /// 过渡带沿横向各外扩 1 个设备像素后从两块硬区里扣掉，外扩进来的像素 m 正好是 0 或 1，三块互补，没有缝也没有重叠。
    /// 拆侧部件（<see cref="CapturePaint.RequestedSungPass"/>）只画本侧：本侧硬区 ∪ 过渡带，带内按全覆盖处理（P3 设计 §10.2 第 1 条）。
    /// </remarks>
    private void DrawWiped(SKCanvas canvas, WipePartition wipe, Func<bool> statesDiffer, CapturePaint? capture, Action<bool> pass)
    {
        if (wipe.Band.Count == 0 || !ForceWipeBand && !statesDiffer())
        {
            bool hasSung = !wipe.Sung.IsEmpty;
            Hard(false);
            if (hasSung) Hard(true);
            void Hard(bool sung)
            {
                canvas.Save();
                if (hasSung) ClipSungRegions(canvas, wipe.Sung, sung);
                pass(sung);
                canvas.Restore();
            }
            return;
        }

        SKMatrix matrix = canvas.TotalMatrix;
        using var clip = new SKRegion(canvas.DeviceClipBounds);
        using SKRegion sungPixels = DevicePixels(wipe.Sung);
        using var expanded = new SKPath();
        float margin = 1f / MathF.Sqrt(matrix.ScaleX * matrix.ScaleX + matrix.SkewY * matrix.SkewY);
        if (!float.IsFinite(margin)) margin = 0f;
        foreach (SKRect rect in wipe.Band) expanded.AddRect(new SKRect(rect.Left - margin, rect.Top, rect.Right + margin, rect.Bottom));
        using SKRegion band = DevicePixels(expanded);
        using var covered = new SKRegion(sungPixels);
        covered.Op(band, SKRegionOperation.Union);
        using var hard = new SKRegion(sungPixels);
        hard.Op(band, SKRegionOperation.Difference);

        if (capture?.RequestedSungPass is { } side)
        {
            if (capture.WipeBand is { } probe) probe.Seen = true;
            canvas.Save();
            if (side) canvas.ClipRegion(covered);
            else canvas.ClipRegion(hard, SKClipOperation.Difference);
            pass(side);
            canvas.Restore();
            return;
        }

        canvas.Save();
        canvas.ClipRegion(covered, SKClipOperation.Difference);
        pass(false);
        canvas.Restore();
        if (!hard.IsEmpty)
        {
            canvas.Save();
            canvas.ClipRegion(hard);
            pass(true);
            canvas.Restore();
        }

        canvas.Save();
        canvas.ClipRegion(band);
        using (capture?.Layer("CaptureWipeBandLayer"))
        {
            int layer = canvas.SaveLayer();
            pass(false);
            Mask(SKBlendMode.DstOut);
            using (capture?.Layer("CaptureWipeSungLayer"))
            using (var plus = new SKPaint { BlendMode = SKBlendMode.Plus })
            {
                int sungLayer = canvas.SaveLayer(plus);
                pass(true);
                Mask(SKBlendMode.DstIn);
                canvas.RestoreToCount(sungLayer);
            }
            canvas.RestoreToCount(layer);
        }
        canvas.Restore();

        SKRegion DevicePixels(SKPath path)
        {
            using var device = new SKPath(path);
            device.Transform(matrix);
            var pixels = new SKRegion();
            pixels.SetPath(device, clip);
            return pixels;
        }

        // 遮罩画在单独一层里再按混合方式合成，没画到的像素就是 m = 0：已唱用 DstIn 只留 × m，未唱用 DstOut 只留 ×(1 − m)。
        void Mask(SKBlendMode blendMode)
        {
            using var reservation = capture?.Layer("CaptureWipeMaskLayer");
            using var layerPaint = new SKPaint { BlendMode = blendMode };
            int maskLayer = canvas.SaveLayer(layerPaint);
            using (var solid = new SKPaint { Color = SKColors.White, IsAntialias = false })
                canvas.DrawPath(wipe.Sung, solid);
            foreach (WipePiece piece in wipe.Pieces)
            {
                using var paint = new SKPaint { BlendMode = SKBlendMode.Src, IsAntialias = false };
                using SKShader? ramp = piece.Constant is null
                    ? SKShader.CreateLinearGradient(new SKPoint(piece.Full, 0f), new SKPoint(piece.Empty, 0f),
                        [SKColors.White, SKColors.White.WithAlpha(0)], SKShaderTileMode.Clamp)
                    : null;
                if (ramp is not null) paint.Shader = ramp;
                else paint.Color = SKColors.White.WithAlpha((byte)MathF.Round(piece.Constant!.Value * 255f));
                canvas.DrawRect(piece.Rect, paint);
            }
            canvas.RestoreToCount(maskLayer);
        }
    }
}
