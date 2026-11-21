using MakoKaraGenerator.Text;

namespace MakoKaraGenerator.Rendering.Skia;

public sealed partial class SkiaSceneGeometry
{
    /// <summary>
    /// 单个元素的扫字与锚定区间：原始 Advance 区间与安全钳位后的非空白墨迹区间（契约 A1）。
    /// </summary>
    /// <param name="AdvanceLeft">整行整形得到的 Advance 起点。</param>
    /// <param name="AdvanceRight">整行整形得到的 Advance 终点。</param>
    /// <param name="InkLeft">安全钳位至 Advance 区间内的非空白墨迹起点；无墨迹或共享字簇/RTL 时回退为 <see cref="AdvanceLeft"/>。</param>
    /// <param name="InkRight">安全钳位至 Advance 区间内的非空白墨迹终点；无墨迹或共享字簇/RTL 时回退为 <see cref="AdvanceRight"/>。</param>
    internal readonly record struct TokenWipeSpan(
        float AdvanceLeft,
        float AdvanceRight,
        float InkLeft,
        float InkRight);

    /// <summary>
    /// 统一纯几何辅助：计算覆盖 <c>[utf16Start, utf16End)</c> 的元素 Advance 区间与安全钳位后的非空白墨迹区间。
    /// </summary>
    /// <remarks>
    /// 视觉扫字（硬切/羽化）、扫字线高亮峰、注音居中锚定与 SDK v2 <c>TokenGeometrySample.ProgressPath</c>
    /// 统一使用本函数，守住「播放头横坐标 == 逐字换色像素边界」不变量并防止斜体墨迹越界。
    /// </remarks>
    internal static TokenWipeSpan ResolveTokenWipeSpan(ShapedLine shaped, int utf16Start, int utf16End)
    {
        float advLeft = shaped.XAt(utf16Start);
        float advRight = shaped.XAt(utf16End);

        // 非正向（RTL / 零宽）或缺少绘制单元时回退至原始 Advance 区间。
        if (utf16End <= utf16Start || !(advRight > advLeft) || shaped.DrawUnits.IsDefaultOrEmpty)
        {
            return new(advLeft, advRight, advLeft, advRight);
        }

        // 视觉 RTL / 非单调排版行（如希伯来文、阿拉伯文）保守回退至原始 Advance 区间。
        for (int i = 1; i < shaped.Elements.Count; i++)
        {
            if (shaped.Elements[i].X < shaped.Elements[i - 1].X)
            {
                return new(advLeft, advRight, advLeft, advRight);
            }
        }

        float rawInkLeft = float.PositiveInfinity;
        float rawInkRight = float.NegativeInfinity;
        bool hasInk = false;

        foreach (ShapedDrawUnit unit in shaped.DrawUnits)
        {
            if (unit.Utf16End <= utf16Start || unit.Utf16Start >= utf16End)
            {
                continue;
            }

            // 跨越元素边界的共享连字字簇（例如 ffi 拆成多个元素）无法分离单元素墨迹，保守回退至 Advance。
            if (unit.Utf16Start < utf16Start || unit.Utf16End > utf16End)
            {
                return new(advLeft, advRight, advLeft, advRight);
            }

            foreach (ShapedGlyph glyph in unit.Glyphs)
            {
                if (glyph.InkBounds is not { } ink)
                {
                    continue;
                }

                if (!float.IsFinite(ink.Left) || !float.IsFinite(ink.Right) || !(ink.Right > ink.Left))
                {
                    continue;
                }

                if (ink.Left < rawInkLeft) rawInkLeft = ink.Left;
                if (ink.Right > rawInkRight) rawInkRight = ink.Right;
                hasInk = true;
            }
        }

        if (!hasInk || !(rawInkRight > rawInkLeft))
        {
            return new(advLeft, advRight, advLeft, advRight);
        }

        float inkLeft = Math.Clamp(rawInkLeft, advLeft, advRight);
        float inkRight = Math.Clamp(rawInkRight, advLeft, advRight);

        return inkRight > inkLeft
            ? new(advLeft, advRight, inkLeft, inkRight)
            : new(advLeft, advRight, advLeft, advRight);
    }
}
