namespace MakoKaraGenerator.Backend.Hosting;

/// <summary>进程内命令重试窗口与每会话有效回执上限。</summary>
public sealed record ProjectCommandRetentionOptions
{
    /// <summary>客户端请求截止时间距离服务时钟的最大秒数，最多十分钟。</summary>
    public int MaxRetryWindowSeconds { get; init; } = 600;

    /// <summary>每会话保留到各自截止时间的命令回执数量上限。</summary>
    public int MaxReceiptsPerSession { get; init; } = 1024;

    /// <summary>单个固定元信息回执的序列化字节上限。</summary>
    public int MaxReceiptBytes { get; init; } = 64 * 1024;

    /// <summary>验证有限重试窗口和正数回执配额。</summary>
    public void Validate()
    {
        if (MaxRetryWindowSeconds is <= 0 or > 600)
            throw new ArgumentOutOfRangeException(nameof(MaxRetryWindowSeconds), "命令重试窗口必须在 1～600 秒之间。");
        if (MaxReceiptsPerSession is <= 0 or > 4096 || MaxReceiptBytes is < 1024 or > 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(MaxReceiptsPerSession));
    }
}
