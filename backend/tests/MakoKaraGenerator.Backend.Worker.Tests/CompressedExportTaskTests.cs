using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Rendering;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Media;
using MakoKaraGenerator.Rendering;

namespace MakoKaraGenerator.Backend.Worker.Tests;

public sealed class CompressedExportTaskTests
{
    [Theory]
    [InlineData("opaqueMp4", "aac")]
    [InlineData("transparentProRes4444", "pcm_s16le")]
    public void 受信压缩音频计划与实际容器音轨策略及报告理由一致(string format, string codec)
    {
        var plan = StaticExportTask.CompressedAudioEncodingPlan(format);
        Assert.False(plan.Copy); Assert.Equal(codec, plan.CodecName);
        Assert.Contains(codec, plan.Reason, StringComparison.Ordinal);
        if (codec == "pcm_s16le") Assert.DoesNotContain("aac", plan.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("mp3", 44100, 1, false, "opaqueMp4")]
    [InlineData("mp3", 48000, 2, true, "opaqueMp4")]
    [InlineData("flac", 44100, 2, false, "opaqueMp4")]
    [InlineData("flac", 44100, 2, false, "transparentProRes4444")]
    public async Task 完整源采样计数进入原导出且MP3不复制为错误音轨(string format, int rate, int channels, bool variable, string output)
    {
        using TemporaryDirectory directory = new(); string source = await EncodeAsync(directory, format, rate, channels, variable);
        var grant = await GrantAsync(source, format); string candidate = directory.FilePath(output == "opaqueMp4" ? "candidate.mp4" : "candidate.mov");
        var factory = new CountingFactory(); List<WorkerMessage> progress = [];
        WorkerMessage request = Request(grant, candidate, output);
        WorkerMessage result = await StaticExportTask.ExecuteAsync(request, true,
            message => { progress.Add(message); return Task.CompletedTask; }, factory, CancellationToken.None);
        long samples = await new FfmpegPcmReader(TestMediaToolPaths.Ffmpeg).ReadMonoSamplesAtRateAsync(source, rate, _ => ValueTask.CompletedTask);
        var report = WorkerExportContract.DecodeReport(result.Payload, new(output, true, 8, 4, 30, 1, 6, 200_000, grant.AudioProfile));
        Assert.Equal((samples * 1_000_000 / rate).ToString(CultureInfo.InvariantCulture), report.AudioDurationUs);
        Assert.True(report.StrictlyVerified); Assert.Equal(output == "opaqueMp4" ? "aac" : "pcm_s16le", report.AudioCodec);
        Assert.Contains(report.Warnings, value => value.Contains(report.AudioCodec!, StringComparison.Ordinal));
        if (output == "transparentProRes4444") Assert.DoesNotContain(report.Warnings, value => value.Contains("aac", StringComparison.Ordinal));
        Assert.Equal(6, report.DecodedFrames); Assert.Equal(6, factory.Frames); Assert.Equal(1, factory.Created); Assert.Equal(1, factory.Disposed);
        Assert.Equal(grant.Path, factory.Snapshot!.AudioPath); Assert.Equal(request.SourceSha256, result.SourceSha256);
        Assert.Equal("prepared", progress[^1].ExportProgress!.Stage);
    }

    [Theory]
    [InlineData("mp3", "opaqueMp4")]
    [InlineData("flac", "opaqueMp4")]
    [InlineData("wav", "opaqueMp4")]
    [InlineData("aac", "opaqueMp4")]
    [InlineData("mov", "opaqueMp4")]
    [InlineData("ogg", "opaqueMp4")]
    [InlineData("mp3", "transparentProRes4444")]
    [InlineData("wav", "transparentProRes4444")]
    public async Task 短音轨补静音到完整画面且报告真实源时长(string format, string output)
    {
        using TemporaryDirectory directory = new(); string source = await EncodeAsync(directory, format, 48000, 1, false);
        var grant = await GrantAsync(source, format); string candidate = directory.FilePath(output == "opaqueMp4" ? "short.mp4" : "short.mov");
        var factory = new CountingFactory();
        WorkerMessage result = await StaticExportTask.ExecuteAsync(Request(grant, candidate, output, 1_000_000), true,
            _ => Task.CompletedTask, factory, CancellationToken.None);
        var report = WorkerExportContract.DecodeReport(result.Payload, new(output, true, 8, 4, 30, 1, 30, 1_000_000, grant.AudioProfile));
        Assert.True(report.StrictlyVerified); Assert.Equal(30, report.DecodedFrames); Assert.Equal(30, factory.Frames);
        long decodedSourceSamples = await new FfmpegPcmReader(TestMediaToolPaths.Ffmpeg).ReadMonoSamplesAtRateAsync(source, 48000, _ => ValueTask.CompletedTask);
        Assert.Equal((decodedSourceSamples * 1_000_000 / 48000).ToString(CultureInfo.InvariantCulture), report.AudioDurationUs);
        Assert.Contains(report.Warnings, value => value.Contains("尾部已补静音", StringComparison.Ordinal));
        Assert.InRange(long.Parse(report.ActualAudioDurationUs!, CultureInfo.InvariantCulture), 950_000, 1_050_000);
        long sampleIndex = 0, early = 0, tail = 0; double earlyEnergy = 0, tailEnergy = 0;
        await new FfmpegPcmReader(TestMediaToolPaths.Ffmpeg).ReadMonoSamplesAtRateAsync(candidate, 48000, data =>
        {
            foreach (float sample in data.Span)
            {
                if (sampleIndex >= 2400 && sampleIndex < 7200) { earlyEnergy += sample * sample; early++; }
                if (sampleIndex >= 24000 && sampleIndex < 43200) { tailEnergy += sample * sample; tail++; }
                sampleIndex++;
            }
            return ValueTask.CompletedTask;
        });
        Assert.True(early > 0 && Math.Sqrt(earlyEnergy / early) > .03, "前段原始音轨应有声音。");
        Assert.True(tail > 0 && Math.Sqrt(tailEnergy / tail) < .001, "音轨结束后必须补静音。");
    }

    [Fact]
    public async Task Probe虚高时长仍以完整解码采样报告实际短音轨()
    {
        using TemporaryDirectory directory = new(); string source = await EncodeAsync(directory, "mp3", 48000, 1, false);
        string json = JsonSerializer.Serialize(new { streams = new[] { new { codec_name = "mp3", sample_rate = 48000, channels = 1, duration = "1000" } },
            format = new { format_name = "mp3", duration = "1000" } });
        string probe = CreatePythonScript(directory, "dishonest-probe", $$"""
            import subprocess, sys
            if {{JsonSerializer.Serialize(source)}} in sys.argv:
                print({{JsonSerializer.Serialize(json)}})
            else:
                sys.exit(subprocess.call([{{JsonSerializer.Serialize(TestMediaToolPaths.Ffprobe)}}, *sys.argv[1:]]))
            """);
        string candidate = directory.FilePath("padded.mp4"); var factory = new CountingFactory();
        var grant = await GrantAsync(source, "mp3");
        WorkerMessage result = await StaticExportTask.ExecuteAsync(Request(grant, candidate, duration: 1_000_000, ffprobe: probe), true,
            _ => Task.CompletedTask, factory, CancellationToken.None);
        var report = WorkerExportContract.DecodeReport(result.Payload, new("opaqueMp4", true, 8, 4, 30, 1, 30, 1_000_000, grant.AudioProfile));
        long samples = await new FfmpegPcmReader(TestMediaToolPaths.Ffmpeg).ReadMonoSamplesAtRateAsync(source, 48000, _ => ValueTask.CompletedTask);
        Assert.Equal((samples * 1_000_000 / 48000).ToString(CultureInfo.InvariantCulture), report.AudioDurationUs);
        Assert.Equal(1, factory.Created); Assert.Equal(30, factory.Frames); Assert.True(File.Exists(candidate));
    }

    [Fact]
    public async Task 完整FLAC头而损坏编码帧不发布部分成功也不创建候选()
    {
        using TemporaryDirectory directory = new(); string source = await EncodeAsync(directory, "flac", 48000, 1, false);
        byte[] bytes = await File.ReadAllBytesAsync(source); int offset = 4;
        while (true)
        {
            bool last = (bytes[offset] & 128) != 0;
            int length = bytes[offset + 1] << 16 | bytes[offset + 2] << 8 | bytes[offset + 3];
            offset += 4 + length; if (last) break;
        }
        Assert.True(bytes.Length - offset > 16); Array.Clear(bytes, offset, bytes.Length - offset); await File.WriteAllBytesAsync(source, bytes);
        var grant = await GrantAsync(source, "flac"); string candidate = directory.FilePath("must-not-exist.mp4"); var factory = new CountingFactory();
        var failure = await Assert.ThrowsAsync<RenderSessionFailureException>(() => StaticExportTask.ExecuteAsync(Request(grant, candidate), true,
            _ => Task.CompletedTask, factory, CancellationToken.None));
        Assert.True(failure.Code is "WorkerExportAudioUnsupported" or "WorkerExportAudioDecodeFailed");
        Assert.Equal(0, factory.Created); Assert.False(File.Exists(candidate));
    }

    [Fact]
    public async Task 解码完成后的同长度源替换在工厂前被重新SHA门禁拒绝()
    {
        using TemporaryDirectory directory = new(); string source = await EncodeAsync(directory, "flac", 48000, 1, false);
        var grant = await GrantAsync(source, "flac");
        string launcher;
        if (OperatingSystem.IsWindows())
        {
            launcher = CreatePythonScript(directory, "owned-decoder", $$"""
                import pathlib, subprocess, sys
                result = subprocess.call([{{JsonSerializer.Serialize(TestMediaToolPaths.Ffmpeg)}}, *sys.argv[1:]])
                p = pathlib.Path({{JsonSerializer.Serialize(source)}})
                b = p.read_bytes()
                p.write_bytes(b[:-1] + bytes([b[-1] ^ 1]))
                sys.exit(result)
                """);
        }
        else
        {
            launcher = directory.FilePath("owned-decoder.sh");
            string quotedSource = "'" + source.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
            await File.WriteAllTextAsync(launcher, "#!/bin/sh\n" + TestMediaToolPaths.Ffmpeg + " \"$@\"\nresult=$?\n"
                + TestMediaToolPaths.Python + " - " + quotedSource + " <<'PY'\nimport pathlib, sys\np=pathlib.Path(sys.argv[1]); b=p.read_bytes(); p.write_bytes(b[:-1]+bytes([b[-1]^1]))\nPY\nexit \"$result\"\n");
            Executable(launcher);
        }
        string candidate = directory.FilePath("must-not-exist.mp4"); var factory = new CountingFactory();
        var failure = await Assert.ThrowsAsync<RenderSessionFailureException>(() => StaticExportTask.ExecuteAsync(Request(grant, candidate, ffmpeg: launcher), true,
            _ => Task.CompletedTask, factory, CancellationToken.None));
        Assert.Equal("WorkerExportAudioMismatch", failure.Code); Assert.Equal(grant.Size, new FileInfo(source).Length);
        Assert.Equal(0, factory.Created); Assert.False(File.Exists(candidate));
    }

    [Theory]
    [InlineData("aac", "mp3", 48000, 1)]
    [InlineData("mp3", "mp3", 192001, 1)]
    [InlineData("flac", "flac", 48000, 3)]
    [InlineData("flac", "mov", 48000, 2)]
    public void 真实Probe不能把其他codec容器或流属性带入压缩执行(string codec, string container, int rate, int channels)
        => Assert.Equal("WorkerExportAudioUnsupported", Assert.Throws<RenderSessionFailureException>(() =>
            StaticExportTask.ValidateCompressedProbe(new(99_000_000, rate, channels, codec, container), codec)).Code);

    [Theory]
    [InlineData("mov,mp4,m4a,3gp,3g2,mj2", true)]
    [InlineData("mov,mp4", true)]
    [InlineData("mov,foreign", false)]
    [InlineData("mov,,mp4", false)]
    public void MOV_probe容器列表只接受完整允许成员(string container, bool accepted)
    {
        var info = new AudioStreamInfo(99_000_000, 48000, 2, "aac", container);
        if (accepted)
        {
            StaticExportTask.ValidateCompressedProbe(info, "mov");
            return;
        }
        Assert.Equal("WorkerExportAudioUnsupported", Assert.Throws<RenderSessionFailureException>(() =>
            StaticExportTask.ValidateCompressedProbe(info, "mov")).Code);
    }

    [Fact]
    public async Task 采样预算按实际源率在下一块计入前拒绝而不是截成成功()
    {
        var reader = new SyntheticReader([.. Enumerable.Repeat(16000, 900), 1]);
        var failure = await Assert.ThrowsAsync<RenderSessionFailureException>(() =>
            StaticExportTask.CountCompressedSamplesAsync(reader, "/owned/source", 48000, CancellationToken.None));
        Assert.Equal("WorkerExportAudioBudgetExceeded", failure.Code); Assert.Equal(900, reader.AcceptedBlocks);
        Assert.Equal(48000, reader.RequestedRate);
    }

    [Fact]
    public async Task 完整EOF计数不符或解码部分后退出失败不能伪成可用()
    {
        foreach (var reader in new[] { new SyntheticReader([16], returned: 15), new SyntheticReader([16], failed: true), new SyntheticReader([]) })
            Assert.Equal("WorkerExportAudioDecodeFailed", (await Assert.ThrowsAsync<RenderSessionFailureException>(() =>
                StaticExportTask.CountCompressedSamplesAsync(reader, "/owned/source", 44100, CancellationToken.None))).Code);
    }

    [Fact]
    public async Task 解码取消保持OCE而不伪成损坏或部分成功()
    {
        using CancellationTokenSource cancellation = new(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => StaticExportTask.CountCompressedSamplesAsync(
            new SyntheticReader([16]), "/owned/source", 44100, cancellation.Token));
    }

    [Fact]
    public async Task 真工具能力列出固定六个音频与两个执行profile()
    {
        Guid task = Guid.NewGuid(); var metadata = new WorkerExportMetadata(WorkerExportContract.ExecutionProfile,
            BackendSnapshotProfiles.StaticLyricsScene, "opaqueMp4", false, null, null, TestMediaToolPaths.Ffmpeg, TestMediaToolPaths.Ffprobe);
        byte[] payload = WorkerExportContract.EncodeInput(task, metadata, []);
        var request = new WorkerMessage(WorkerExportContract.CapabilitiesKind, "caps", WorkerProtocol.ComputeSha256(payload), payload,
            taskId: task.ToString("N"), sourceSha256: WorkerProtocol.ComputeSha256([]), exportFormat: "opaqueMp4", exportIncludeAudio: false);
        var result = await StaticExportTask.ExecuteAsync(request, true, _ => Task.CompletedTask, CancellationToken.None);
        var caps = WorkerExportContract.DecodeJson<WorkerExportCapabilitiesDto>(result.Payload, 4096);
        Assert.True(caps.OpaqueMp4); Assert.True(caps.TransparentProRes4444);
        Assert.Equal(new[] { BackendAudioInputProfiles.Pcm16Wave, BackendAudioInputProfiles.Mp3, BackendAudioInputProfiles.Flac,
            BackendAudioInputProfiles.Aac, BackendAudioInputProfiles.Mov, BackendAudioInputProfiles.Ogg }, caps.AudioProfiles);
        Assert.Equal(new[] { WorkerExportContract.ExecutionProfile, WorkerExportContract.CompressedExecutionProfile }, caps.ExecutionProfiles);
        Assert.Equal(StaticProjectSnapshotCodec.EngineIdentity, caps.EngineIdentity);
    }

    private static WorkerMessage Request(WorkerExportAudioGrant grant, string candidate, string format = "opaqueMp4", long duration = 200_000,
        string? ffmpeg = null, string? ffprobe = null)
    {
        ffmpeg ??= TestMediaToolPaths.Ffmpeg;
        ffprobe ??= TestMediaToolPaths.Ffprobe;
        var project = Project.Create("compressed export", Composition.Create(8, 4, FrameRate.Fps30, outputDurationUs: duration),
            MakoKaraGenerator.Core.Lyrics.LyricsDocument.Create([]), audioPath: "/declaration-only-source")
            with { AudioMedia = new(grant.Sha256, grant.Size, "preserved original name") };
        byte[] bytes = StaticProjectSnapshotCodec.Encode(ProjectSnapshot.FromProject(project)); Guid task = Guid.NewGuid();
        var metadata = new WorkerExportMetadata(WorkerExportContract.GetExecutionProfile(grant.AudioProfile), BackendSnapshotProfiles.StaticLyricsScene,
            format, true, candidate, grant, ffmpeg, ffprobe);
        byte[] payload = WorkerExportContract.EncodeInput(task, metadata, bytes);
        return new(WorkerExportContract.RequestKind, "compressed", WorkerProtocol.ComputeSha256(payload), payload,
            taskId: task.ToString("N"), sourceSha256: WorkerProtocol.ComputeSha256(bytes), exportFormat: format, exportIncludeAudio: true);
    }
    private static async Task<WorkerExportAudioGrant> GrantAsync(string path, string format) => new(path, new FileInfo(path).Length,
        Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(path))), format switch
        {
            "mp3" => BackendAudioInputProfiles.Mp3, "flac" => BackendAudioInputProfiles.Flac,
            "aac" => BackendAudioInputProfiles.Aac, "mov" => BackendAudioInputProfiles.Mov,
            "ogg" => BackendAudioInputProfiles.Ogg, _ => BackendAudioInputProfiles.Pcm16Wave,
        });
    private static async Task<string> EncodeAsync(TemporaryDirectory directory, string format, int rate, int channels, bool variable)
    {
        string path = directory.FilePath("source." + format);
        var start = new ProcessStartInfo(TestMediaToolPaths.Ffmpeg) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
        foreach (string argument in new[] { "-v", "error", "-f", "lavfi", "-i", "sine=frequency=997:sample_rate=" + rate.ToString(CultureInfo.InvariantCulture),
            "-t", "0.213", "-ac", channels.ToString(CultureInfo.InvariantCulture), "-c:a", format switch
            { "mp3" => "libmp3lame", "flac" => "flac", "aac" or "mov" => "aac", "ogg" => "libvorbis", _ => "pcm_s16le" } }) start.ArgumentList.Add(argument);
        if (format == "mp3") { start.ArgumentList.Add(variable ? "-q:a" : "-b:a"); start.ArgumentList.Add(variable ? "4" : "128k"); }
        start.ArgumentList.Add(path);
        using Process process = Process.Start(start)!; Task<string> stderr = process.StandardError.ReadToEndAsync(); Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync(); Assert.True(process.ExitCode == 0, await stderr); await stdout; return path;
    }
    private static string CreatePythonScript(TemporaryDirectory directory, string name, string pythonSource)
    {
        if (OperatingSystem.IsWindows())
        {
            string pyPath = directory.FilePath(name + ".py");
            File.WriteAllText(pyPath, pythonSource);
            string cmdPath = directory.FilePath(name + ".cmd");
            File.WriteAllText(cmdPath, string.Create(CultureInfo.InvariantCulture, $"@echo off\r\n\"{TestMediaToolPaths.Python}\" \"{pyPath}\" %*\r\n"));
            return cmdPath;
        }
        string scriptPath = directory.FilePath(name);
        File.WriteAllText(scriptPath, "#!/usr/bin/env python3\n" + pythonSource.Replace("\r\n", "\n", StringComparison.Ordinal));
        Executable(scriptPath);
        return scriptPath;
    }
    private static void Executable(string path)
    { if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
    private sealed class SyntheticReader(int[] blocks, long? returned = null, bool failed = false) : IResampledPcmSampleReader
    {
        public int AcceptedBlocks { get; private set; }
        public int RequestedRate { get; private set; }
        public Task<long> ReadMonoSamplesAsync(string filePath, Func<ReadOnlyMemory<float>, ValueTask> onSamples,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async Task<long> ReadMonoSamplesAtRateAsync(string filePath, int sampleRate, Func<ReadOnlyMemory<float>, ValueTask> onSamples,
            CancellationToken cancellationToken = default)
        {
            RequestedRate = sampleRate; long total = 0;
            foreach (int block in blocks) { await onSamples(new float[block]); total += block; AcceptedBlocks++; }
            if (failed) throw new IOException("Actual decoder exited after emitting partial samples.");
            return returned ?? total;
        }
    }
    private sealed class CountingFactory : IRenderFrameSessionFactory
    {
        public ProjectSnapshot? Snapshot { get; private set; }
        public int Created { get; private set; }
        public int Disposed { get; private set; }
        public int Frames { get; private set; }
        public ValueTask<IRenderFrameSession> CreateAsync(ProjectSnapshot snapshot, CancellationToken cancellationToken = default)
        { Snapshot = snapshot; Created++; return ValueTask.FromResult<IRenderFrameSession>(new Session(this)); }
        private sealed class Session(CountingFactory owner) : IRenderFrameSession
        {
            public ValueTask<RenderFrame> RenderAsync(RenderRequest request, CancellationToken cancellationToken = default)
            {
                owner.Frames++; var target = request.ResolveTarget();
                return ValueTask.FromResult(new RenderFrame(new byte[target.PixelWidth * target.PixelHeight * 4], target,
                    PixelFormat.Bgra8888Premultiplied, request.TimeUs));
            }
            public ValueTask DisposeAsync() { owner.Disposed++; return ValueTask.CompletedTask; }
        }
    }
}
