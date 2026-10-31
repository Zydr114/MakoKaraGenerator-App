namespace MakoKaraGenerator.Media;

/// <summary>
/// 增量构建 <see cref="WaveformPeakPyramid"/>。
/// </summary>
/// <remarks>
/// <para>
/// 存在的理由是内存：一首 5 分钟 44.1 kHz 的单声道 float 采样约 52 MB，
/// 一小时的文件超过 600 MB。流式解码时边读边归并成峰值，
/// 就只保留金字塔本身（约为采样数的 1/32），不需要把整首歌留在内存里。
/// </para>
/// <para>
/// 本类型不是线程安全的：解码回调按顺序调用即可。
/// </para>
/// </remarks>
public sealed class WaveformPeakBuilder
{
    private readonly int _baseBucketSamples;
    private readonly List<float> _mins = [];
    private readonly List<float> _maxs = [];

    private float _bucketMin = float.PositiveInfinity;
    private float _bucketMax = float.NegativeInfinity;
    private int _bucketFill;
    private long _sampleCount;

    /// <summary>创建一个增量构建器。</summary>
    /// <param name="baseBucketSamples">第 0 层的桶大小（采样数），必须为正。</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="baseBucketSamples"/> 非正时抛出。</exception>
    public WaveformPeakBuilder(int baseBucketSamples = WaveformPeakPyramid.DefaultBaseBucketSamples)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(baseBucketSamples);
        _baseBucketSamples = baseBucketSamples;
    }

    /// <summary>已累计的采样数。</summary>
    public long SampleCount => _sampleCount;

    /// <summary>已完成的第 0 层桶数。</summary>
    public int CompletedBucketCount => _mins.Count;

    /// <summary>追加一块采样。</summary>
    /// <param name="samples">归一化到 <c>[-1, 1]</c> 的单声道采样。</param>
    /// <remarks>
    /// 非有限值（NaN / Infinity）按静音处理。若不处理，
    /// 一个 NaN 会让整段的 min/max 都变成 NaN，波形整条消失。
    /// </remarks>
    public void Append(ReadOnlySpan<float> samples)
    {
        foreach (float sample in samples)
        {
            float value = float.IsFinite(sample) ? sample : 0f;

            _bucketMin = Math.Min(_bucketMin, value);
            _bucketMax = Math.Max(_bucketMax, value);
            _bucketFill++;
            _sampleCount++;

            if (_bucketFill >= _baseBucketSamples)
            {
                FlushBucket();
            }
        }
    }

    /// <summary>完成构建。调用后本实例不可再使用。</summary>
    /// <param name="sampleRate">采样率（Hz），必须为正。</param>
    /// <returns>峰值金字塔。</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="sampleRate"/> 非正时抛出。</exception>
    public WaveformPeakPyramid Build(int sampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);

        // 最后不足一桶的采样也要保留：否则曲尾的波形会凭空消失。
        if (_bucketFill > 0)
        {
            FlushBucket();
        }

        return WaveformPeakPyramid.FromBaseBuckets(sampleRate, _sampleCount, _baseBucketSamples,
            System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_mins),
            System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_maxs));
    }

    private void FlushBucket()
    {
        if (_bucketFill == 0)
        {
            return;
        }

        _mins.Add(float.IsPositiveInfinity(_bucketMin) ? 0f : _bucketMin);
        _maxs.Add(float.IsNegativeInfinity(_bucketMax) ? 0f : _bucketMax);

        _bucketMin = float.PositiveInfinity;
        _bucketMax = float.NegativeInfinity;
        _bucketFill = 0;
    }
}
