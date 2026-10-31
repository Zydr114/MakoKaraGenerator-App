using System.Text;
using CliWrap;

namespace MakoKaraGenerator.Media.Tests;

public sealed class RestrictedAudioInputTests
{
    [Theory]
    [InlineData("wav", "pcm_s16le", 44_100, 1)]
    [InlineData("mp3", "mp3", 44_100, 2)]
    [InlineData("flac", "flac", 48_000, 2)]
    public async Task 真实三格式受限解码和原自动探测得到完全相同的重采样字节(string format, string codec,
        int sourceRate, int channels)
    {
        using TemporaryDirectory directory = new();
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(20));
        string source = directory.FilePath("owned-source.wav");
        WavWriter.Write(source, WavWriter.BuildSegmentedTone(sourceRate, (.413, .7f), (.6, .2f)), sourceRate);
        string input = directory.FilePath("owned-audio.input");
        if (format == "wav") File.Copy(source, input);
        else
        {
            List<string> arguments = ["-v", "error", "-i", source, "-ac", "2", "-c:a",
                format == "mp3" ? "libmp3lame" : "flac"];
            if (format == "mp3") arguments.AddRange(["-b:a", "128k"]);
            arguments.AddRange(["-f", format, input]);
            StringBuilder errors = new();
            CommandResult generated = await Cli.Wrap(FfmpegPcmReader.DefaultExecutableName).WithArguments(arguments)
                .WithStandardErrorPipe(PipeTarget.ToStringBuilder(errors)).WithValidation(CommandResultValidation.None)
                .ExecuteAsync(deadline.Token);
            Assert.True(generated.ExitCode == 0, errors.ToString());
        }

        FfmpegMediaProbe probe = new();
        AudioStreamInfo? ordinaryInfo = await probe.ProbeAsync(input, deadline.Token);
        AudioStreamInfo? restrictedInfo = await probe.ProbeAsync(input, format, deadline.Token);
        Assert.NotNull(restrictedInfo);
        Assert.Equal(codec, restrictedInfo.CodecName);
        Assert.Equal(sourceRate, restrictedInfo.SampleRate);
        Assert.Equal(channels, restrictedInfo.Channels);
        Assert.Equal(ordinaryInfo, restrictedInfo);

        FfmpegPcmReader reader = new();
        using MemoryStream ordinaryBytes = new();
        using MemoryStream restrictedBytes = new();
        long ordinaryCount = await reader.ReadMonoSamplesAtRateAsync(input, 48_000,
            samples => AppendAsync(ordinaryBytes, samples), deadline.Token);
        long restrictedCount = await reader.ReadMonoSamplesAtRateAsync(input, 48_000, format,
            samples => AppendAsync(restrictedBytes, samples), deadline.Token);
        Assert.True(ordinaryCount > 48_000);
        Assert.Equal(ordinaryCount, restrictedCount);
        Assert.Equal(ordinaryCount * sizeof(float), restrictedBytes.Length);
        Assert.Equal(ordinaryBytes.ToArray(), restrictedBytes.ToArray());
    }

    private static ValueTask AppendAsync(MemoryStream output, ReadOnlyMemory<float> samples)
    {
        output.Write(WavWriter.ToFloat32LittleEndian(samples.Span));
        return ValueTask.CompletedTask;
    }
}
