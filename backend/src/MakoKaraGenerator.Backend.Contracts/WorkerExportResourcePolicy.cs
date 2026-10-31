namespace MakoKaraGenerator.Backend.Contracts;

/// <summary>宿主固定的资源策略；null 表示该维度没有部署限额，文件仍按流处理并受磁盘错误和取消控制。</summary>
public sealed record WorkerExportResourcePolicy
{
    public int? MaxFrames { get; init; }
    public long? MaxDurationUs { get; init; }
    public int? MaxFrameRate { get; init; }
    public int? MaxCanvasWidth { get; init; }
    public int? MaxCanvasHeight { get; init; }
    public long? MaxRawFrameBytes { get; init; }
    public long? MaxMediaBytes { get; init; }
    public long? MaxAudioBytes { get; init; }
    public long? MaxCompressedAudioDurationUs { get; init; }
    public int? OperationTimeoutSeconds { get; init; }

    public void Validate()
    {
        if (MaxFrames is <= 0 || MaxDurationUs is <= 0 || MaxFrameRate is <= 0 || MaxCanvasWidth is <= 0
            || MaxCanvasHeight is <= 0 || MaxRawFrameBytes is <= 0 || MaxMediaBytes is <= 0 || MaxAudioBytes is <= 0
            || MaxCompressedAudioDurationUs is <= 0 || OperationTimeoutSeconds is <= 0)
            throw new WorkerProtocolException("导出宿主资源策略必须为正数或 null。", "WorkerExportInputRejected");
    }
}
