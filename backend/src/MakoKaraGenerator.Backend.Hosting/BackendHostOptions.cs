namespace MakoKaraGenerator.Backend.Hosting;

/// <summary>后端资源目录与配额。目录由组合根创建，业务层不接受客户端路径。</summary>
public sealed record BackendHostOptions
{
    /// <summary>组合根控制的临时资源目录。</summary>
    public required string ResourceRoot { get; init; }
    /// <summary>单上传资源字节上限；null表示不配置字节上限。</summary>
    public long? MaxAssetBytes { get; init; }
    /// <summary>包含暂存上传的磁盘字节配额；null表示不配置字节配额。</summary>
    public long? MaxAssetQuotaBytes { get; init; }

    /// <summary>未引用资源在最后释放后可保留的秒数。</summary>
    public int UnreferencedAssetLifetimeSeconds { get; init; } = 600;
    /// <summary>已发布加在途资源的数量上限。</summary>
    public int MaxAssets { get; init; } = 4096;

    /// <summary>验证所有资源数量、字节和时间边界。</summary>
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ResourceRoot);
        if (MaxAssetBytes is <= 0 || MaxAssetQuotaBytes is <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxAssetBytes), "资源限额必须为正数或 null。");
        if (UnreferencedAssetLifetimeSeconds is <= 0 or > 86400 || MaxAssets is <= 0 or > 65536)
            throw new ArgumentOutOfRangeException(nameof(UnreferencedAssetLifetimeSeconds));
        if (MaxAssetBytes > MaxAssetQuotaBytes)
            throw new ArgumentOutOfRangeException(nameof(MaxAssetBytes), "单资源上限不能超过总配额。");
    }
}
