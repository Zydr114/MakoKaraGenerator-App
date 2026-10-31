using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using CliWrap;

namespace MakoKaraGenerator.Media.Tests;

public sealed class RestrictedSourceRateTests
{
    [Theory]
    [InlineData("mp3", 44_100, 1)]
    [InlineData("mp3", 48_000, 2)]
    [InlineData("flac", 48_000, 2)]
    [InlineData("flac", 192_000, 1)]
    public async Task 完整受限源rate计数与未重采样解码逐字节一致而不依赖probe时长(string format, int rate, int channels)
    {
        using TemporaryDirectory directory = new();
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(20));
        int expectedCount = checked(rate * 3 + 17);
        string input = await CreateInputAsync(directory, format, rate, channels, expectedCount, deadline.Token);
        var info = await new FfmpegMediaProbe().ProbeAsync(input, format, deadline.Token);
        Assert.NotNull(info); Assert.Equal(rate, info.SampleRate); Assert.Equal(channels, info.Channels);
        Assert.Equal(format, info.CodecName); Assert.Equal(format, info.ContainerName);
        FfmpegPcmReader reader = new();
        using IncrementalHash originalHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using IncrementalHash restrictedHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long originalCount = await reader.ReadMonoSamplesAsync(input, samples => Hash(originalHash, samples), deadline.Token);
        long restrictedCount = await reader.ReadMonoSamplesAtRateAsync(input, info.SampleRate, format,
            samples => Hash(restrictedHash, samples), deadline.Token);
        if (format == "flac") Assert.Equal(expectedCount, originalCount);
        // MP3 encoder delay/padding metadata need not reproduce the pre-encoding sample count.
        // Only complete decoding of these same encoded bytes establishes its actual duration.
        Assert.True(originalCount > 0); Assert.Equal(originalCount, restrictedCount);
        Assert.Equal(originalHash.GetHashAndReset(), restrictedHash.GetHashAndReset());

        static ValueTask Hash(IncrementalHash hash, ReadOnlyMemory<float> samples)
        {
            Assert.InRange(samples.Length, 1, 64 * 1024 / sizeof(float));
            hash.AppendData(MemoryMarshal.AsBytes(samples.Span));
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task 显式受限探测不得把错误声明的WAV自动识别为FLAC()
    {
        using TemporaryDirectory directory = new();
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(20));
        string input = await CreateInputAsync(directory, "flac", 48_000, 1, 48_000, deadline.Token);
        FfmpegMediaProbe probe = new();
        AudioStreamInfo? ordinary = await probe.ProbeAsync(input, deadline.Token);
        Assert.NotNull(ordinary); Assert.Equal("flac", ordinary.ContainerName);
        await Assert.ThrowsAsync<IOException>(() => probe.ProbeAsync(input, "wav", deadline.Token));
    }

    [Fact]
    public async Task FLAC后段损坏即使已有完整前段样本也不得返回成功计数()
    {
        using TemporaryDirectory directory = new();
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(20));
        const int rate = 48_000;
        const int sourceCount = rate * 3;
        string input = await CreateInputAsync(directory, "flac", rate, 1, sourceCount, deadline.Token);
        byte[] bytes = await File.ReadAllBytesAsync(input, deadline.Token);
        StringBuilder packetJson = new();
        CommandResult packets = await Cli.Wrap(FfmpegMediaProbe.DefaultExecutableName)
            .WithArguments(["-v", "error", "-f", "flac", "-show_packets", "-show_entries", "packet=pos", "-of", "json", input])
            .WithStandardOutputPipe(PipeTarget.ToStringBuilder(packetJson)).ExecuteAsync(deadline.Token);
        Assert.Equal(0, packets.ExitCode);
        using JsonDocument positions = JsonDocument.Parse(packetJson.ToString());
        int lateFrame = int.Parse(positions.RootElement.GetProperty("packets").EnumerateArray().SkipLast(1).Last()
            .GetProperty("pos").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(lateFrame, bytes.Length / 2, bytes.Length - 6);
        // Reserved blocksize zero is a native frame-header error, unlike arbitrary CRC/data changes.
        bytes.AsSpan(lateFrame + 2, 4).Clear();
        await File.WriteAllBytesAsync(input, bytes, deadline.Token);
        long partial = 0;
        await Assert.ThrowsAsync<IOException>(() => new FfmpegPcmReader().ReadMonoSamplesAtRateAsync(input, rate, "flac", samples =>
        {
            partial += samples.Length; return ValueTask.CompletedTask;
        }, deadline.Token));
        Assert.InRange(partial, 1, sourceCount - 1);
    }

    private static async Task<string> CreateInputAsync(TemporaryDirectory directory, string format, int rate, int channels,
        int samples, CancellationToken token)
    {
        float[] sourceSamples = new float[samples];
        for (int index = 0; index < samples; index++) sourceSamples[index] = .6f * (float)Math.Sin(2 * Math.PI * 440 * index / rate);
        string source = directory.FilePath("source.wav"); WavWriter.Write(source, sourceSamples, rate);
        string input = directory.FilePath("owned.input");
        List<string> arguments = ["-v", "error", "-i", source, "-ac", channels.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-c:a", format == "mp3" ? "libmp3lame" : "flac"];
        if (format == "mp3") arguments.AddRange(["-b:a", "128k"]);
        arguments.AddRange(["-f", format, input]);
        StringBuilder errors = new();
        CommandResult generated = await Cli.Wrap(FfmpegPcmReader.DefaultExecutableName).WithArguments(arguments)
            .WithStandardErrorPipe(PipeTarget.ToStringBuilder(errors)).WithValidation(CommandResultValidation.None).ExecuteAsync(token);
        Assert.True(generated.ExitCode == 0, errors.ToString()); return input;
    }
}
