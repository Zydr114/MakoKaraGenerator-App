namespace MakoKaraGenerator.Core.Projects;

/// <summary>对 <see cref="GradientSpec"/> 做纯数据采样的工具。</summary>
/// <remarks>
/// 采样器不依赖 Skia 或 Avalonia，渲染器可以把同一套结果用于预览和导出。
/// 输入位置是当前坐标空间中的归一化位置；线性渐变的角度投影由
/// <see cref="ProjectLinearPosition"/> 计算。
/// </remarks>
public static class GradientSampler
{
    /// <summary>按归一化位置读取渐变颜色。</summary>
    /// <param name="gradient">有效的渐变描述。</param>
    /// <param name="position">渐变位置；超出 0 到 1 时按边界停止点处理。</param>
    /// <returns>采样到的颜色。</returns>
    /// <exception cref="ArgumentException">渐变描述无效时抛出。</exception>
    /// <exception cref="ArgumentOutOfRangeException">位置不是有限数时抛出。</exception>
    /// <exception cref="NotSupportedException">当前采样器尚未实现径向或 Oklch 渐变时抛出。</exception>
    public static RgbaColor Sample(GradientSpec gradient, double position)
    {
        ArgumentNullException.ThrowIfNull(gradient);

        if (!gradient.IsValid)
        {
            throw new ArgumentException("不能采样无效的渐变描述。", nameof(gradient));
        }

        if (!double.IsFinite(position))
        {
            throw new ArgumentOutOfRangeException(nameof(position), position, "渐变位置必须是有限数。");
        }

        if (gradient.Kind != GradientKind.Linear)
            throw new NotSupportedException("径向渐变需要二维坐标，请使用 SampleAt。");

        if (gradient.ColorSpace == GradientColorSpace.Oklch)
        {
            throw new NotSupportedException("Oklch 渐变采样将在感知色空间接入阶段实现。");
        }

        return SampleStops(gradient, position);
    }

    /// <summary>按当前坐标空间中的归一化二维坐标读取渐变颜色。</summary>
    /// <param name="gradient">有效的渐变描述。</param>
    /// <param name="x">横向归一化坐标。</param>
    /// <param name="y">纵向归一化坐标。</param>
    /// <returns>采样到的颜色。</returns>
    public static RgbaColor SampleAt(GradientSpec gradient, double x, double y)
    {
        ArgumentNullException.ThrowIfNull(gradient);
        if (!gradient.IsValid)
            throw new ArgumentException("不能采样无效的渐变描述。", nameof(gradient));
        if (!double.IsFinite(x) || !double.IsFinite(y))
            throw new ArgumentOutOfRangeException(nameof(x), "渐变坐标必须是有限数。");
        if (gradient.ColorSpace == GradientColorSpace.Oklch)
            throw new NotSupportedException("Oklch 渐变采样将在感知色空间接入阶段实现。");

        double position = gradient.Kind == GradientKind.Radial
            ? ProjectRadialPosition(gradient, x, y)
            : ProjectLinearPosition(gradient, x, y);
        return SampleStops(gradient, position);
    }

    /// <summary>渐变是否含重合色标（硬边，P3 设计 §5.1）。</summary>
    /// <param name="gradient">有效的渐变描述。</param>
    /// <returns>任意相邻两个色标位置相同且颜色不同时为 <see langword="true"/>。</returns>
    public static bool HasHardEdges(GradientSpec gradient)
    {
        ArgumentNullException.ThrowIfNull(gradient);
        System.Collections.Immutable.ImmutableArray<GradientStop> stops = gradient.Stops;
        for (int index = 1; index < stops.Length; index++)
        {
            if (stops[index].Offset == stops[index - 1].Offset && stops[index].Color != stops[index - 1].Color) return true;
        }

        return false;
    }

    /// <summary>在相邻两个色标之间按渐变的插值空间取色。</summary>
    /// <param name="gradient">有效的渐变描述。</param>
    /// <param name="leftIndex">左侧色标的下标；右侧是下一个色标。</param>
    /// <param name="amount">两色标之间的比例，0 为左侧颜色，1 为右侧颜色。</param>
    /// <returns>插值颜色。</returns>
    /// <exception cref="ArgumentOutOfRangeException">下标不在相邻色标对的范围内，或比例不是 0 到 1 的有限数。</exception>
    /// <exception cref="NotSupportedException">Oklch 插值尚未实现。</exception>
    public static RgbaColor SampleBetween(GradientSpec gradient, int leftIndex, double amount)
    {
        ArgumentNullException.ThrowIfNull(gradient);
        if (leftIndex < 0 || leftIndex + 1 >= gradient.Stops.Length)
            throw new ArgumentOutOfRangeException(nameof(leftIndex), leftIndex, "须指向一对相邻色标。");
        if (!double.IsFinite(amount) || amount is < 0d or > 1d)
            throw new ArgumentOutOfRangeException(nameof(amount), amount, "比例必须是 0 到 1 的有限数。");
        if (gradient.ColorSpace == GradientColorSpace.Oklch)
            throw new NotSupportedException("Oklch 渐变采样将在感知色空间接入阶段实现。");
        GradientStop left = gradient.Stops[leftIndex], right = gradient.Stops[leftIndex + 1];
        return gradient.ColorSpace == GradientColorSpace.Hsv
            ? InterpolateHsv(left.Color, right.Color, amount, gradient.HuePath)
            : InterpolateLinearRgb(left.Color, right.Color, amount);
    }

    private static RgbaColor SampleStops(GradientSpec gradient, double position)
    {
        double normalized = Math.Clamp(position, 0d, 1d);
        System.Collections.Immutable.ImmutableArray<GradientStop> stops = gradient.Stops;

        if (normalized <= stops[0].Offset)
        {
            return stops[0].Color;
        }

        if (normalized >= stops[^1].Offset)
        {
            return stops[^1].Color;
        }

        for (int index = 1; index < stops.Length; index++)
        {
            GradientStop left = stops[index - 1];
            GradientStop right = stops[index];

            if (normalized > right.Offset)
            {
                continue;
            }

            double span = right.Offset - left.Offset;
            double amount = span <= 0d ? 0d : (normalized - left.Offset) / span;

            return gradient.ColorSpace == GradientColorSpace.Hsv
                ? InterpolateHsv(left.Color, right.Color, amount, gradient.HuePath)
                : InterpolateLinearRgb(left.Color, right.Color, amount);
        }

        return stops[^1].Color;
    }

    /// <summary>把归一化二维坐标转换成径向渐变距离。</summary>
    /// <param name="gradient">有效的径向渐变描述。</param>
    /// <param name="x">横向归一化坐标。</param>
    /// <param name="y">纵向归一化坐标。</param>
    /// <returns>径向距离；半径上的位置为 1。</returns>
    public static double ProjectRadialPosition(GradientSpec gradient, double x, double y)
    {
        ArgumentNullException.ThrowIfNull(gradient);
        if (!gradient.IsValid || gradient.Kind != GradientKind.Radial)
            throw new ArgumentException("只能投影有效的径向渐变。", nameof(gradient));
        if (!double.IsFinite(x) || !double.IsFinite(y))
            throw new ArgumentOutOfRangeException(nameof(x), "渐变坐标必须是有限数。");

        double dx = (x - gradient.CenterX) / gradient.RadiusX;
        double dy = (y - gradient.CenterY) / gradient.RadiusY;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    /// <summary>把归一化二维坐标投影到线性渐变的方向轴。</summary>
    /// <param name="gradient">有效的线性渐变描述。</param>
    /// <param name="x">当前坐标的横向归一化位置。</param>
    /// <param name="y">当前坐标的纵向归一化位置。</param>
    /// <returns>投影后的渐变位置；调用 <see cref="Sample"/> 时会按边界停止点处理。</returns>
    /// <exception cref="ArgumentException">渐变不是有效的线性渐变时抛出。</exception>
    /// <exception cref="ArgumentOutOfRangeException">坐标不是有限数时抛出。</exception>
    public static double ProjectLinearPosition(GradientSpec gradient, double x, double y)
    {
        ArgumentNullException.ThrowIfNull(gradient);

        if (!gradient.IsValid || gradient.Kind != GradientKind.Linear)
        {
            throw new ArgumentException("只能投影有效的线性渐变。", nameof(gradient));
        }

        if (!double.IsFinite(x) || !double.IsFinite(y))
        {
            throw new ArgumentOutOfRangeException(nameof(x), "渐变坐标必须是有限数。");
        }

        double angle = gradient.AngleDegrees * Math.PI / 180d;
        double directionX = Math.Cos(angle);
        double directionY = Math.Sin(angle);

        return 0.5d + ((x - 0.5d) * directionX) + ((y - 0.5d) * directionY);
    }

    private static RgbaColor InterpolateLinearRgb(RgbaColor left, RgbaColor right, double amount)
    {
        double red = Lerp(ToLinear(left.Red / 255d), ToLinear(right.Red / 255d), amount);
        double green = Lerp(ToLinear(left.Green / 255d), ToLinear(right.Green / 255d), amount);
        double blue = Lerp(ToLinear(left.Blue / 255d), ToLinear(right.Blue / 255d), amount);
        double alpha = Lerp(left.Alpha, right.Alpha, amount);

        return new RgbaColor(
            ToByte(FromLinear(red) * 255d),
            ToByte(FromLinear(green) * 255d),
            ToByte(FromLinear(blue) * 255d),
            ToByte(alpha));
    }

    private static RgbaColor InterpolateHsv(
        RgbaColor left,
        RgbaColor right,
        double amount,
        GradientHuePath path)
    {
        (double leftHue, double leftSaturation, double leftValue) = ToHsv(left);
        (double rightHue, double rightSaturation, double rightValue) = ToHsv(right);
        double hue = leftHue + HueDelta(leftHue, rightHue, path) * amount;

        return FromHsv(
            hue,
            Lerp(leftSaturation, rightSaturation, amount),
            Lerp(leftValue, rightValue, amount),
            ToByte(Lerp(left.Alpha, right.Alpha, amount)));
    }

    private static (double Hue, double Saturation, double Value) ToHsv(RgbaColor color)
    {
        double red = color.Red / 255d;
        double green = color.Green / 255d;
        double blue = color.Blue / 255d;
        double max = Math.Max(red, Math.Max(green, blue));
        double min = Math.Min(red, Math.Min(green, blue));
        double delta = max - min;

        double hue = delta <= 0d
            ? 0d
            : max == red
                ? 60d * (((green - blue) / delta) % 6d)
                : max == green
                    ? 60d * (((blue - red) / delta) + 2d)
                    : 60d * (((red - green) / delta) + 4d);

        if (hue < 0d)
        {
            hue += 360d;
        }

        return (hue, max <= 0d ? 0d : delta / max, max);
    }

    private static RgbaColor FromHsv(double hue, double saturation, double value, byte alpha)
    {
        double normalizedHue = ((hue % 360d) + 360d) % 360d;
        double chroma = value * saturation;
        double section = normalizedHue / 60d;
        double second = chroma * (1d - Math.Abs(section % 2d - 1d));
        double match = value - chroma;
        (double red, double green, double blue) = section switch
        {
            < 1d => (chroma, second, 0d),
            < 2d => (second, chroma, 0d),
            < 3d => (0d, chroma, second),
            < 4d => (0d, second, chroma),
            < 5d => (second, 0d, chroma),
            _ => (chroma, 0d, second),
        };

        return new RgbaColor(
            ToByte((red + match) * 255d),
            ToByte((green + match) * 255d),
            ToByte((blue + match) * 255d),
            alpha);
    }

    private static double HueDelta(double from, double to, GradientHuePath path)
    {
        double increasing = ((to - from) % 360d + 360d) % 360d;
        double decreasing = increasing - 360d;

        return path switch
        {
            GradientHuePath.Increasing => increasing,
            GradientHuePath.Decreasing => decreasing,
            GradientHuePath.Longer => Math.Abs(increasing) < 180d ? decreasing : increasing,
            _ => Math.Abs(increasing) <= 180d ? increasing : decreasing,
        };
    }

    private static double ToLinear(double value) => value <= 0.04045d
        ? value / 12.92d
        : Math.Pow((value + 0.055d) / 1.055d, 2.4d);

    private static double FromLinear(double value) => value <= 0.0031308d
        ? value * 12.92d
        : 1.055d * Math.Pow(value, 1d / 2.4d) - 0.055d;

    private static double Lerp(double left, double right, double amount) => left + ((right - left) * amount);

    private static byte ToByte(double value) => (byte)Math.Clamp(Math.Round(value), 0d, 255d);
}
