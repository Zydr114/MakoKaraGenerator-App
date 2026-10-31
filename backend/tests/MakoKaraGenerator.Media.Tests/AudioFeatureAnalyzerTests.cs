using System.Collections.Immutable;
using MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Media.Tests;

public sealed class AudioFeatureAnalyzerTests : IDisposable
{
    private readonly TemporaryDirectory _directory = new();
    public void Dispose() => _directory.Dispose();
    private string Write(float[] samples, int rate = 48_000, string name = "audio.wav")
    { string path = _directory.FilePath(name); WavWriter.Write(path, samples, rate); return path; }
    private static float[] Signal(double duration, int rate, Func<double, double> value)
        => Enumerable.Range(0, (int)Math.Round(duration * rate)).Select(index => (float)value(index / (double)rate)).ToArray();
    private static ImmutableArray<AudioFeaturePoint> Points(AudioFeatures features)
        => features.GetRange(new(0, features.DurationUs!.Value)).Points;

    [Fact]
    public async Task 真正静音有零能量无节拍且短尾和半开时长完整保留()
    {
        var result = await AudioFeatureAnalyzer.CreateDefault().AnalyzeAsync(Write(new float[48_624]));
        Assert.Equal(AudioAvailability.Available, result.Features.Availability); Assert.Null(result.DiagnosticCode);
        Assert.Equal(48_624, result.SampleCount); Assert.Equal(1_013_000, result.Features.DurationUs);
        var points = Points(result.Features); Assert.Equal(51, points.Length); Assert.Equal(1_000_000, points[^1].TimeUs);
        Assert.All(points, point => { Assert.Equal(0, point.Values.BassLevel); Assert.Equal(0, point.Values.Pulse);
            Assert.Null(point.Values.BeatPhase); Assert.Equal(0, point.Values.BeatConfidence); });
        Assert.Equal(AudioAvailability.Available, result.Features.GetAt(1_012_999).Availability);
        Assert.Null(result.Features.GetAt(1_013_000).Values); Assert.Equal(AudioAvailability.OutsideRange, result.Features.GetAt(1_013_000).Availability);
        Assert.NotNull(result.SourceHash); Assert.NotNull(result.PcmHash); Assert.StartsWith("sha256:", result.Features.Revision);
    }
    [Fact]
    public async Task 同音轨低频响应明显高于高频且持续音不伪造节拍()
    {
        var samples = Signal(3, 48_000, time => .7 * Math.Sin(2 * Math.PI * (time < 1 ? 80 : time < 2 ? 1000 : 5) * time));
        var features = (await AudioFeatureAnalyzer.CreateDefault().AnalyzeAsync(Write(samples))).Features;
        Assert.InRange(features.GetAt(800_000).Values!.Value.BassLevel, .65, .85);
        Assert.InRange(features.GetAt(1_800_000).Values!.Value.BassLevel, 0, .1);
        Assert.InRange(features.GetAt(2_800_000).Values!.Value.BassLevel, 0, .15);
        Assert.All(Points(features), point => Assert.Null(point.Values.BeatPhase));
        var highOnly = (await AudioFeatureAnalyzer.CreateDefault().AnalyzeAsync(Write(Signal(1, 48_000,
            time => .7 * Math.Sin(2 * Math.PI * 1000 * time)), name: "high-only.wav"))).Features;
        Assert.InRange(highOnly.GetAt(800_000).Values!.Value.BassLevel, 0, .05);
    }
    [Fact]
    public async Task 规则脉冲有近似节奏但首次三拍不猜相位且长空档会失效()
    {
        var samples = Signal(6.5, 48_000, time => time >= .2 && time < 4.3 && (time - .2) % .5 < .06 ? .8 * Math.Sin(2 * Math.PI * 80 * time) : 0);
        var features = (await AudioFeatureAnalyzer.CreateDefault().AnalyzeAsync(Write(samples))).Features;
        Assert.All(Points(features).Where(point => point.TimeUs < 1_650_000), point => Assert.Null(point.Values.BeatPhase));
        var beat = features.GetAt(2_460_000).Values!.Value;
        Assert.NotNull(beat.BeatPhase); Assert.InRange(beat.BeatPhase.Value, .35, .65); Assert.InRange(beat.BeatConfidence, .8, 1);
        Assert.InRange(features.GetAt(2_200_000).Values!.Value.Pulse, .8, 1);
        Assert.Null(features.GetAt(6_000_000).Values!.Value.BeatPhase);
    }
    [Fact]
    public async Task 不规则脉冲不输出稳定拍点()
    {
        double[] starts = [.2, .7, 1.5, 1.9, 2.65, 3.1, 4.0];
        var features = (await AudioFeatureAnalyzer.CreateDefault().AnalyzeAsync(Write(Signal(4.6, 48_000,
            time => starts.Any(start => time >= start && time < start + .06) ? .8 * Math.Sin(2 * Math.PI * 80 * time) : 0)))).Features;
        Assert.All(Points(features), point => Assert.Null(point.Values.BeatPhase));
    }
    [Theory]
    [InlineData(32_000)] [InlineData(44_100)] [InlineData(48_000)] [InlineData(96_000)]
    public async Task 不同源采样率实际重采样到相同整数微秒hop(int rate)
    {
        string path = Write(Signal(2.54, rate, time => .7 * Math.Sin(2 * Math.PI * 80 * time)), rate);
        var result = await AudioFeatureAnalyzer.CreateDefault().AnalyzeAsync(path);
        Assert.Equal(AudioAvailability.Available, result.Features.Availability); Assert.InRange(result.SampleCount!.Value, 121_919, 121_921);
        Assert.InRange(result.Features.DurationUs!.Value, 2_539_980, 2_540_021);
        Assert.All(Points(result.Features).Select((point, index) => (point, index)), item => Assert.Equal(item.index * 20_000L, item.point.TimeUs));
        Assert.InRange(result.Features.GetAt(1_000_000).Values!.Value.BassLevel, .9, 1);
        long nativeCount = await new FfmpegPcmReader().ReadMonoSamplesAsync(path, _ => ValueTask.CompletedTask);
        Assert.Equal((long)Math.Round(2.54 * rate), nativeCount);
    }
    [Fact]
    public async Task 相同文件重建和移动路径修订一致而音频修改建立新修订()
    {
        var analyzer = AudioFeatureAnalyzer.CreateDefault();
        string path = Write(Signal(.4, 48_000, time => .5 * Math.Sin(2 * Math.PI * 80 * time)));
        var first = await analyzer.AnalyzeAsync(path); var same = await analyzer.AnalyzeAsync(path);
        string moved = _directory.FilePath("moved.wav"); File.Copy(path, moved);
        var copy = await analyzer.AnalyzeAsync(moved);
        Assert.Equal(first.Features.Revision, same.Features.Revision); Assert.Equal(first.Features.Revision, copy.Features.Revision);
        Assert.Equal(Points(first.Features).ToArray(), Points(copy.Features).ToArray());
        Write(Signal(.4, 48_000, time => .7 * Math.Sin(2 * Math.PI * 80 * time)));
        var changed = await analyzer.AnalyzeAsync(path);
        Assert.NotEqual(first.SourceHash, changed.SourceHash); Assert.NotEqual(first.PcmHash, changed.PcmHash); Assert.NotEqual(first.Features.Revision, changed.Features.Revision);
    }
    [Fact]
    public async Task 任意回调分块不改变分析或规范PCM修订且不截断有限超范围浮点()
    {
        string path = Write([0]); var samples = Signal(.273, 48_000, time => 1.3 * Math.Sin(2 * Math.PI * 80 * time));
        AudioFeatureAnalysis? basis = null;
        foreach (int chunk in new[] { 1, 959, 960, 16_384 })
        {
            var result = await new AudioFeatureAnalyzer(new Probe(), new Reader(samples, chunk)).AnalyzeAsync(path);
            Assert.Equal(AudioAvailability.Available, result.Features.Availability);
            if (basis is not null) { Assert.Equal(basis.PcmHash, result.PcmHash); Assert.Equal(basis.Features.Revision, result.Features.Revision);
                Assert.Equal(Points(basis.Features).ToArray(), Points(result.Features).ToArray()); }
            basis = result;
        }
        Assert.Equal(273_000, basis!.Features.DurationUs);
    }
    [Fact]
    public async Task 无源无流和探测解码失败均明确不可用不返回伪静音()
    {
        var analyzer = AudioFeatureAnalyzer.CreateDefault();
        Assert.Equal(AudioAvailability.NoAudio, (await analyzer.AnalyzeAsync(null)).Features.Availability);
        var missing = await analyzer.AnalyzeAsync(_directory.FilePath("missing.wav"));
        Assert.Equal(AudioAvailability.AnalysisFailed, missing.Features.Availability); Assert.Null(missing.Features.GetAt(0).Values);
        string path = Write([0]);
        var absent = await new AudioFeatureAnalyzer(new Probe(noAudio: true), new Reader([0], 1)).AnalyzeAsync(path);
        Assert.Equal(AudioAvailability.NoAudio, absent.Features.Availability);
        var failed = await new AudioFeatureAnalyzer(new Probe(), new Reader([0], 1, before: () => throw new IOException("decode proof"))).AnalyzeAsync(path);
        Assert.Equal(AudioAvailability.AnalysisFailed, failed.Features.Availability); Assert.Contains("decode proof", failed.DiagnosticMessage);
        Assert.Null(failed.Features.Revision); Assert.Null(failed.SampleCount);
    }
    [Theory]
    [InlineData("nonfinite")] [InlineData("empty")] [InlineData("count")] [InlineData("changed")]
    public async Task 非法或变化输入不发布部分分析(string mode)
    {
        string path = Write([0]); float[] samples = mode switch { "nonfinite" => [float.NaN], "empty" => [], _ => [0, 0] };
        var reader = new Reader(samples, 1, mode == "count" ? 3 : null, mode == "changed" ? () => File.AppendAllText(path, "changed") : null);
        var failed = await new AudioFeatureAnalyzer(new Probe(), reader).AnalyzeAsync(path);
        Assert.Equal(AudioAvailability.AnalysisFailed, failed.Features.Availability); Assert.Null(failed.Features.Revision); Assert.Null(failed.Features.GetAt(0).Values);
        Assert.Equal(mode switch { "nonfinite" => "NonFiniteAudioSample", "empty" => "AudioEmptyDecodedStream",
            "count" => "AudioSampleCountMismatch", _ => "AudioSourceChanged" }, failed.DiagnosticCode);
    }
    [Fact]
    public async Task 取消前和解码中取消保持取消而不会伪装分析失败()
    {
        string path = Write([0]); using CancellationTokenSource cancellation = new(); cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => AudioFeatureAnalyzer.CreateDefault().AnalyzeAsync(path, cancellation.Token));
        using CancellationTokenSource midway = new();
        var analyzer = new AudioFeatureAnalyzer(new Probe(), new Reader([0, 0], 1, before: midway.Cancel));
        await Assert.ThrowsAsync<OperationCanceledException>(() => analyzer.AnalyzeAsync(path, midway.Token));
    }
    [Fact]
    public async Task 真实float解码流的非有限样本不发布成功分析()
    {
        string path = _directory.FilePath("nonfinite.wav");
        using (var writer = new BinaryWriter(File.Create(path)))
        {
            writer.Write("RIFF"u8); writer.Write(36 + 3840); writer.Write("WAVE"u8); writer.Write("fmt "u8); writer.Write(16);
            writer.Write((short)3); writer.Write((short)1); writer.Write(48_000); writer.Write(192_000); writer.Write((short)4); writer.Write((short)32);
            writer.Write("data"u8); writer.Write(3840);
            for (int index = 0; index < 960; index++) writer.Write(index == 10 ? float.NaN : 0f);
        }
        var failed = await AudioFeatureAnalyzer.CreateDefault().AnalyzeAsync(path);
        Assert.Equal(AudioAvailability.AnalysisFailed, failed.Features.Availability);
        Assert.Equal("NonFiniteAudioSample", failed.DiagnosticCode); Assert.Null(failed.Features.Revision);
    }
    [Fact]
    public async Task 真实解码中取消传播取消并能重新分析同一文件()
    {
        string path = Write(Signal(.5, 48_000, time => .5 * Math.Sin(2 * Math.PI * 80 * time)));
        using CancellationTokenSource cancellation = new();
        var analyzer = new AudioFeatureAnalyzer(new FfmpegMediaProbe(), new CancelReader(cancellation));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => analyzer.AnalyzeAsync(path, cancellation.Token));
        Assert.Equal(AudioAvailability.Available, (await AudioFeatureAnalyzer.CreateDefault().AnalyzeAsync(path)).Features.Availability);
    }
    private sealed class CancelReader(CancellationTokenSource cancellation) : IResampledPcmSampleReader
    {
        public Task<long> ReadMonoSamplesAsync(string filePath, Func<ReadOnlyMemory<float>, ValueTask> onSamples, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<long> ReadMonoSamplesAtRateAsync(string filePath, int sampleRate, Func<ReadOnlyMemory<float>, ValueTask> onSamples, CancellationToken cancellationToken = default)
            => new FfmpegPcmReader().ReadMonoSamplesAtRateAsync(filePath, sampleRate, async samples =>
            { await onSamples(samples); cancellation.Cancel(); }, cancellationToken);
    }
    private sealed class Probe(bool noAudio = false) : IMediaProbe
    {
        public Task<AudioStreamInfo?> ProbeAsync(string filePath, CancellationToken cancellationToken = default)
            => Task.FromResult<AudioStreamInfo?>(noAudio ? null : new(999, 1, 1, "mock", "mock"));
        public Task<MediaFileInfo?> ProbeFileAsync(string filePath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
    private sealed class Reader(float[] samples, int chunk, long? reported = null, Action? before = null) : IResampledPcmSampleReader
    {
        public Task<long> ReadMonoSamplesAsync(string filePath, Func<ReadOnlyMemory<float>, ValueTask> onSamples, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async Task<long> ReadMonoSamplesAtRateAsync(string filePath, int sampleRate, Func<ReadOnlyMemory<float>, ValueTask> onSamples, CancellationToken cancellationToken = default)
        {
            Assert.Equal(48_000, sampleRate); before?.Invoke();
            for (int offset = 0; offset < samples.Length; offset += chunk) await onSamples(samples.AsMemory(offset, Math.Min(chunk, samples.Length - offset)));
            return reported ?? samples.Length;
        }
    }
}
