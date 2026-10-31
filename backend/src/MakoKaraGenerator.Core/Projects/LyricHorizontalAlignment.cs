namespace MakoKaraGenerator.Core.Projects;

/// <summary>
/// 歌词行的水平对齐方式。
/// </summary>
/// <remarks>
/// <para>
/// docs/development.md §4.1 的 P0 要求"对齐方式和基本样式"。此前横向恒定居中，
/// 纵向位置由 <see cref="LyricStyle.VerticalCenterRatio" /> 决定。
/// </para>
/// <para>
/// 刻意<b>没有</b>边距参数：左/右对齐就是贴着画布边缘。理由是"文字该离边缘多远"
/// 是风格问题，加一个自己拍的数字不如等真的需要时再做成显式设置——
/// 而且居中本来也没有边距，三档之间的规则因此是一致的。
/// </para>
/// </remarks>
public enum LyricHorizontalAlignment
{
    /// <summary>左对齐：文字左端贴画布左边缘。</summary>
    Left = 0,

    /// <summary>水平居中（默认，与加入本字段之前的行为一致）。</summary>
    Center = 1,

    /// <summary>右对齐：文字右端贴画布右边缘。</summary>
    Right = 2,
}
