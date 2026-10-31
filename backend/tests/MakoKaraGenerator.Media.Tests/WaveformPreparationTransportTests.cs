using System.Globalization;
using System.Text.Json;

namespace MakoKaraGenerator.Media.Tests;

public sealed class WaveformPreparationTransportTests
{
    private static readonly int[] ExpectedBucketCounts = [3, 2, 1];
    [Fact]
    public void 完整奇数尾桶重建保精度超范围与负零且不借采样近似()
    {
        float negativeZero = BitConverter.Int32BitsToSingle(unchecked((int)0x80000000));
        float[] minimums = [negativeZero, -3.25f, .5f], maximums = [0f, 2.5f, 4f];
        var peaks = WaveformPeakPyramid.FromBaseBuckets(44100, 129, 64, minimums, maximums);
        Assert.Equal(129, peaks.SampleCount); Assert.Equal(2925, peaks.DurationUs);
        Assert.Equal(3, peaks.LevelCount); Assert.Equal(ExpectedBucketCounts, Enumerable.Range(0, 3).Select(peaks.BucketCount));
        Assert.Equal(unchecked((int)0x80000000), BitConverter.SingleToInt32Bits(peaks.GetBucketPeak(0, 0).Min));
        Assert.Equal(0, BitConverter.SingleToInt32Bits(peaks.GetBucketPeak(0, 0).Max));
        Assert.Equal(new PeakRange(-3.25f, 2.5f), peaks.GetBucketPeak(1, 0));
        Assert.Equal(new PeakRange(.5f, 4f), peaks.GetBucketPeak(1, 1));
        Assert.Equal(new PeakRange(-3.25f, 4f), peaks.GetBucketPeak(2, 0));
        minimums[0] = -9; maximums[0] = 9;
        Assert.Equal(0f, peaks.GetBucketPeak(0, 0).Max);
    }

    [Theory]
    [InlineData(0, 64, 1, 1)]
    [InlineData(48000, -1, 0, 0)]
    [InlineData(48000, 65, 1, 1)]
    [InlineData(48000, 64, 2, 1)]
    public void 无效来源采样或缺尾桶不生成波形(int rate, long samples, int minimumCount, int maximumCount)
        => Assert.ThrowsAny<ArgumentException>(() => WaveformPeakPyramid.FromBaseBuckets(rate, samples, 64,
            new float[minimumCount], new float[maximumCount]));

    [Theory]
    [InlineData(float.NaN, 0f)]
    [InlineData(0f, float.PositiveInfinity)]
    [InlineData(1f, -1f)]
    public void 无效峰值不静默归零(float minimum, float maximum)
        => Assert.Throws<ArgumentException>(() => WaveformPeakPyramid.FromBaseBuckets(48000, 1, 64, [minimum], [maximum]));

    [Fact]
    public async Task 受限完整结果恰一桶接受超一采样在Append前拒绝且native已排水()
    {
        if (!OperatingSystem.IsLinux()) return;
        using TemporaryDirectory temporary = new(); string input = temporary.FilePath("owned.input");
        await File.WriteAllBytesAsync(input, "owned"u8.ToArray());
        string probe = await ExecutableAsync(temporary, "probe", "print('{\"streams\":[{\"sample_rate\":\"48000\",\"channels\":3,\"codec_name\":\"pcm_f32le\"}],\"format\":{\"format_name\":\"wav\"}}')\n");
        string exact = await ExecutableAsync(temporary, "exact", "import struct,sys\nsys.stdout.buffer.write(struct.pack('<64f',*([1.25]*64)))\n");
        var accepted = await AudioWaveformLoader.CreateForSelfContainedFiles(1, exact, probe).LoadAsync(input);
        Assert.Equal(64, accepted!.Peaks.SampleCount); Assert.Equal(3, accepted.Info.Channels);
        Assert.Equal(new PeakRange(1.25f, 1.25f), accepted.Peaks.GetBucketPeak(0, 0));
        string pidFile = temporary.FilePath("pid");
        string oversized = await ExecutableAsync(temporary, "oversized", "import os,struct,sys,time\nopen(" + JsonSerializer.Serialize(pidFile)
            + ",'w').write(str(os.getpid()))\nsys.stdout.buffer.write(struct.pack('<65f',*([.25]*65)));sys.stdout.buffer.flush()\ntime.sleep(30)\n");
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(8));
        await Assert.ThrowsAsync<WaveformPeakBudgetExceededException>(() =>
            AudioWaveformLoader.CreateForSelfContainedFiles(1, oversized, probe).LoadAsync(input, cancellationToken: deadline.Token));
        int pid = int.Parse(await File.ReadAllTextAsync(pidFile), CultureInfo.InvariantCulture);
        Assert.False(Directory.Exists("/proc/" + pid.ToString(CultureInfo.InvariantCulture)));
        string ordinary = await ExecutableAsync(temporary, "ordinary", "import struct,sys\nsys.stdout.buffer.write(struct.pack('<65f',*([.25]*65)))\n");
        Assert.Equal(65, (await AudioWaveformLoader.CreateForSelfContainedFiles(ordinary, probe).LoadAsync(input))!.Peaks.SampleCount);
    }

    private static async Task<string> ExecutableAsync(TemporaryDirectory temporary, string name, string source)
    {
        string path = temporary.FilePath(name); await File.WriteAllTextAsync(path, "#!/usr/bin/env python3\n" + source);
        if (OperatingSystem.IsLinux()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }
}
