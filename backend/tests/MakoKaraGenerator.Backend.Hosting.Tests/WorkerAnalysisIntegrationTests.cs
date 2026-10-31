using System.Buffers.Binary;
using System.Security.Cryptography;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Hosting;
using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.Media;
using MakoKaraGenerator.Rendering;
using Xunit.Abstractions;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

public sealed class WorkerAnalysisIntegrationTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(32000, 1)] [InlineData(44100, 2)] [InlineData(48000, 1)] [InlineData(96000, 2)]
    public async Task 真实独立Worker与原分析器完整canonical所有hop及双SHA逐字节相等(int rate, int channels)
    {
        using TemporaryDirectory temp = new(); string wav = temp.FilePath("actual.wav"); await File.WriteAllBytesAsync(wav, Wave(rate, channels, 4.013));
        var grant = await GrantAsync(wav); var executor = Executor(); var caps = await executor.ProbeAsync();
        Assert.True(caps.GuardAvailable); Assert.True(caps.ToolStartupVerified); Assert.Null(caps.FailureCode);
        List<BackendAnalysisProgressDto> progress = [];
        byte[] actual = await executor.ExecuteAsync(grant, new InlineProgress(progress.Add));
        var oracle = (await AudioFeatureAnalyzer.CreateDefault().AnalyzeAsync(wav)).ToSourceData()!.Data!;
        Assert.Equal(AudioAnalysisJson.Encode(oracle), actual);
        var data = AnalysisResultCodec.Decode(actual, grant.Sha256);
        Assert.Equal(grant.Sha256, data.SourceHash); Assert.Equal(oracle.PcmHash, data.PcmHash); Assert.Equal(oracle.Revision, data.Revision);
        Assert.Equal(oracle.ContentHash, WorkerProtocol.ComputeSha256(actual)); Assert.Equal(201, data.Points.Length);
        Assert.True(data.Points[^1].TimeUs < data.DurationUs); Assert.NotEmpty(progress);
        Assert.Equal("prepared", progress[^1].Stage); Assert.Equal(data.SampleCount.ToString(System.Globalization.CultureInfo.InvariantCulture), progress[^1].TotalSamples);
        Assert.DoesNotContain(progress, value => value.Stage != "prepared" && value.TotalSamples is not null);
        string moved = temp.FilePath("same-bytes.wav"); File.Copy(wav, moved);
        Assert.Equal(actual, await executor.ExecuteAsync(await GrantAsync(moved)));
        output.WriteLine($"Real analysis sourceRate={rate}, channels={channels}, samples={data.SampleCount}, hops={data.Points.Length}, duration={data.DurationUs}, canonicalBytes={actual.Length}, sourceSHA={data.SourceHash}, pcmSHA={data.PcmHash}");
    }
    [Fact]
    public async Task 实际采样进度后的取消不发布部分结果且原执行器可重试()
    {
        using TemporaryDirectory temp = new(); string wav = temp.FilePath("cancel.wav"); await File.WriteAllBytesAsync(wav, Wave(32000, 1, 30));
        var grant = await GrantAsync(wav); var executor = Executor(); using CancellationTokenSource cancellation = new(); long actualSamples = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => executor.ExecuteAsync(grant, new InlineProgress(value =>
        {
            if (value.Stage == "decoding") { actualSamples = long.Parse(value.CompletedSamples, System.Globalization.CultureInfo.InvariantCulture); cancellation.Cancel(); }
        }), cancellation.Token));
        Assert.True(actualSamples > 0);
        byte[] complete = await executor.ExecuteAsync(grant); var data = AnalysisResultCodec.Decode(complete, grant.Sha256);
        Assert.Equal(1_440_000, data.SampleCount); Assert.Equal(1500, data.Points.Length);
    }
    [Fact]
    public async Task 授权SHA与输入WAV错配失败没有静音或部分成功()
    {
        using TemporaryDirectory temp = new(); string wav = temp.FilePath("source.wav"); await File.WriteAllBytesAsync(wav, Wave(48000, 1, .1));
        var grant = await GrantAsync(wav); var executor = Executor();
        var failure = await Assert.ThrowsAsync<RenderSessionFailureException>(() => executor.ExecuteAsync(grant with { Sha256 = new('a', 64) }));
        Assert.Equal("WorkerAnalysisSourceMismatch", failure.Code); Assert.Null(failure.DiagnosticSnapshot);
        await File.WriteAllBytesAsync(wav, new byte[44]);
        grant = await GrantAsync(wav);
        failure = await Assert.ThrowsAsync<RenderSessionFailureException>(() => executor.ExecuteAsync(grant));
        Assert.Equal("PcmWaveInputUnsupported", failure.Code); Assert.Null(failure.DiagnosticSnapshot);
    }
    private static async Task<BackendAudioAnalysisGrant> GrantAsync(string path)
        => new(path, new FileInfo(path).Length, Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(path))));
    private static WorkerBackendAnalysisExecutor Executor()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "MakoKaraGenerator.slnx"))) root = root.Parent;
        string dll = Path.Combine(root!.FullName, "src/MakoKaraGenerator.Backend.Worker/bin/Debug/net10.0/MakoKaraGenerator.Backend.Worker.dll");
        return new(new WorkerProcessOptions("dotnet", [dll]), "/usr/bin/ffmpeg", "/usr/bin/ffprobe");
    }
    private static byte[] Wave(int rate, int channels, double seconds)
    {
        int samples = (int)Math.Round(rate * seconds); int length = samples * channels * 2; byte[] bytes = new byte[44 + length];
        "RIFF"u8.CopyTo(bytes); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4, 4), bytes.Length - 8); "WAVEfmt "u8.CopyTo(bytes.AsSpan(8));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16, 4), 16); BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(20, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(22, 2), (ushort)channels); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(24, 4), rate);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(28, 4), rate * channels * 2); BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(32, 2), (ushort)(channels * 2));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(34, 2), 16); "data"u8.CopyTo(bytes.AsSpan(36)); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(40, 4), length);
        for (int sample = 0; sample < samples; sample++)
        {
            double time = sample / (double)rate;
            double value = time % .5 < .06 ? Math.Sin(time * 2 * Math.PI * 80) * 20000 : Math.Sin(time * 2 * Math.PI * 1000) * 1000;
            for (int channel = 0; channel < channels; channel++) BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(44 + (sample * channels + channel) * 2, 2), (short)value);
        }
        return bytes;
    }
    private sealed class InlineProgress(Action<BackendAnalysisProgressDto> callback) : IProgress<BackendAnalysisProgressDto>
    { public void Report(BackendAnalysisProgressDto value) => callback(value); }
}
