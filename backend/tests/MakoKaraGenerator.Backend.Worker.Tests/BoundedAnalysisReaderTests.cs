using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Media;

namespace MakoKaraGenerator.Backend.Worker.Tests;

public sealed class BoundedAnalysisReaderTests
{
    [Fact]
    public async Task 实际采样预算在原Builder回调前拒绝且没有丢尾伪成功()
    {
        int appended = 0, progress = 0;
        var reader = new StaticAudioAnalysisTask.BoundedAnalysisReader(new Blocks(3001), _ => progress++);
        var failure = await Assert.ThrowsAsync<WorkerProtocolException>(() => reader.ReadMonoSamplesAtRateAsync("owned.wav", 48000,
            block => { appended += block.Length; return ValueTask.CompletedTask; }));
        Assert.Equal("WorkerAnalysisBudgetExceeded", failure.Code); Assert.Equal(14_400_000, appended); Assert.Equal(3000, progress);
        Assert.Equal(14_400_000, reader.SampleCount);
    }
    [Fact]
    public async Task 原回调失败不会公布未分析的采样进度()
    {
        int progress = 0; var reader = new StaticAudioAnalysisTask.BoundedAnalysisReader(new Blocks(1), _ => progress++);
        await Assert.ThrowsAsync<IOException>(() => reader.ReadMonoSamplesAtRateAsync("owned.wav", 48000,
            _ => ValueTask.FromException(new IOException("original append failed"))));
        Assert.Equal(0, reader.SampleCount); Assert.Equal(0, progress);
    }
    [Fact]
    public async Task 原AnalyzeAsync实际执行到样本上限后预算异常不会被IOException过滤吞掉()
    {
        using TemporaryDirectory temp = new(); string source = temp.FilePath("controlled-source"); await File.WriteAllBytesAsync(source, [1]);
        var reader = new StaticAudioAnalysisTask.BoundedAnalysisReader(new Blocks(3001), _ => { });
        var failure = await Assert.ThrowsAsync<WorkerProtocolException>(() => new AudioFeatureAnalyzer(new Probe(), reader).AnalyzeAsync(source));
        Assert.Equal("WorkerAnalysisBudgetExceeded", failure.Code); Assert.Equal(14_400_000, reader.SampleCount);
        Assert.IsNotType<IOException>(failure);
    }
    [Fact]
    public async Task 原AnalyzeAsync的取消仍为取消而不是不可用结果()
    {
        using TemporaryDirectory temp = new(); string source = temp.FilePath("controlled-source"); await File.WriteAllBytesAsync(source, [1]);
        using CancellationTokenSource cancellation = new();
        var reader = new StaticAudioAnalysisTask.BoundedAnalysisReader(new Blocks(2), _ => cancellation.Cancel());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new AudioFeatureAnalyzer(new Probe(), reader).AnalyzeAsync(source, cancellation.Token));
    }
    private sealed class Probe : IMediaProbe
    {
        public Task<AudioStreamInfo?> ProbeAsync(string path, CancellationToken cancellationToken = default)
            => Task.FromResult<AudioStreamInfo?>(new(300_000_000, 48000, 1, "controlled", "controlled"));
        public Task<MediaFileInfo?> ProbeFileAsync(string path, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
    private sealed class Blocks(int blocks) : IResampledPcmSampleReader
    {
        public Task<long> ReadMonoSamplesAsync(string path, Func<ReadOnlyMemory<float>, ValueTask> onSamples, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public async Task<long> ReadMonoSamplesAtRateAsync(string path, int rate, Func<ReadOnlyMemory<float>, ValueTask> onSamples,
            CancellationToken cancellationToken = default)
        {
            float[] block = new float[4800];
            for (int i = 0; i < blocks; i++) { cancellationToken.ThrowIfCancellationRequested(); await onSamples(block); }
            return (long)blocks * block.Length;
        }
    }
}
