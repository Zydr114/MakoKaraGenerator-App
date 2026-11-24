using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Text;
using SkiaSharp;

namespace MakoKaraGenerator.Rendering.Skia;

// 渐变着色（P3 设计 §5、§7.2）：参考框、硬边取样与按单字着色器。
public sealed partial class SkiaRenderPipeline
{
    /// <summary>采样渐变时的均匀取样点数；没有硬边的渐变照旧按这个数取样。</summary>
    private const int GradientSampleCount = 65;

    /// <summary>整行坐标的横向范围；注音取所属底字整行的范围（P3 设计 §7.2），其余为自身整形宽度。</summary>
    /// <param name="Left">左端横坐标。</param>
    /// <param name="Width">宽度。</param>
    private readonly record struct LineSpan(float Left, float Width);

    /// <summary>
    /// 一次绘制的渐变着色：画布、整行坐标共用一个着色器；按单字坐标时每个绘制单元按自己的字框各建一个，随本对象释放。
    /// </summary>
    private sealed class TextShading : IDisposable
    {
        private readonly Func<ShapedDrawUnit, SKShader?>? _create;
        private Dictionary<ShapedDrawUnit, SKShader?>? _units;

        private TextShading(SKShader? shared, Func<ShapedDrawUnit, SKShader?>? create)
        {
            Shared = shared;
            _create = create;
        }

        /// <summary>无渐变或渐变组合不受支持。</summary>
        public static TextShading None { get; } = new(null, null);

        /// <summary>整段共用的着色器；按单字时为 null。</summary>
        public SKShader? Shared { get; }

        /// <summary>没有着色器。</summary>
        public bool IsEmpty => Shared is null && _create is null;

        /// <summary>按绘制单元取着色器；不是按单字时为 null。</summary>
        public Func<ShapedDrawUnit, SKShader?>? UnitShader => _create is null ? null : For;

        public static TextShading Of(SKShader? shared) => shared is null ? None : new(shared, null);

        public static TextShading PerUnit(Func<ShapedDrawUnit, SKShader?> create) => new(null, create);

        private SKShader? For(ShapedDrawUnit unit)
        {
            _units ??= new(ReferenceEqualityComparer.Instance);
            if (!_units.TryGetValue(unit, out var shader)) _units[unit] = shader = _create!(unit);
            return shader;
        }

        public void Dispose()
        {
            if (ReferenceEquals(this, None)) return;
            Shared?.Dispose();
            if (_units is null) return;
            foreach (var shader in _units.Values) shader?.Dispose();
            _units = null;
        }
    }

    /// <summary>把核心渐变描述做成本次绘制的着色。</summary>
    /// <remarks>
    /// <para>
    /// Skia 的基础线性渐变不能表达 HSV 色相路径和线性 RGB 插值，因此先用核心的
    /// <see cref="GradientSampler"/> 采样，再把采样点交给 Skia。这保证预览与导出共享同一套
    /// 渐变取色规则；采样数足够高时，Skia 只在相邻采样点之间做很小的屏幕空间插值。
    /// </para>
    /// <para>
    /// 参考框：画布为整个画布；整行横向为 <paramref name="span"/>（缺省为本行的整形范围），纵向为本行字体上升部到下降部；
    /// 按单字为每个绘制单元的字框（字簇宽度 × 字体上升部到下降部）。
    /// </para>
    /// </remarks>
    private static TextShading CreateTextShading(GradientSpec? gradient, ShapedLine shaped, float originX, float baselineY,
        LyricStyle style, Composition composition, SKMatrix device, LineSpan? span = null, float? opacityOverride = null)
    {
        if (gradient is null || !GradientSupport.IsSupported(gradient)) return TextShading.None;
        float opacity = opacityOverride ?? style.Opacity;
        if (gradient.CoordinateSpace == GradientCoordinateSpace.Glyph)
        {
            var (ascent, descent) = LineMetrics(shaped, style);
            return TextShading.PerUnit(unit => CreateGradientShader(gradient, GlyphBox(unit, originX, baselineY, ascent, descent), opacity, device));
        }
        return TextShading.Of(CreateGradientShader(gradient, ReferenceBox(gradient, shaped, originX, baselineY, style, composition, span), opacity, device));
    }

    /// <summary>画布或整行坐标的参考框（左、上、宽、高）；宽高至少 1。</summary>
    private static (float Left, float Top, float Width, float Height) ReferenceBox(GradientSpec gradient, ShapedLine shaped,
        float originX, float baselineY, LyricStyle style, Composition composition, LineSpan? span)
    {
        if (gradient.CoordinateSpace == GradientCoordinateSpace.Canvas)
            return (0f, 0f, Math.Max(1f, composition.CanvasWidth), Math.Max(1f, composition.CanvasHeight));
        var (ascent, descent) = LineMetrics(shaped, style);
        return (span?.Left ?? originX, baselineY + ascent, Math.Max(1f, span?.Width ?? shaped.Width), Math.Max(1f, descent - ascent));
    }

    /// <summary>单个绘制单元的字框；单元没有字体度量时用整行的。</summary>
    private static (float Left, float Top, float Width, float Height) GlyphBox(ShapedDrawUnit unit, float originX, float baselineY,
        float lineAscent, float lineDescent)
    {
        bool own = unit.Descent > unit.Ascent;
        float ascent = own ? unit.Ascent : lineAscent, descent = own ? unit.Descent : lineDescent;
        return (originX + unit.X, baselineY + ascent, Math.Max(1f, unit.Advance), Math.Max(1f, descent - ascent));
    }

    /// <summary>整行的字体上升部与下降部（相对基线，上升部为负）：各绘制单元所用字体度量的并（D-P3-8）。</summary>
    /// <remarks>没有绘制单元或度量时退回旧的字号估算（上升 0.85、下降 0.15 倍字号）。</remarks>
    private static (float Ascent, float Descent) LineMetrics(ShapedLine shaped, LyricStyle style)
    {
        float ascent = 0f, descent = 0f;
        foreach (var unit in shaped.DrawUnits)
        {
            ascent = Math.Min(ascent, unit.Ascent);
            descent = Math.Max(descent, unit.Descent);
        }
        return descent > ascent ? (ascent, descent) : (-style.FontSizePx * 0.85f, style.FontSizePx * 0.15f);
    }

    private static SKShader? CreateGradientShader(GradientSpec gradient, (float Left, float Top, float Width, float Height) box,
        float opacity, SKMatrix device)
    {
        var (rectLeft, rectTop, width, height) = box;
        float centerX = rectLeft + (width * 0.5f);
        float centerY = rectTop + (height * 0.5f);
        float radians = (float)(gradient.AngleDegrees * (Math.PI / 180d));
        float directionX = MathF.Cos(radians);
        float directionY = MathF.Sin(radians);
        // 取矩形在该方向上的投影半长，使 0°/90° 时停止点分别贴住行的左右/上下边界，
        // 斜向时也刚好覆盖整行，而不是因使用对角线长度而把端点推到可见区域之外。
        float halfLength = (MathF.Abs(directionX) * width + MathF.Abs(directionY) * height) * 0.5f + 1f;
        float radiusX = Math.Max(1e-4f, (float)(gradient.RadiusX * width));
        float radiusY = Math.Max(1e-4f, (float)(gradient.RadiusY * height));

        SKColor[] colors;
        float[] positions;
        try
        {
            if (GradientSampler.HasHardEdges(gradient))
            {
                // 硬边的过渡宽约 1 设备像素（P3 设计 §5.1）；0°／90° 的线性渐变边缘与像素对齐，保持清晰的硬边。
                double softness = gradient.Kind == GradientKind.Radial
                    ? 0.5d / Math.Max(1e-4d, Math.Min(DeviceLength(device, radiusX, 0f), DeviceLength(device, 0f, radiusY)))
                    : gradient.AngleDegrees % 90d == 0d ? 0d
                    : 0.5d / Math.Max(1e-4d, DeviceLength(device, directionX * halfLength * 2f, directionY * halfLength * 2f));
                (colors, positions) = HardEdgeStops(gradient, opacity, softness);
            }
            else (colors, positions) = UniformStops(gradient, opacity);
        }
        catch (NotSupportedException)
        {
            return null;
        }

        if (gradient.Kind == GradientKind.Radial)
        {
            centerX = rectLeft + ((float)gradient.CenterX * width);
            centerY = rectTop + ((float)gradient.CenterY * height);
            // 局部矩阵把着色器空间的单位圆放大成参考框里的椭圆（半径为参考框宽高乘半径比例）；
            // 0.112.76 之前这里取了倒数，椭圆只有约 1/半径 像素，整片几乎都是末端色。
            SKMatrix matrix = SKMatrix.CreateScale(radiusX, radiusY, centerX, centerY);
            return SKShader.CreateRadialGradient(new SKPoint(centerX, centerY), 1f, colors, positions, SKShaderTileMode.Clamp, matrix);
        }

        SKPoint direction = new(directionX * halfLength, directionY * halfLength);
        return SKShader.CreateLinearGradient(
            new SKPoint(centerX - direction.X, centerY - direction.Y),
            new SKPoint(centerX + direction.X, centerY + direction.Y),
            colors,
            positions,
            SKShaderTileMode.Clamp);

        static double DeviceLength(SKMatrix matrix, float x, float y)
        {
            var mapped = matrix.MapVector(x, y);
            return Math.Sqrt((double)mapped.X * mapped.X + (double)mapped.Y * mapped.Y);
        }
    }

    /// <summary>没有硬边的渐变：沿用均匀取样，像素与 P3 之前相同。</summary>
    private static (SKColor[] Colors, float[] Positions) UniformStops(GradientSpec gradient, float opacity)
    {
        SKColor[] colors = new SKColor[GradientSampleCount];
        float[] positions = new float[GradientSampleCount];
        for (int index = 0; index < GradientSampleCount; index++)
        {
            float position = index / (float)(GradientSampleCount - 1);
            RgbaColor color = gradient.Kind == GradientKind.Linear
                ? GradientSampler.Sample(gradient, position)
                : GradientSampler.SampleAt(gradient, gradient.CenterX + (position * gradient.RadiusX), gradient.CenterY);
            colors[index] = ToSkColorWithOpacity(color, opacity);
            positions[index] = position;
        }
        return (colors, positions);
    }

    /// <summary>
    /// 含硬边的渐变：按色标分段取样，每段按长度取点（同色段只取两端）；重合色标处向 Skia 提交位置相同的两个点（P3 设计 §5.1）。
    /// <paramref name="softness"/> 大于 0 时，硬边两侧的点各向外移开这么多（归一化位置），形成约 1 设备像素的过渡。
    /// </summary>
    internal static (SKColor[] Colors, float[] Positions) HardEdgeStops(GradientSpec gradient, float opacity, double softness)
    {
        var stops = gradient.Stops;
        List<(double Position, RgbaColor Color)> points = [(0d, stops[0].Color)];
        for (int index = 0; index + 1 < stops.Length; index++)
        {
            double left = stops[index].Offset, right = stops[index + 1].Offset;
            if (right <= left) continue;
            int count = stops[index].Color == stops[index + 1].Color ? 1 : Math.Max(1, (int)Math.Ceiling((right - left) * (GradientSampleCount - 1)));
            for (int step = 0; step <= count; step++)
            {
                double amount = step / (double)count;
                Add(left + (right - left) * amount, GradientSampler.SampleBetween(gradient, index, amount));
            }
        }
        Add(1d, stops[^1].Color);

        if (softness > 0d)
        {
            for (int index = 1; index < points.Count; index++)
            {
                if (points[index].Position != points[index - 1].Position) continue;
                double edge = points[index].Position;
                points[index - 1] = (Math.Max(0d, edge - softness), points[index - 1].Color);
                points[index] = (Math.Min(1d, edge + softness), points[index].Color);
            }
            // 移开后越过相邻取样点时向后收拢，保持位置不递减（Skia 的要求）。
            for (int index = 1; index < points.Count; index++)
                if (points[index].Position < points[index - 1].Position) points[index] = (points[index - 1].Position, points[index].Color);
        }

        var colors = new SKColor[points.Count];
        var positions = new float[points.Count];
        for (int index = 0; index < points.Count; index++)
        {
            colors[index] = ToSkColorWithOpacity(points[index].Color, opacity);
            positions[index] = (float)points[index].Position;
        }
        return (colors, positions);

        void Add(double position, RgbaColor color)
        {
            if (points[^1].Position == position && points[^1].Color == color) return;
            points.Add((position, color));
        }
    }

    /// <summary>径向渐变的实际中心与半径须是有限 float 且半径不过小；超出时明确拒绝，不夹值。</summary>
    private static void ValidateNativeGradient(GradientSpec gradient, ShapedLine shaped, LyricStyle style, Composition composition, LineSpan? span = null)
    {
        if (!float.IsFinite((float)(gradient.AngleDegrees * Math.PI / 180)))
            throw new PreparedFrameException("UnsupportedNativeStyleValues", "渐变角度超出后端有限精度范围。");
        if (gradient.Kind != GradientKind.Radial) return;
        if (gradient.CoordinateSpace != GradientCoordinateSpace.Glyph)
        {
            var box = ReferenceBox(gradient, shaped, 0f, 0f, style, composition, span);
            Check(box.Width, box.Height);
            return;
        }
        var (ascent, descent) = LineMetrics(shaped, style);
        foreach (var unit in shaped.DrawUnits)
        {
            var box = GlyphBox(unit, 0f, 0f, ascent, descent);
            Check(box.Width, box.Height);
        }

        void Check(double width, double height)
        {
            double rx = gradient.RadiusX * width, ry = gradient.RadiusY * height;
            if (!float.IsFinite((float)rx) || !float.IsFinite((float)ry) || rx < 1e-4 || ry < 1e-4
                || !float.IsFinite((float)(gradient.CenterX * width)) || !float.IsFinite((float)(gradient.CenterY * height)))
                throw new PreparedFrameException("UnsupportedNativeStyleValues", "渐变实际径向中心或半径超出后端范围，不能夹值绘制。");
        }
    }
}
