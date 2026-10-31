namespace MakoKaraGenerator.Core.Effects;

/// <summary>R0 已接受的包展开预算；逻辑内容量不等于进程峰值内存。</summary>
public sealed record EffectPackageContentBudget
{
    /// <summary>首版固定初始预算。</summary>
    public static EffectPackageContentBudget Default { get; } = new();
    /// <summary>包括清单在内的全部文件字节。</summary>
    public long MaximumBytes { get; init; } = 256L * 1024 * 1024;
    /// <summary>包括清单的文件数量；空目录不占文件内容。</summary>
    public int MaximumFiles { get; init; } = 1024;
    /// <summary>包括清单的单文件字节。</summary>
    public long MaximumFileBytes { get; init; } = 64L * 1024 * 1024;
}

/// <summary>一次读取的顺序预算账；在分配／读取每个文件前登记，不静默忽略超额文件。</summary>
public sealed class EffectPackageContentUsage
{
    private readonly EffectPackageContentBudget _budget;
    private long _bytes;
    private int _files;
    /// <summary>建立显式预算，非法限制拒绝。</summary>
    public EffectPackageContentUsage(EffectPackageContentBudget? budget = null)
    {
        _budget = budget ?? EffectPackageContentBudget.Default;
        if (_budget.MaximumBytes <= 0 || _budget.MaximumFiles <= 0 || _budget.MaximumFileBytes <= 0)
            throw new ArgumentException("包内容预算必须为正。", nameof(budget));
    }
    /// <summary>检查并登记一个真实文件；失败不增加账目。</summary>
    public void Claim(string path, long length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (length > int.MaxValue) throw new EffectPackageException("PackageFileCapacityExceeded", path, "单文件超出本后端不可变字节容量。");
        if (length > _budget.MaximumFileBytes) throw new EffectPackageException("PackageFileBudgetExceeded", path, $"单文件 {length} 字节，限制 {_budget.MaximumFileBytes}。");
        if (_files >= _budget.MaximumFiles) throw new EffectPackageException("PackageFileCountExceeded", path, $"文件数超过 {_budget.MaximumFiles}。");
        if (length > _budget.MaximumBytes - _bytes) throw new EffectPackageException("PackageContentBudgetExceeded", path, $"已有 {_bytes} 字节，新增 {length}，总限制 {_budget.MaximumBytes}。");
        _files++; _bytes += length;
    }
}
