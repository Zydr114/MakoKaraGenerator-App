using System.Globalization;

namespace MakoKaraGenerator.Core.Projects;

/// <summary>
/// 与具体图形库无关的 RGBA 颜色。
/// </summary>
/// <remarks>
/// <para>
/// Core 层不得引用 Avalonia 或 SkiaSharp（docs/roadmap.md §3.2），
/// 因此画布背景色等颜色值在这里以四个字节表示，
/// 由渲染层在边界处转换为 <c>SKColor</c> 等具体类型。
/// </para>
/// <para>
/// 文本形式固定为 <c>"#RRGGBBAA"</c>（含 Alpha）。不省略 <c>FF</c> 是为了保证无损往返，
/// 避免"看起来一样的两个颜色在保存后不相等"。
/// </para>
/// </remarks>
/// <param name="Red">红色通道。</param>
/// <param name="Green">绿色通道。</param>
/// <param name="Blue">蓝色通道。</param>
/// <param name="Alpha">Alpha 通道，255 表示完全不透明。</param>
public readonly record struct RgbaColor(byte Red, byte Green, byte Blue, byte Alpha = 255)
{
    /// <summary>不透明黑色。</summary>
    public static RgbaColor Black { get; } = new(0, 0, 0);

    /// <summary>不透明白色。</summary>
    public static RgbaColor White { get; } = new(255, 255, 255);

    /// <summary>完全透明。</summary>
    public static RgbaColor Transparent { get; } = new(0, 0, 0, 0);

    /// <summary>解析 <c>"#RRGGBB"</c>、<c>"#RRGGBBAA"</c> 或省略 <c>#</c> 的等价写法。</summary>
    /// <param name="text">待解析的文本。</param>
    /// <returns>解析出的颜色。</returns>
    /// <exception cref="ArgumentException"><paramref name="text"/> 不是合法的十六进制颜色时抛出。</exception>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> 为 <see langword="null"/> 时抛出。</exception>
    public static RgbaColor Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        return TryParse(text, out RgbaColor color)
            ? color
            : throw new ArgumentException($"无法解析颜色 '{text}'，期望 '#RRGGBB' 或 '#RRGGBBAA'。", nameof(text));
    }

    /// <summary>尝试解析颜色文本，不抛异常。</summary>
    /// <param name="text">待解析的文本，可为 <see langword="null"/>。</param>
    /// <param name="color">成功时为解析出的颜色。</param>
    /// <returns>解析成功时为 <see langword="true"/>。</returns>
    public static bool TryParse(string? text, out RgbaColor color)
    {
        color = default;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        ReadOnlySpan<char> span = text.AsSpan().Trim();

        if (span.Length > 0 && span[0] == '#')
        {
            span = span[1..];
        }

        if (span.Length is not (6 or 8))
        {
            return false;
        }

        if (!TryParseByte(span[..2], out byte red)
            || !TryParseByte(span[2..4], out byte green)
            || !TryParseByte(span[4..6], out byte blue))
        {
            return false;
        }

        byte alpha = 255;

        if (span.Length == 8 && !TryParseByte(span[6..8], out alpha))
        {
            return false;
        }

        color = new RgbaColor(red, green, blue, alpha);
        return true;
    }

    /// <summary>转换为 <c>"#RRGGBBAA"</c> 文本。</summary>
    /// <returns>固定 9 个字符的颜色文本（含 <c>#</c>），使用不变文化。</returns>
    public string ToHex() => string.Create(
        CultureInfo.InvariantCulture,
        $"#{Red:X2}{Green:X2}{Blue:X2}{Alpha:X2}");

    /// <summary>以 <c>"#RRGGBBAA"</c> 形式返回。</summary>
    public override string ToString() => ToHex();

    private static bool TryParseByte(ReadOnlySpan<char> span, out byte value) =>
        byte.TryParse(span, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value);
}
