namespace MakoKaraGenerator.Backend.Contracts;

/// <summary>保留导出私有合同的错误编号；解析统一使用中性PCM输入检查。</summary>
public static class WorkerExportPcmWave
{
    public static long Validate(Stream stream, long expectedSize)
    {
        try { return PcmWaveInput.Validate(stream, expectedSize).DurationUs; }
        catch (WorkerProtocolException error)
        { throw new WorkerProtocolException(error.Message, "WorkerExportAudioUnsupported"); }
    }
}
