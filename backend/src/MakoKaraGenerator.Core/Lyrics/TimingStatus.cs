namespace MakoKaraGenerator.Core.Lyrics;

/// <summary>
/// 行或文本元素的时间来源状态。
/// </summary>
/// <remarks>
/// <para>
/// 状态见 docs/development.md §5.4。它<b>不是装饰信息</b>：
/// 导出前必须能据此提示用户哪些字仍是估算值，
/// 并且严禁把逐行时间平均分配后标记为精确逐字时间
/// （docs/development.md §19 设计底线）。
/// </para>
/// <para>
/// 枚举的数值顺序保证序列化稳定（按名称写入 JSON，见持久化层）。
/// </para>
/// </remarks>
public enum TimingStatus
{
    /// <summary>没有时间。<see cref="LyricToken.Time"/> / <see cref="LyricLine.Time"/> 应为 <see langword="null"/>。</summary>
    Missing = 0,

    /// <summary>从外部文件导入。<b>不</b>代表已经人工确认。</summary>
    Imported = 1,

    /// <summary>软件估算得到（例如按字符权重分配、自动对齐）。需要用户校正后才可以视为精确。</summary>
    Estimated = 2,

    /// <summary>用户修改过。</summary>
    Edited = 3,

    /// <summary>用户明确确认。</summary>
    Confirmed = 4,

    /// <summary>不满足数据约束（例如终点不大于起点）。</summary>
    Invalid = 5,
}

/// <summary>
/// <see cref="TimingStatus"/> 的语义辅助方法。
/// </summary>
/// <remarks>
/// 这些判断被集中在一处，是为了避免各处自行诠释状态含义而出现不一致，
/// 尤其是"哪些状态需要在导出报告里被列为不确定"。
/// </remarks>
public static class TimingStatusExtensions
{
    /// <summary>时间是否仍未被用户确认（缺失、仅导入或仅估算）。</summary>
    /// <param name="status">待判断的状态。</param>
    /// <returns>属于 <see cref="TimingStatus.Missing"/>、<see cref="TimingStatus.Imported"/> 或 <see cref="TimingStatus.Estimated"/> 时为 <see langword="true"/>。</returns>
    /// <remarks>
    /// "仅导入" 也算未确认：导入的逐字时间可能来自自动对齐，不能直接当作精确数据展示（docs/development.md §5.4）。
    /// </remarks>
    public static bool IsUnconfirmed(this TimingStatus status) =>
        status is TimingStatus.Missing or TimingStatus.Imported or TimingStatus.Estimated;

    /// <summary>时间是否可以被视为用户已确认的精确值。</summary>
    /// <param name="status">待判断的状态。</param>
    /// <returns>属于 <see cref="TimingStatus.Edited"/> 或 <see cref="TimingStatus.Confirmed"/> 时为 <see langword="true"/>。</returns>
    public static bool IsResolved(this TimingStatus status) =>
        status is TimingStatus.Edited or TimingStatus.Confirmed;

    /// <summary>该状态是否必须伴随一个时间区间。</summary>
    /// <param name="status">待判断的状态。</param>
    /// <returns>除 <see cref="TimingStatus.Missing"/> 外的状态均返回 <see langword="true"/>。</returns>
    public static bool RequiresTimeRange(this TimingStatus status) => status != TimingStatus.Missing;

    /// <summary>取得适合在导入/导出报告中显示的中文说明。</summary>
    /// <param name="status">待描述的状态。</param>
    /// <returns>状态的中文名称。</returns>
    public static string ToDisplayName(this TimingStatus status) => status switch
    {
        TimingStatus.Missing => "缺失",
        TimingStatus.Imported => "导入",
        TimingStatus.Estimated => "估算",
        TimingStatus.Edited => "已修改",
        TimingStatus.Confirmed => "已确认",
        TimingStatus.Invalid => "非法",
        _ => "未知",
    };
}
