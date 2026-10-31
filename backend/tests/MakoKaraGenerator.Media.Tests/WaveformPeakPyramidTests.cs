using MakoKaraGenerator.Media;

namespace MakoKaraGenerator.Media.Tests;

/// <summary>波形峰值金字塔的聚合与分辨率选择测试。</summary>
public sealed class WaveformPeakPyramidTests
{
    private const int SampleRate = 1000;

    [Fact]
    public void 单桶取最小与最大值()
    {
        float[] samples = [0.5f, -0.25f, 0.75f, -0.9f];

        WaveformPeakPyramid pyramid = WaveformPeakPyramid.Build(samples, SampleRate, baseBucketSamples: 4);

        Assert.Equal(-0.9f, pyramid.GetBucketPeak(0, 0).Min, 1e-6f);
        Assert.Equal(0.75f, pyramid.GetBucketPeak(0, 0).Max, 1e-6f);
    }

    [Fact]
    public void 多个桶互不影响()
    {
        float[] samples = [0.1f, 0.2f, 0.8f, 0.9f, -0.5f, -0.6f, 0.05f, 0.06f];

        WaveformPeakPyramid pyramid = WaveformPeakPyramid.Build(samples, SampleRate, baseBucketSamples: 2);

        Assert.Equal(4, pyramid.BucketCount(0));
        Assert.Equal(0.1f, pyramid.GetBucketPeak(0, 0).Min, 1e-6f);
        Assert.Equal(0.8f, pyramid.GetBucketPeak(0, 1).Min, 1e-6f);
        Assert.Equal(-0.6f, pyramid.GetBucketPeak(0, 2).Min, 1e-6f);
        Assert.Equal(0.06f, pyramid.GetBucketPeak(0, 3).Max, 1e-6f);
    }

    [Fact]
    public void 尾部的不足一桶也会被保留()
    {
        // 5 个采样、桶大小 2：不能丢掉最后一个采样，否则曲尾波形会凭空消失。
        float[] samples = [0.1f, 0.2f, 0.3f, 0.4f, 0.95f];

        WaveformPeakPyramid pyramid = WaveformPeakPyramid.Build(samples, SampleRate, baseBucketSamples: 2);

        Assert.Equal(5, pyramid.SampleCount);
        Assert.Equal(3, pyramid.BucketCount(0));
        Assert.Equal(0.95f, pyramid.GetBucketPeak(0, 2).Max, 1e-6f);
    }

    [Fact]
    public void 高层由低层两两归并()
    {
        float[] samples = [0.1f, 0.9f, -0.3f, -0.8f, 0.2f, 0.4f, -0.1f, -0.2f];

        WaveformPeakPyramid pyramid = WaveformPeakPyramid.Build(samples, SampleRate, baseBucketSamples: 2);

        Assert.Equal(4, pyramid.BucketCount(0));
        Assert.Equal(2, pyramid.BucketCount(1));
        Assert.Equal(1, pyramid.BucketCount(2));

        // 第 0 层：[0.1,0.9] [-0.8,-0.3] [0.2,0.4] [-0.2,-0.1]
        Assert.Equal(-0.8f, pyramid.GetBucketPeak(1, 0).Min, 1e-6f);
        Assert.Equal(0.9f, pyramid.GetBucketPeak(1, 0).Max, 1e-6f);
        Assert.Equal(-0.2f, pyramid.GetBucketPeak(1, 1).Min, 1e-6f);
        Assert.Equal(0.4f, pyramid.GetBucketPeak(1, 1).Max, 1e-6f);

        Assert.Equal(-0.8f, pyramid.GetBucketPeak(2, 0).Min, 1e-6f);
        Assert.Equal(0.9f, pyramid.GetBucketPeak(2, 0).Max, 1e-6f);
    }

    [Fact]
    public void 取任意区间的峰值()
    {
        float[] samples = new float[16];
        samples[3] = -0.7f;
        samples[10] = 0.85f;

        WaveformPeakPyramid pyramid = WaveformPeakPyramid.Build(samples, SampleRate, baseBucketSamples: 4);

        PeakRange range = pyramid.GetPeak(0, 16);

        Assert.Equal(-0.7f, range.Min, 1e-6f);
        Assert.Equal(0.85f, range.Max, 1e-6f);
        Assert.Equal(0.85f, range.Amplitude, 1e-6f);
    }

    [Fact]
    public void 空区间返回静音()
    {
        WaveformPeakPyramid pyramid = WaveformPeakPyramid.Build([0.5f, -0.5f], SampleRate);

        Assert.Equal(PeakRange.Silence, pyramid.GetPeak(1, 1));
        Assert.Equal(PeakRange.Silence, pyramid.GetPeak(5, 9));
        Assert.Equal(PeakRange.Silence, pyramid.GetPeak(-10, -5));
    }

    [Fact]
    public void 越界采样区间被钳制()
    {
        WaveformPeakPyramid pyramid = WaveformPeakPyramid.Build([0.5f, -0.6f, 0.7f, -0.8f], SampleRate, 2);

        PeakRange range = pyramid.GetPeak(1, 1_000_000);

        Assert.Equal(-0.8f, range.Min, 1e-6f);
        Assert.Equal(0.7f, range.Max, 1e-6f);
    }

    [Fact]
    public void 空输入不会崩()
    {
        WaveformPeakPyramid pyramid = WaveformPeakPyramid.Build([], SampleRate);

        Assert.Equal(0, pyramid.SampleCount);
        Assert.Equal(0, pyramid.BucketCount(0));
        Assert.Equal(PeakRange.Silence, pyramid.GetPeak(0, 100));
        Assert.Equal(0, pyramid.DurationUs);
    }

    [Fact]
    public void 非有限采样被当作静音()
    {
        // 一个 NaN 若被原样纳入聚合，整段会变成 NaN，波形整条消失。
        float[] samples = [0.5f, float.NaN, float.PositiveInfinity, -0.5f];

        WaveformPeakPyramid pyramid = WaveformPeakPyramid.Build(samples, SampleRate, 4);
        PeakRange range = pyramid.GetPeak(0, 4);

        Assert.Equal(-0.5f, range.Min, 1e-6f);
        Assert.Equal(0.5f, range.Max, 1e-6f);
        Assert.False(float.IsNaN(range.Min));
        Assert.False(float.IsNaN(range.Max));
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(64, 0)]
    [InlineData(65, 0)]     // 128 > 65，所以还停在第 0 层
    [InlineData(128, 1)]
    [InlineData(256, 2)]
    [InlineData(4096, 6)]   // 4096 采样、桶 64：最高层正好是 4096
    [InlineData(1_000_000, 6)]
    public void 分辨率选择取不超过跨度的最粗层(long span, int expectedLevel)
    {
        WaveformPeakPyramid pyramid = WaveformPeakPyramid.Build(new float[4096], SampleRate, 64);

        Assert.Equal(expectedLevel, pyramid.SelectLevel(span));
    }

    [Fact]
    public void 分辨率选择不会超出层数()
    {
        WaveformPeakPyramid pyramid = WaveformPeakPyramid.Build(new float[512], SampleRate, 64);

        int level = pyramid.SelectLevel(long.MaxValue);

        Assert.True(level < pyramid.LevelCount);
        Assert.True(pyramid.BucketCount(level) > 0);
    }

    [Fact]
    public void 桶大小按二倍增长()
    {
        WaveformPeakPyramid pyramid = WaveformPeakPyramid.Build(new float[1024], SampleRate, 64);

        Assert.Equal(64, pyramid.BucketSamples(0));
        Assert.Equal(128, pyramid.BucketSamples(1));
        Assert.Equal(256, pyramid.BucketSamples(2));
    }

    [Fact]
    public void 分辨率下限按采样率换算()
    {
        WaveformPeakPyramid pyramid = WaveformPeakPyramid.Build(new float[1024], 44_100, 64);

        // ceil(64 / 44100 * 1e6) = ceil(1451.24) = 1452 us
        Assert.Equal(1452, pyramid.MinimumResolutionUs);
    }

    [Fact]
    public void 时长由采样数推算()
    {
        WaveformPeakPyramid pyramid = WaveformPeakPyramid.Build(new float[44_100], 44_100);

        Assert.Equal(1_000_000, pyramid.DurationUs);
    }

    [Fact]
    public void 非法参数被拒绝()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => WaveformPeakPyramid.Build([], 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => WaveformPeakPyramid.Build([], 1000, 0));
    }

    [Fact]
    public void 越界层号被拒绝()
    {
        WaveformPeakPyramid pyramid = WaveformPeakPyramid.Build(new float[16], SampleRate, 4);

        Assert.Throws<ArgumentOutOfRangeException>(() => pyramid.BucketSamples(pyramid.LevelCount));
        Assert.Throws<ArgumentOutOfRangeException>(() => pyramid.GetBucketPeak(pyramid.LevelCount, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => pyramid.BucketCount(-1));
    }

    [Fact]
    public void 越界桶号返回静音()
    {
        WaveformPeakPyramid pyramid = WaveformPeakPyramid.Build(new float[16], SampleRate, 4);

        Assert.Equal(PeakRange.Silence, pyramid.GetBucketPeak(0, 999));
        Assert.Equal(PeakRange.Silence, pyramid.GetBucketPeak(0, -1));
    }

    [Fact]
    public void 增量构建与一次性构建结果一致()
    {
        float[] samples = WavWriter.BuildSegmentedTone(44_100, (0.5, 0.9f), (0.5, 0.2f));

        WaveformPeakPyramid oneShot = WaveformPeakPyramid.Build(samples, 44_100, 64);

        WaveformPeakBuilder builder = new(64);

        // 故意用不能整除桶大小的块来喂，验证跨块的部分桶处理。
        for (int offset = 0; offset < samples.Length; offset += 777)
        {
            int count = Math.Min(777, samples.Length - offset);
            builder.Append(samples.AsSpan(offset, count));
        }

        WaveformPeakPyramid incremental = builder.Build(44_100);

        Assert.Equal(oneShot.SampleCount, incremental.SampleCount);
        Assert.Equal(oneShot.LevelCount, incremental.LevelCount);

        for (int level = 0; level < oneShot.LevelCount; level++)
        {
            Assert.Equal(oneShot.BucketCount(level), incremental.BucketCount(level));

            for (int bucket = 0; bucket < oneShot.BucketCount(level); bucket++)
            {
                Assert.Equal(oneShot.GetBucketPeak(level, bucket), incremental.GetBucketPeak(level, bucket));
            }
        }
    }

    [Fact]
    public void 峰值区间可以合并()
    {
        PeakRange first = new(-0.2f, 0.4f);
        PeakRange second = new(-0.7f, 0.1f);

        Assert.Equal(new PeakRange(-0.7f, 0.4f), first.Union(second));
        Assert.Equal(0.7f, first.Union(second).Amplitude, 1e-6f);
    }
}
