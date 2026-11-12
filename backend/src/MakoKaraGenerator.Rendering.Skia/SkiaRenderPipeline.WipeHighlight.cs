using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Text;
using SkiaSharp;
using static MakoKaraGenerator.Rendering.Skia.SkiaSceneGeometry;

namespace MakoKaraGenerator.Rendering.Skia;

public sealed partial class SkiaRenderPipeline
{
    /// <summary>扫字线高亮的一个峰（P3 设计 §10.3）：中心相对绘制原点的横向偏移、半宽与所属元素的高亮参数。</summary>
    private readonly record struct HighlightPeak(float Offset, float HalfWidth, WipeHighlightSpec Spec);

    // 正文与注音当前要叠的高亮峰。DrawSceneLine 为整行设置正文的峰、DrawRuby 为每段注音设置注音的峰，结束时恢复；
    // DrawStyledLine 按是否注音取用。渲染与捕获都在设备锁内串行，字段不会被并发改写。
    private IReadOnlyList<HighlightPeak> _baseHighlight = [];
    private IReadOnlyList<HighlightPeak> _rubyHighlight = [];

    // 高亮另画一遍：峰着色器只输出强度 h（放在 α），混合器按未预乘颜色把目标与高亮色混合并保持目标 α，
    // 0 普通、1 滤色、2 相加（与 WipeHighlightBlend 取值相同）。不改原有绘制，峰外像素不受影响。
    private const string HighlightPeakSource = """
        uniform float3 peak;
        half4 main(float2 p) {
            float d = abs(p.x - peak.x);
            float h = d >= peak.y ? 0.0 : peak.z * 0.5 * (1.0 + cos(3.14159265358979 * d / peak.y));
            return half4(0.0, 0.0, 0.0, half(h));
        }
        """;

    private const string HighlightBlendSource = """
        uniform float3 tint;
        uniform int mode;
        half4 main(half4 src, half4 dst) {
            float h = float(src.a);
            if (h <= 0.0 || dst.a <= 0.0) return dst;
            float3 c = clamp(float3(dst.rgb) / float(dst.a), 0.0, 1.0);
            float3 r = mode == 0 ? mix(c, tint, h)
                : mode == 1 ? 1.0 - (1.0 - c) * (1.0 - tint * h)
                : min(c + tint * h, 1.0);
            return half4(half3(r * float(dst.a)), dst.a);
        }
        """;

    private static readonly Lazy<SKRuntimeEffect> HighlightPeakEffect = new(() =>
        SKRuntimeEffect.CreateShader(HighlightPeakSource, out string error)
            ?? throw new PreparedFrameException("WipeHighlightShaderUnavailable", "扫字线高亮着色器无法编译：" + error));

    private static readonly Lazy<SKRuntimeEffect> HighlightBlendEffect = new(() =>
        SKRuntimeEffect.CreateBlender(HighlightBlendSource, out string error)
            ?? throw new PreparedFrameException("WipeHighlightShaderUnavailable", "扫字线高亮混合器无法编译：" + error));

    /// <summary>元素解析后的扫字样式：元素覆盖写了就叠到行样式上。</summary>
    private static WipeSpec ElementWipe(LyricStyle lineStyle, LyricLine line, TokenSample token) =>
        token.Index < line.Tokens.Length && line.Tokens[token.Index].StyleOverride?.Wipe is { } overlay
            ? overlay.Apply(lineStyle.Wipe) : lineStyle.Wipe;

    /// <summary>本装载里有脚本样式输出的元素准备后的扫字样式，按源元素 ID；底字输出优先于元素族。没有输出时为 null。</summary>
    private static Dictionary<string, WipeSpec>? PreparedWipes(PreparedRenderFrame? prepared, string? binding)
    {
        if (prepared is null || binding is null || prepared.TokenStyles.IsEmpty && prepared.BaseStyles.IsEmpty) return null;
        Dictionary<string, WipeSpec>? wipes = null;
        foreach (var output in prepared.TokenStyles)
            if (output.Target.BindingId.Value == binding) (wipes ??= new(StringComparer.Ordinal))[output.Target.TokenId.Value] = prepared.GetTokenDrawingStyle(output.Target).Wipe;
        foreach (var output in prepared.BaseStyles)
            if (output.Target.BindingId.Value == binding) (wipes ??= new(StringComparer.Ordinal))[output.Target.TokenId.Value] = prepared.GetBaseDrawingStyle(output.Target).Wipe;
        return wipes;
    }

    /// <summary>
    /// 整行正文的高亮峰，偏移相对整行左端（P3 设计 §10.3）。
    /// </summary>
    /// <param name="sample">整行的时刻采样（不是单个元素的采样，峰可以延伸到相邻元素）。</param>
    /// <param name="shaped">整行布局。</param>
    /// <param name="wipe">元素解析后的扫字样式。</param>
    /// <param name="fontSizePx">行字号；峰宽 = 比例 × 字号。</param>
    /// <returns>有元素正在演唱时只有它的峰；没有时取按时间外推后仍压到自己区间的元素的峰（滑入、滑出）。</returns>
    private static IReadOnlyList<HighlightPeak> BuildHighlights(LyricFrameSample sample, ShapedLine shaped,
        Func<TokenSample, WipeSpec> wipe, float fontSizePx)
    {
        if (!sample.HasTokens || !sample.TokensMatchLineText) return Array.Empty<HighlightPeak>();
        List<HighlightPeak>? current = null, moving = null;
        foreach (TokenSample token in sample.Tokens)
        {
            if (token.Source != ProgressSource.FromToken || wipe(token).Highlight is not { } spec) continue;
            AddPeak(token.Time, token.Progress, sample.TimeUs, shaped.XAt(token.Utf16Start), shaped.XAt(token.Utf16End),
                spec, fontSizePx, ref current, ref moving);
        }
        return current ?? moving ?? (IReadOnlyList<HighlightPeak>)Array.Empty<HighlightPeak>();
    }

    /// <summary>一段注音的高亮峰，偏移相对注音左端；分段各按自己的进度，宽度按注音字号。</summary>
    private static IReadOnlyList<HighlightPeak> BuildRubyHighlights(RubyLayout entry, LyricFrameSample sample, WipeHighlightSpec? spec)
    {
        if (spec is null) return Array.Empty<HighlightPeak>();
        List<HighlightPeak>? current = null, moving = null;
        foreach (RubyLayoutSegment segment in entry.Segments)
        {
            if (segment.Right <= segment.Left) continue;
            RubyTextSegmentSample state = sample.RubySegments.FirstOrDefault(value => value.RubyId == entry.Id && value.SegmentId == segment.Id);
            if (state.Source == ProgressSource.Unavailable) continue;
            bool single = entry.Segments.Length == 1;
            AddPeak(state.Time, state.Progress, sample.TimeUs, single ? 0f : segment.Left, single ? entry.Shaped.Width : segment.Right,
                spec, entry.Options.FontSizePx, ref current, ref moving);
        }
        return current ?? moving ?? (IReadOnlyList<HighlightPeak>)Array.Empty<HighlightPeak>();
    }

    private static void AddPeak(Core.Time.TimeRange? time, double progress, long timeUs, float left, float right,
        WipeHighlightSpec spec, float fontSizePx, ref List<HighlightPeak>? current, ref List<HighlightPeak>? moving)
    {
        float width = (float)(spec.WidthRatio * fontSizePx);
        if (time is not { } range || SungPosition(time, progress, timeUs, left, right, width) is not { } position) return;
        float half = width / 2f;
        HighlightPeak peak = new(position, half, spec);
        if (timeUs >= range.StartUs && timeUs < range.EndUs) (current ??= []).Add(peak);
        else if (position > Math.Min(left, right) - half && position < Math.Max(left, right) + half) (moving ??= []).Add(peak);
    }

    /// <summary>本次绘制要叠的峰：只叠在填充与描边的颜色上，覆盖、材质基础外观、阴影与辉光部件不叠。</summary>
    private IReadOnlyList<HighlightPeak> HighlightPeaks(bool ruby, CapturePaint? capture) =>
        capture is { IsCoverage: true } or { MaterialFallback: not null } or { ShadowOnly: true } or { GlowOnly: true } ? []
            : ruby ? _rubyHighlight : _baseHighlight;

    /// <summary>
    /// 在已画好的一笔上叠高亮（P3 设计 §10.3）：每个峰裁到自己的横向范围，用峰着色器与混合器再画一遍同一形状。
    /// </summary>
    /// <param name="canvas">目标画布。</param>
    /// <param name="peaks">要叠的峰；为空时什么也不画。</param>
    /// <param name="originX">峰偏移的基准（绘制原点）。</param>
    /// <param name="draw">用给定着色器与混合器重画形状；描边层里直接画矩形即可（混合器保持目标 α）。</param>
    private static void ApplyHighlight(SKCanvas canvas, IReadOnlyList<HighlightPeak> peaks, float originX, Action<SKShader, SKBlender> draw)
    {
        if (peaks.Count == 0) return;
        SKRuntimeEffect peakEffect = HighlightPeakEffect.Value, blendEffect = HighlightBlendEffect.Value;
        SKRect bounds = canvas.LocalClipBounds;
        foreach (HighlightPeak peak in peaks)
        {
            float strength = peak.Spec.Opacity * peak.Spec.Color.Alpha / 255f;
            if (!(strength > 0f) || !(peak.HalfWidth > 0f)) continue;
            float center = originX + peak.Offset;
            using var peakUniforms = new SKRuntimeEffectUniforms(peakEffect);
            peakUniforms.Add("peak", new[] { center, peak.HalfWidth, strength });
            using var peakChildren = new SKRuntimeEffectChildren(peakEffect);
            using SKShader shader = peakEffect.ToShader(peakUniforms, peakChildren)
                ?? throw new PreparedFrameException("WipeHighlightShaderUnavailable", "无法创建扫字线高亮着色器。");
            using var blendUniforms = new SKRuntimeEffectUniforms(blendEffect);
            blendUniforms.Add("tint", new[] { peak.Spec.Color.Red / 255f, peak.Spec.Color.Green / 255f, peak.Spec.Color.Blue / 255f });
            blendUniforms.Add("mode", (int)peak.Spec.Blend);
            using var blendChildren = new SKRuntimeEffectChildren(blendEffect);
            using SKBlender blender = blendEffect.ToBlender(blendUniforms, blendChildren)
                ?? throw new PreparedFrameException("WipeHighlightShaderUnavailable", "无法创建扫字线高亮混合器。");
            canvas.Save();
            canvas.ClipRect(new SKRect(center - peak.HalfWidth, bounds.Top, center + peak.HalfWidth, bounds.Bottom));
            draw(shader, blender);
            canvas.Restore();
        }
    }
}
