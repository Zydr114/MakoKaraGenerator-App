using System.Diagnostics;
using System.Security.Cryptography;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Rendering;
using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.Media;

namespace MakoKaraGenerator.Backend.Worker.Tests;

public sealed class CompressedAnalysisTaskTests
{
    [Theory]
    [InlineData("mp3", 44100, 1, false)]
    [InlineData("mp3", 48000, 2, true)]
    [InlineData("flac", 44100, 2, false)]
    public async Task 固定压缩输入经真实媒体解码与原Analyzer完整canonical逐字节相等(string format, int rate, int channels, bool variable)
    {
        using TemporaryDirectory directory = new(); string source = await EncodeAsync(directory, format, rate, channels, variable);
        var grant = await GrantAsync(source, format); List<WorkerMessage> progress = [];
        WorkerMessage result = await StaticAudioAnalysisTask.ExecuteAsync(Request(grant), true,
            message => { progress.Add(message); return Task.CompletedTask; }, CancellationToken.None);
        var oracle = (await AudioFeatureAnalyzer.CreateDefault().AnalyzeAsync(source)).ToSourceData()!.Data!;
        Assert.Equal(AudioAnalysisJson.Encode(oracle), result.Payload); Assert.Equal(grant.Sha256, oracle.SourceHash);
        Assert.Equal(oracle.ContentHash, result.OutputSha256); Assert.Equal("prepared", progress[^1].AnalysisProgress!.Stage);
        Assert.Equal(oracle.SampleCount.ToString(System.Globalization.CultureInfo.InvariantCulture), progress[^1].AnalysisProgress!.TotalSamples);
        Assert.True(oracle.SampleCount > 4800); Assert.True(oracle.Points[^1].TimeUs < oracle.DurationUs);
    }

    [Theory]
    [InlineData("analysis-pcmwav-v1", BackendAudioInputProfiles.Mp3)]
    [InlineData("analysis-compressed-audio-v1", BackendAudioInputProfiles.Pcm16Wave)]
    [InlineData("unknown", BackendAudioInputProfiles.Flac)]
    [InlineData("analysis-compressed-audio-v1", "unknown")]
    public void 元数据执行profile必须与固定音频profile严格一致(string execution, string audio)
    {
        var metadata = new WorkerAnalysisMetadata(execution, new(OperatingSystem.IsWindows() ? @"C:\owned\source" : "/owned/source", 100, new('a', 64), audio), TestMediaToolPaths.Ffmpeg, TestMediaToolPaths.Ffprobe);
        Assert.Equal("WorkerAnalysisProtocolInvalid", Assert.Throws<WorkerProtocolException>(() => WorkerAnalysisContract.EncodeInput(Guid.NewGuid(), metadata)).Code);
    }

    [Theory]
    [InlineData("aac", "mp3", 48000, 1)]
    [InlineData("mp3", "mp3", 192001, 1)]
    [InlineData("flac", "flac", 48000, 3)]
    [InlineData("flac", "mov", 48000, 2)]
    public void 实际Probe不允许其他codec容器采样率或声道(string codec, string container, int rate, int channels)
        => Assert.Equal("WorkerAnalysisInputRejected", Assert.Throws<WorkerProtocolException>(() =>
            StaticAudioAnalysisTask.ValidateProbeInput(new(0, rate, channels, codec, container), codec)).Code);

    [Theory]
    [InlineData("mov,mp4,m4a,3gp,3g2,mj2", true)]
    [InlineData("mov,mp4", true)]
    [InlineData("mov,foreign", false)]
    [InlineData("mov,,mp4", false)]
    public void MOV_probe容器列表只接受完整允许成员(string container, bool accepted)
    {
        var info = new AudioStreamInfo(0, 48000, 2, "aac", container);
        if (accepted)
        {
            StaticAudioAnalysisTask.ValidateProbeInput(info, "mov");
            return;
        }
        Assert.Equal("WorkerAnalysisInputRejected", Assert.Throws<WorkerProtocolException>(() =>
            StaticAudioAnalysisTask.ValidateProbeInput(info, "mov")).Code);
    }

    [Fact]
    public async Task 真工具列表核验仅配置能力且具有当前引擎及精确profile集合()
    {
        Guid task = Guid.NewGuid(); byte[] payload = WorkerAnalysisContract.EncodeInput(task,
            new(WorkerAnalysisContract.Profile, null, TestMediaToolPaths.Ffmpeg, TestMediaToolPaths.Ffprobe), true);
        WorkerMessage result = await StaticAudioAnalysisTask.ExecuteAsync(new(WorkerAnalysisContract.CapabilitiesKind, "cap",
            WorkerProtocol.ComputeSha256(payload), payload, taskId: task.ToString("N")), true, _ => Task.CompletedTask, CancellationToken.None);
        var caps = WorkerAnalysisContract.DecodeJson<WorkerAnalysisCapabilitiesDto>(result.Payload, 4096);
        Assert.True(caps.ToolStartupVerified); Assert.Equal(StaticProjectSnapshotCodec.EngineIdentity, caps.EngineIdentity);
        Assert.Equal(new[] { BackendAudioInputProfiles.Pcm16Wave, BackendAudioInputProfiles.Mp3, BackendAudioInputProfiles.Flac,
            BackendAudioInputProfiles.Aac, BackendAudioInputProfiles.Mov, BackendAudioInputProfiles.Ogg }, caps.AudioProfiles);
        Assert.Equal(new[] { BackendAudioInputProfiles.LegacyAnalysisSource, BackendAudioInputProfiles.CompressedAnalysisSource }, caps.SourceProfiles);
        Assert.Throws<IOException>(() => StaticAudioAnalysisTask.RequireTools(" D wav WAV\n D mp3 MP3", ["wav", "mp3", "flac"]));
        Assert.Throws<IOException>(() => StaticAudioAnalysisTask.RequireTools(" V....D mp3 fake", ["mp3"], true));
    }

    [Fact]
    public async Task 同字节grant格式错配或同长度SHA篡改没有prepared及部分结果()
    {
        using TemporaryDirectory directory = new(); string source = await EncodeAsync(directory, "flac", 48000, 1, false);
        var grant = await GrantAsync(source, "flac");
        foreach (var invalid in new[] { grant with { AudioProfile = BackendAudioInputProfiles.Mp3 }, grant with { Sha256 = new('a', 64) } })
        {
            List<WorkerMessage> progress = [];
            await Assert.ThrowsAsync<WorkerProtocolException>(() => StaticAudioAnalysisTask.ExecuteAsync(Request(invalid), true,
                message => { progress.Add(message); return Task.CompletedTask; }, CancellationToken.None));
            Assert.DoesNotContain(progress, value => value.AnalysisProgress?.Stage == "prepared");
        }
    }

    [Fact]
    public async Task 完整FLAC头但实际编码帧损坏不能发布partial或prepared()
    {
        using TemporaryDirectory directory = new(); string source = await EncodeAsync(directory, "flac", 48000, 1, false);
        byte[] bytes = await File.ReadAllBytesAsync(source); int offset = 4;
        while (true)
        {
            bool last = (bytes[offset] & 128) != 0;
            int length = bytes[offset + 1] << 16 | bytes[offset + 2] << 8 | bytes[offset + 3];
            offset += 4 + length; if (last) break;
        }
        Assert.True(bytes.Length - offset > 16);
        Array.Clear(bytes, offset, bytes.Length - offset); await File.WriteAllBytesAsync(source, bytes);
        List<WorkerMessage> progress = []; var grant = await GrantAsync(source, "flac");
        await Assert.ThrowsAsync<WorkerProtocolException>(() => StaticAudioAnalysisTask.ExecuteAsync(Request(grant), true,
            message => { progress.Add(message); return Task.CompletedTask; }, CancellationToken.None));
        Assert.DoesNotContain(progress, value => value.AnalysisProgress?.Stage == "prepared");
    }

    [Theory]
    [InlineData("aac", 48000, 1)]
    [InlineData("mp3", 192001, 1)]
    [InlineData("mp3", 48000, 3)]
    public async Task 实际Probe工具错误流属性在原Analyzer解码之前严格拒绝(string codec, int rate, int channels)
    {
        using TemporaryDirectory directory = new(); string source = await EncodeAsync(directory, "mp3", 48000, 1, false);
        string json = System.Text.Json.JsonSerializer.Serialize(new { streams = new[] { new { codec_name = codec, sample_rate = rate, channels } }, format = new { format_name = "mp3" } });
        string probe;
        if (OperatingSystem.IsWindows())
        {
            string py = directory.FilePath("owned-probe.py");
            await File.WriteAllTextAsync(py, "print(" + System.Text.Json.JsonSerializer.Serialize(json) + ")\n");
            probe = directory.FilePath("owned-probe.cmd");
            await File.WriteAllTextAsync(probe, $"@echo off\r\n\"{TestMediaToolPaths.Python}\" \"{py}\" %*\r\n");
        }
        else
        {
            probe = directory.FilePath("owned-probe.py");
            await File.WriteAllTextAsync(probe, "#!/usr/bin/python3\nprint(" + System.Text.Json.JsonSerializer.Serialize(json) + ")\n");
            File.SetUnixFileMode(probe, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        Guid task = Guid.NewGuid(); var grant = await GrantAsync(source, "mp3");
        byte[] payload = WorkerAnalysisContract.EncodeInput(task, new(WorkerAnalysisContract.CompressedProfile, grant, TestMediaToolPaths.Ffmpeg, probe));
        var request = new WorkerMessage(WorkerAnalysisContract.RequestKind, "probe-policy", WorkerProtocol.ComputeSha256(payload), payload,
            taskId: task.ToString("N"), sourceSha256: grant.Sha256);
        List<WorkerMessage> progress = [];
        var failure = await Assert.ThrowsAsync<WorkerProtocolException>(() => StaticAudioAnalysisTask.ExecuteAsync(request, true,
            value => { progress.Add(value); return Task.CompletedTask; }, CancellationToken.None));
        Assert.Equal("WorkerAnalysisInputRejected", failure.Code);
        Assert.DoesNotContain(progress, value => value.AnalysisProgress?.Stage is "decoding" or "prepared");
    }

    private static WorkerMessage Request(WorkerAnalysisAudioGrant grant)
    {
        Guid task = Guid.NewGuid(); byte[] bytes = WorkerAnalysisContract.EncodeInput(task,
            new(WorkerAnalysisContract.CompressedProfile, grant, TestMediaToolPaths.Ffmpeg, TestMediaToolPaths.Ffprobe));
        return new(WorkerAnalysisContract.RequestKind, Guid.NewGuid().ToString("N"), WorkerProtocol.ComputeSha256(bytes), bytes,
            taskId: task.ToString("N"), sourceSha256: grant.Sha256);
    }
    private static async Task<WorkerAnalysisAudioGrant> GrantAsync(string path, string format) => new(path, new FileInfo(path).Length,
        Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(path))), format == "mp3" ? BackendAudioInputProfiles.Mp3 : BackendAudioInputProfiles.Flac);
    private static async Task<string> EncodeAsync(TemporaryDirectory directory, string format, int rate, int channels, bool variable)
    {
        string path = directory.FilePath("source." + format);
        var start = new ProcessStartInfo(TestMediaToolPaths.Ffmpeg) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
        foreach (string arg in new[] { "-v", "error", "-f", "lavfi", "-i", "sine=frequency=997:sample_rate=" + rate.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-t", "0.213", "-ac", channels.ToString(System.Globalization.CultureInfo.InvariantCulture), "-c:a", format == "mp3" ? "libmp3lame" : "flac" }) start.ArgumentList.Add(arg);
        if (format == "mp3") { start.ArgumentList.Add(variable ? "-q:a" : "-b:a"); start.ArgumentList.Add(variable ? "4" : "128k"); }
        start.ArgumentList.Add(path);
        using Process process = Process.Start(start)!; Task<string> stderr = process.StandardError.ReadToEndAsync(); Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync(); Assert.True(process.ExitCode == 0, await stderr); await stdout; return path;
    }
}
