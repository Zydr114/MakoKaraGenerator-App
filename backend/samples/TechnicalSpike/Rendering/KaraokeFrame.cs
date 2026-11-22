using SkiaSharp;
using MakoKaraGenerator.Text;

namespace MakoKaraGenerator.Spike.Rendering;

/// <summary>
/// 一个逐字元素的文本区间与时间区间。
/// </summary>
/// <remarks>
/// 画布推进填充需要"这个元素覆盖行文本的哪一段、对应哪段时间"，
/// 保证画布与时间轴用的是同一份逐字信息。
/// </remarks>
/// <param name="Utf16Start">元素在行文本中的起始下标。</param>
/// <param name="Utf16Length">元素占用的 UTF-16 代码单元数量。</param>
/// <param name="StartUs">元素起始时刻（微秒）。</param>
/// <param name="EndUs">元素结束时刻（微秒）。</param>
public readonly record struct KaraokeTokenTiming(int Utf16Start, int Utf16Length, long StartUs, long EndUs)
{
    /// <summary>结束下标（不含）。</summary>
    public int Utf16End => Utf16Start + Utf16Length;

    /// <summary>时长（微秒）。</summary>
    public long DurationUs => EndUs - StartUs;
}

/// <summary>
/// 一个逐字元素在画布上的落位（相对整行左端的像素值）。
/// </summary>
/// <param name="Utf16Start">元素在行文本中的起始下标。</param>
/// <param name="Utf16Length">占用的 UTF-16 代码单元数量。</param>
/// <param name="X">相对整行左端的起始位置。</param>
/// <param name="Width">宽度。</param>
/// <param name="IsFallback">是否由回退字体绘制。</param>
public readonly record struct KaraokeElementSpan(int Utf16Start, int Utf16Length, float X, float Width, bool IsFallback)
{
    /// <summary>右端位置。</summary>
    public float XEnd => X + Width;
}

/// <summary>
/// 一帧歌词画面的不可变描述。
/// </summary>
/// <remarks>
/// <para>
/// 这是 docs/development.md §10.2 中 <c>ProjectSnapshot</c> 思路的最小验证：
/// 渲染输入是一个不可变值，渲染过程不会读回 UI 状态，
/// 因此预览与离屏导出可以共用同一个渲染器（§10.3）。
/// </para>
/// <para>
/// 颜色使用 <see cref="SKColor" /> 而不是 Avalonia 的 <c>Color</c>，
/// 以保证本类型所在的程序集不依赖 UI 框架。
/// </para>
/// </remarks>
/// <param name="Line">已布局的整行文本。</param>
/// <param name="Elements">元素的落位信息。</param>
/// <param name="ProgressX">已唱部分宽度（相对整行左端，像素）。</param>
/// <param name="CurrentElementIndex">当前正在演唱的元素序号；无则为 -1。</param>
/// <param name="Width">画布宽度。</param>
/// <param name="Height">画布高度。</param>
/// <param name="FontSize">字号（像素）。</param>
/// <param name="BackgroundColor">背景色。</param>
/// <param name="GridColor">参考网格色。</param>
/// <param name="UnsungColor">未唱颜色。</param>
/// <param name="SungColor">已唱颜色。</param>
/// <param name="CurrentColor">当前元素颜色。</param>
/// <param name="PlayheadColor">播放头颜色。</param>
/// <param name="BoundaryColor">元素边界刻度颜色。</param>
public sealed record KaraokeFrame(
    ShapedLine Line,
    IReadOnlyList<KaraokeElementSpan> Elements,
    float ProgressX,
    int CurrentElementIndex,
    float Width,
    float Height,
    float FontSize,
    SKColor BackgroundColor,
    SKColor GridColor,
    SKColor UnsungColor,
    SKColor SungColor,
    SKColor CurrentColor,
    SKColor PlayheadColor,
    SKColor BoundaryColor);
