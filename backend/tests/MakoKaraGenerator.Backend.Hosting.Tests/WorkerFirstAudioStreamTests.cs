using System.Buffers.Binary;
using System.Diagnostics;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Media;
using Xunit.Abstractions;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

public sealed class WorkerFirstAudioStreamTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 真实Worker双音轨第二默认和单音轨均保首轨完整时间基与全部峰值(bool secondDefault)
    {
        if (!OperatingSystem.IsLinux()) return;
        using TemporaryDirectory directory = new();
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(30));
        string audio = directory.FilePath("owned.mov");
        List<string> arguments = ["-v", "error", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=8000:duration=1"];
        if (secondDefault) arguments.AddRange(["-f", "lavfi", "-i", "sine=frequency=880:sample_rate=48000:duration=1"]);
        arguments.AddRange(["-map", "0:a:0"]);
        if (secondDefault) arguments.AddRange(["-map", "1:a:0"]);
        arguments.AddRange(["-c:a", "pcm_s16le"]);
        if (secondDefault) arguments.AddRange(["-ac:a:1", "2", "-disposition:a:0", "0", "-disposition:a:1", "default"]);
        arguments.Add(audio);
        _ = await Ffmpeg(arguments, deadline.Token);
        byte[] oracleBytes = await Ffmpeg(["-v", "error", "-i", audio, "-map", "0:a:0", "-vn", "-ac", "1", "-f", "f32le", "-acodec", "pcm_f32le", "-"], deadline.Token);
        float[] samples = new float[oracleBytes.Length / 4];
        for (int index = 0; index < samples.Length; index++) samples[index] = BinaryPrimitives.ReadSingleLittleEndian(oracleBytes.AsSpan(index * 4, 4));
        Assert.Equal(8000, samples.Length);
        WaveformPeakPyramid expected = WaveformPeakPyramid.Build(samples, 8000);
        string root = RepositoryRoot();
        string worker = Environment.GetEnvironmentVariable("MAKO_FIRST_STREAM_WORKER")
            ?? Path.Combine(root, "src/MakoKaraGenerator.Backend.Worker/bin/Debug/net10.0/MakoKaraGenerator.Backend.Worker");
        Assert.True(File.Exists(worker), worker);
        byte[] bytes = await File.ReadAllBytesAsync(audio, deadline.Token);
        WorkerBackendAudioPreparationExecutor executor = new(new(worker), "/usr/bin/ffmpeg", "/usr/bin/ffprobe");
        BackendAudioPreparationCapabilities capabilities = await executor.ProbeAsync(deadline.Token);
        Assert.True(capabilities.GuardAvailable, capabilities.FailureCode);
        Assert.True(capabilities.ToolStartupVerified, capabilities.FailureCode);
        LoadedWaveform actual = (await executor.ExecuteAsync(new(audio, bytes.LongLength, WorkerProtocol.ComputeSha256(bytes)), deadline.Token))!;
        output.WriteLine($"secondDefault={secondDefault}; Info={actual.Info}; SampleCount={actual.Peaks.SampleCount}; DurationUs={actual.Peaks.DurationUs}; oracleSamples={samples.Length}");
        Assert.Equal(8000, actual.Info.SampleRate);
        Assert.Equal(1, actual.Info.Channels);
        Assert.Equal("pcm_s16le", actual.Info.CodecName);
        Assert.Equal(1000000, actual.Info.DurationUs);
        Assert.Equal(await new FfmpegMediaProbe().ProbeAsync(audio, deadline.Token), actual.Info);
        LoadedWaveform direct = (await AudioWaveformLoader.CreateForSelfContainedFiles().LoadAsync(audio, cancellationToken: deadline.Token))!;
        Assert.Equal(actual.Info, direct.Info);
        foreach (WaveformPeakPyramid pyramid in new[] { actual.Peaks, direct.Peaks })
        {
            Assert.Equal(expected.SampleRate, pyramid.SampleRate);
            Assert.Equal(expected.SampleCount, pyramid.SampleCount);
            Assert.Equal(expected.DurationUs, pyramid.DurationUs);
            Assert.Equal(expected.LevelCount, pyramid.LevelCount);
            for (int level = 0; level < expected.LevelCount; level++)
            {
                Assert.Equal(expected.BucketSamples(level), pyramid.BucketSamples(level));
                Assert.Equal(expected.BucketCount(level), pyramid.BucketCount(level));
                for (int bucket = 0; bucket < expected.BucketCount(level); bucket++)
                {
                    PeakRange left = expected.GetBucketPeak(level, bucket), right = pyramid.GetBucketPeak(level, bucket);
                    Assert.Equal(BitConverter.SingleToInt32Bits(left.Min), BitConverter.SingleToInt32Bits(right.Min));
                    Assert.Equal(BitConverter.SingleToInt32Bits(left.Max), BitConverter.SingleToInt32Bits(right.Max));
                }
            }
        }
    }

    private static async Task<byte[]> Ffmpeg(IEnumerable<string> arguments, CancellationToken token)
    {
        ProcessStartInfo info = new("/usr/bin/ffmpeg") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        using Process process = Process.Start(info)!;
        using MemoryStream bytes = new();
        Task copy = process.StandardOutput.BaseStream.CopyToAsync(bytes, token);
        Task<string> errors = process.StandardError.ReadToEndAsync(token);
        try { await Task.WhenAll(copy, process.WaitForExitAsync(token)); }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); } }
        Assert.True(process.ExitCode == 0, await errors);
        return bytes.ToArray();
    }

    private static string RepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "MakoKaraGenerator.slnx"))) current = current.Parent;
        return current!.FullName;
    }
}
