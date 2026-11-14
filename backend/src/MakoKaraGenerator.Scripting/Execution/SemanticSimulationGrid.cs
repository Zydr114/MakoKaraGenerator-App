namespace MakoKaraGenerator.Scripting.Execution;

/// <summary>R0 已接受的整数微秒 120 Hz 网格；不随渲染帧率变化。</summary>
public static class SemanticSimulationGrid
{
    /// <summary>第 n 个点 floor(n×1000000/120)，避免中间乘法溢出。</summary>
    public static long TimeAt(long index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        return checked(index / 120 * 1_000_000 + index % 120 * 1_000_000 / 120);
    }
    /// <summary>不晚于目标时刻的最后网格索引；含 floor 造成的整数边界。</summary>
    public static long IndexAtOrBefore(long timeUs)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(timeUs);
        return checked(timeUs / 1_000_000 * 120 + ((timeUs % 1_000_000 + 1) * 120 - 1) / 1_000_000);
    }
}
