using System.Text.Json.Nodes;

namespace MakoKaraGenerator.Core.Persistence;

/// <summary>
/// 把开发期唯一一对 <c>gradientStartColor</c> / <c>gradientEndColor</c> 翻译成渐变样式里的“已唱”一档（ADR-0040 之后字幕只保留未唱/已唱两态）。
/// </summary>
/// <remarks>
/// <para>
/// 那一对字段属于未发布的中间状态：它被同时用在“未唱”与“已唱”两遍绘制上，
/// 两遍像素完全相同，逐字扫描的高亮因此不可见（ADR-0032 记录了这次修正）。
/// </para>
/// <para>
/// 迁移而不是直接丢弃，是为了不静默丢掉用户在开发版里设过的颜色；
/// 译成“已唱渐变”是语义上最接近的选择——原来的意图是让高亮那一段有渐变。
/// </para>
/// </remarks>
internal static class LegacyGradientFields
{
    private const string OldStart = "gradientStartColor";
    private const string OldEnd = "gradientEndColor";
    private const string NewStart = "sungGradientStartColor";
    private const string NewEnd = "sungGradientEndColor";

    /// <summary>在一个样式对象内翻译旧字段。</summary>
    /// <param name="style">样式对象（基线的 <c>lyricStyle</c>，或某层覆盖）。</param>
    /// <param name="log">迁移日志。</param>
    /// <returns>确实翻译过时为 <see langword="true"/>。</returns>
    /// <exception cref="ArgumentNullException">任一参数为 <see langword="null"/> 时抛出。</exception>
    internal static bool Translate(JsonObject style, MigrationLog log)
    {
        ArgumentNullException.ThrowIfNull(style);
        ArgumentNullException.ThrowIfNull(log);

        string? start = Text(style, OldStart);
        string? end = Text(style, OldEnd);

        if (start is null && end is null)
        {
            return false;
        }

        style.Remove(OldStart);
        style.Remove(OldEnd);

        if (start is not null)
        {
            style[NewStart] = start;
        }

        if (end is not null)
        {
            style[NewEnd] = end;
        }

        log.Add(
            $"发现开发期的 {OldStart} / {OldEnd}：已按「已唱渐变」迁移成 {NewStart} / {NewEnd}。");

        return true;
    }

    /// <summary>读一个字符串字段；类型不符时按缺失处理（迁移不负责修数据）。</summary>
    private static string? Text(JsonObject source, string key) =>
        source[key] is JsonValue value && value.TryGetValue(out string? text) ? text : null;
}
