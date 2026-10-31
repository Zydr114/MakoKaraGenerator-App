namespace MakoKaraGenerator.Timeline;

/// <summary>
/// 时间轴上的循环选区（入点 → 出点）。
/// </summary>
/// <param name="StartUs">入点（微秒）。</param>
/// <param name="EndUs">出点（微秒）。</param>
/// <remarks>
/// <para>
/// 这是<b>界面状态</b>而不是工程数据：它不随工程保存，也不进撤销栈
/// （与"当前缩放/平移到哪里"同类）。放在本程序集是为了让"拖出来的选区到底合不合法"
/// 能被单元测试直接钉住，而不是靠眼看。
/// </para>
/// <para>
/// 两个端点由用户分别设置（可以先按 O 再按 I），因此"入点晚于出点"是一个真实可到达的状态。
/// 本类型如实表示它（<see cref="IsValid"/> 为 <see langword="false"/>），
/// <b>不</b>在这里替用户把两端交换过来——静默修正会让人以为自己设反的那次操作被接受了。
/// </para>
/// </remarks>
public readonly record struct LoopRegion(long StartUs, long EndUs)
{
    /// <summary>入点非负、且严格早于出点时选区有效。</summary>
    public bool IsValid => StartUs >= 0 && EndUs > StartUs;

    /// <summary>选区长度（微秒）；无效选区为 0。</summary>
    public long DurationUs => IsValid ? EndUs - StartUs : 0L;

    /// <summary>用一次拖拽的两个端点造选区，与拖拽方向无关。</summary>
    /// <param name="anchorUs">按下时的时刻（微秒）。</param>
    /// <param name="currentUs">当前指针处的时刻（微秒）。</param>
    /// <returns>有序的选区。</returns>
    /// <remarks>
    /// 拖拽<b>可以</b>在这里交换两端：方向是这个手势自己的一部分，
    /// 与"用户依次敲了两个数字"不同，不存在"他其实想反过来"这个歧义。
    /// </remarks>
    public static LoopRegion FromDrag(long anchorUs, long currentUs) =>
        anchorUs <= currentUs
            ? new LoopRegion(anchorUs, currentUs)
            : new LoopRegion(currentUs, anchorUs);

    /// <summary>某个时刻是否已经到达或越过出点（循环播放据此跳回入点）。</summary>
    /// <param name="timeUs">时刻（微秒）。</param>
    /// <returns>到达或越过出点时为 <see langword="true"/>。</returns>
    public bool HasReachedEnd(long timeUs) => IsValid && timeUs >= EndUs;
}
