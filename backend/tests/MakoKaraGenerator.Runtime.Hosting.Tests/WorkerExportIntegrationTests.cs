using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using CliWrap;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Hosting;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Media;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Scripting;
using Xunit.Abstractions;

namespace MakoKaraGenerator.Runtime.Hosting.Tests;

public sealed class WorkerExportIntegrationTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("opaqueMp4", 1, 30, 1, null, false, "draft")]
    [InlineData("transparentProRes4444", 2, 30000, 1001, null, false, "high")]
    [InlineData("opaqueMp4", 1, 30, 1, "mp3", false, "high")]
    [InlineData("opaqueMp4", 2, 30, 1, "mp3", true, "draft")]
    [InlineData("opaqueMp4", 2, 30, 1, "flac", false, "standard")]
    [InlineData("transparentProRes4444", 2, 30000, 1001, "flac", false, "standard")]
    public async Task 独立真实Worker普通或透明短片保实际歌词像素与完整固定来源音轨(string format, int channels, int numerator, int denominator,
        string? compressed, bool variable, string quality)
    {
        using TemporaryDirectory temp = new(); string wav = temp.FilePath("audio.wav");
        await File.WriteAllBytesAsync(wav, Wave(channels, 3));
        string source = wav;
        if (compressed is not null)
        {
            source = temp.FilePath("audio." + compressed);
            List<string> arguments = ["-v", "error", "-i", wav, "-c:a", compressed == "mp3" ? "libmp3lame" : "flac"];
            if (compressed == "mp3") arguments.AddRange(variable ? ["-q:a", "4"] : ["-b:a", "128k"]);
            arguments.Add(source); Assert.Equal(0, (await Cli.Wrap("/usr/bin/ffmpeg").WithArguments(arguments).ExecuteAsync()).ExitCode);
        }
        var grant = new BackendExportAudioGrant(source, new FileInfo(source).Length, Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(source))),
            compressed == "mp3" ? BackendAudioInputProfiles.Mp3 : compressed == "flac" ? BackendAudioInputProfiles.Flac : BackendAudioInputProfiles.Pcm16Wave);
        var snapshot = Snapshot(new(numerator, denominator), audio: true);
        var plan = CompositionFramePlan.Create(2_000_000, snapshot.Composition.FrameRate);
        string candidate = temp.FilePath(format == "opaqueMp4" ? "candidate.mp4" : "candidate.mov");
        var executor = Executor(); var capabilities = await executor.ProbeAsync();
        Assert.True(capabilities.GuardAvailable); Assert.True(format == "opaqueMp4" ? capabilities.OpaqueMp4 : capabilities.TransparentProRes4444);
        List<BackendJobProgressDto> progress = [];
        ExportReportDto report;
        try { report = await executor.ExecuteAsync(snapshot, new(format, true, candidate, grant, quality), new InlineProgress(progress.Add)); }
        catch (RenderSessionFailureException failure) { output.WriteLine($"Real {format} failed code={failure.Code}; stages={string.Join(',', progress.Select(value => value.Stage))}"); throw; }
        Assert.True(report.StrictlyVerified); Assert.True(report.IsVerified);
        Assert.Equal(quality, report.RequestedQuality); Assert.Equal(quality, report.ActualQuality); Assert.Equal(plan.TotalFrames, report.ExpectedFrames);
        Assert.Equal(plan.TotalFrames, report.DecodedFrames); Assert.Equal(plan.TotalFrames, report.FramesWritten);
        long sourceSamples = await new FfmpegPcmReader("/usr/bin/ffmpeg").ReadMonoSamplesAtRateAsync(source, 44100, _ => ValueTask.CompletedTask);
        Assert.Equal((sourceSamples * 1_000_000 / 44100).ToString(CultureInfo.InvariantCulture), report.AudioDurationUs);
        Assert.Equal(0, report.FailedFrames); Assert.Equal(0, report.DroppedFrames); Assert.True(report.AudioIncluded);
        Assert.Equal(format != "opaqueMp4", report.HasAlpha); Assert.Equal(new FileInfo(candidate).Length, report.MediaSize);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(candidate))), report.Sha256);
        Assert.Empty(report.Diagnostics); Assert.DoesNotContain(report.Warnings, value => value.Contains(temp.Path, StringComparison.Ordinal));
        Assert.Equal("prepared", progress[^1].Stage); Assert.DoesNotContain(progress, value => value.Stage == "committing");
        Assert.Equal(plan.TotalFrames, progress[^1].CompletedFrames);
        byte[] actual = await FrameAsync(candidate, 15);
        var factory = new ProjectRenderSessionFactory(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"), temp.FilePath("unused-cache"),
            new FfmpegVideoFrameProvider(), ReplayReason.Initial);
        await using var session = await factory.CreateAsync(snapshot);
        using var expected = await session.RenderAsync(RenderRequest.At(snapshot, plan.FrameTimeUs(15)) with
        { Quality = RenderQuality.Final, TransparentBackground = format != "opaqueMp4" });
        Assert.Equal(expected.PixelMemory.Length, actual.Length);
        int glyph = 0, matching = 0, transparent = 0, semi = 0;
        ReadOnlyMemory<byte> reference = expected.PixelMemory;
        for (int i = 0; i < actual.Length; i += 4)
        {
            if (format != "opaqueMp4")
            {
                if (actual[i + 3] == 0) transparent++;
                if (actual[i + 3] is > 50 and < 230) semi++;
                if (reference.Span[i + 3] > 50) { glyph++; if (Math.Abs(actual[i + 3] - reference.Span[i + 3]) <= 8) matching++; }
            }
            else if (reference.Span[i + 2] > 100) { glyph++; if (actual[i + 2] > 100) matching++; }
        }
        Assert.True(glyph > 100); Assert.True(matching > glyph * .9, $"Native glyph coverage mismatch {matching}/{glyph}");
        if (format != "opaqueMp4") { Assert.True(transparent > 1000); Assert.True(semi > 100); }
        double energy = 0; long samples = 0;
        long decodedSamples = await new FfmpegPcmReader("/usr/bin/ffmpeg").ReadMonoSamplesAtRateAsync(candidate, 44100, data =>
        { foreach (float value in data.Span) energy += value * value; samples += data.Length; return ValueTask.CompletedTask; });
        Assert.Equal(samples, decodedSamples); Assert.True(Math.Sqrt(energy / samples) > .08, "Decoded actual output audio was silent.");
        long expectedSamples = plan.VideoDurationUs * 44100 / 1_000_000;
        Assert.InRange(decodedSamples, expectedSamples - 1024, expectedSamples + 1024);
        Assert.False(Directory.Exists(temp.FilePath("unused-cache")));
        output.WriteLine($"Real {format}: decodedFrames={report.DecodedFrames}, audioSamples={decodedSamples}, glyph={matching}/{glyph}, alpha={report.HasAlpha}, bytes={report.MediaSize}");
    }
    [Fact]
    public async Task 显式纯视频不会读取音频声明且取消真实导出后同执行器可再次工作()
    {
        using TemporaryDirectory temp = new(); var executor = Executor();
        var snapshot = Snapshot(FrameRate.Fps60, audio: false) with { Composition = Snapshot(FrameRate.Fps60, false).Composition with
            { CanvasWidth = 1920, CanvasHeight = 1080, OutputDurationUs = 10_000_000 } };
        using CancellationTokenSource cancellation = new(); int completed = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => executor.ExecuteAsync(snapshot,
            new("opaqueMp4", false, temp.FilePath("canceled.mp4"), null), new InlineProgress(value =>
            { if (value.Stage == "rendering") { completed = value.CompletedFrames; cancellation.Cancel(); } }), cancellation.Token));
        Assert.InRange(completed, 1, 599); Assert.False(File.Exists(temp.FilePath("canceled.mp4")));
        var pure = Snapshot(FrameRate.Fps30, audio: false);
        var report = await executor.ExecuteAsync(pure, new("opaqueMp4", false, temp.FilePath("pure.mp4"), null));
        Assert.True(report.StrictlyVerified); Assert.False(report.AudioIncluded); Assert.Null(report.AudioCodec); Assert.Null(report.ActualAudioDurationUs);
    }
    [Fact]
    public async Task 音频授权SHA或时长错配必须失败且不降级静音成功()
    {
        using TemporaryDirectory temp = new(); string wav = temp.FilePath("short.wav"); await File.WriteAllBytesAsync(wav, Wave(1, 1));
        var grant = new BackendExportAudioGrant(wav, new FileInfo(wav).Length, Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(wav))));
        var exception = await Assert.ThrowsAsync<RenderSessionFailureException>(() => Executor().ExecuteAsync(Snapshot(FrameRate.Fps30, true),
            new("opaqueMp4", true, temp.FilePath("short.mp4"), grant)));
        Assert.Equal("WorkerExportAudioTooShort", exception.Code); Assert.False(File.Exists(temp.FilePath("short.mp4")));
        var corrupt = grant with { Sha256 = new string('a', 64) };
        await File.WriteAllBytesAsync(wav, Wave(1, 3)); corrupt = corrupt with { Size = new FileInfo(wav).Length };
        exception = await Assert.ThrowsAsync<RenderSessionFailureException>(() => Executor().ExecuteAsync(Snapshot(FrameRate.Fps30, true),
            new("opaqueMp4", true, temp.FilePath("changed.mp4"), corrupt)));
        Assert.Equal("WorkerExportAudioMismatch", exception.Code); Assert.False(File.Exists(temp.FilePath("changed.mp4")));
    }
    private static WorkerBackendExportExecutor Executor()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "MakoKaraGenerator.slnx"))) root = root.Parent;
        string dll = Path.Combine(root!.FullName, "src", "MakoKaraGenerator.Backend.Worker", "bin", "Debug", "net10.0", "MakoKaraGenerator.Backend.Worker.dll");
        return new(new WorkerProcessOptions("dotnet", [dll]), "/usr/bin/ffmpeg", "/usr/bin/ffprobe",
            exportOptions: new BackendExportOptions { MaxMediaBytes = WorkerExportContract.MaximumMediaBytes });
    }
    private static ProjectSnapshot Snapshot(FrameRate rate, bool audio)
    {
        var lyrics = LyricsDocument.Create([LyricLine.Create("line", "KARAOKE", new(0, 2_000_000), TimingStatus.Edited,
            [LyricToken.Create("token", "KARAOKE", new(0, 2_000_000), TimingStatus.Edited)])]);
        var project = Project.Create("real export", Composition.Create(320, 180, rate, new(20, 40, 130, 255), 2_000_000), lyrics,
            lyricStyle: LyricStyle.Default with { FontFamilyName = "Noto Sans CJK SC", FontSizePx = 30,
                UnsungColor = new(240, 240, 240, 160), SungColor = new(240, 240, 240, 160) },
            audioPath: audio ? "/declarative-source-not-authorized.wav" : "/pure-video-must-not-read.wav");
        return ProjectSnapshot.FromProject(project);
    }
    private static byte[] Wave(int channels, int seconds)
    {
        const int rate = 44100; int length = rate * seconds * channels * 2; byte[] bytes = new byte[44 + length];
        "RIFF"u8.CopyTo(bytes); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4, 4), bytes.Length - 8); "WAVEfmt "u8.CopyTo(bytes.AsSpan(8));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16, 4), 16); BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(20, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(22, 2), (ushort)channels); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(24, 4), rate);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(28, 4), rate * channels * 2); BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(32, 2), (ushort)(channels * 2));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(34, 2), 16); "data"u8.CopyTo(bytes.AsSpan(36)); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(40, 4), length);
        for (int sample = 0; sample < rate * seconds; sample++) for (int channel = 0; channel < channels; channel++)
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(44 + (sample * channels + channel) * 2, 2), (short)(Math.Sin(sample * 2 * Math.PI * 440 / rate) * 10000));
        return bytes;
    }
    private static async Task<byte[]> FrameAsync(string path, int index)
    {
        using MemoryStream data = new(); using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(30));
        var result = await Cli.Wrap("/usr/bin/ffmpeg").WithArguments(["-v", "error", "-i", path, "-vf", $"select=eq(n\\,{index.ToString(CultureInfo.InvariantCulture)})",
            "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "bgra", "pipe:1"]).WithStandardOutputPipe(PipeTarget.ToStream(data))
            .ExecuteAsync(deadline.Token);
        Assert.Equal(0, result.ExitCode); return data.ToArray();
    }
    private sealed class InlineProgress(Action<BackendJobProgressDto> report) : IProgress<BackendJobProgressDto>
    { public void Report(BackendJobProgressDto value) => report(value); }
}
