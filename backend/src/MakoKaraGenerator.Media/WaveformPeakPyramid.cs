using System.Globalization;

namespace MakoKaraGenerator.Media;

/// <summary>
/// 一段采样区间内的峰值（最小值与最大值）。
/// </summary>
/// <param name="Min">最小值，范围 <c>[-1, 1]</c>。</param>
/// <param name="Max">最大值，范围 <c>[-1, 1]</c>。</param>
/// <remarks>
/// 波形绘制需要的是"这段区间内振幅的上下包络"，而不是平均值：
/// 平均值会把正负相消的信号画成一条直线，完全看不出声音。
/// </remarks>
public readonly record struct PeakRange(float Min, float Max)
{
    /// <summary>全零区间（静音或空区间）。</summary>
    public static PeakRange Silence { get; } = new(0f, 0f);

    /// <summary>该区间的最大绝对振幅。</summary>
    public float Amplitude => Math.Max(Math.Abs(Min), Math.Abs(Max));

    /// <summary>合并两个区间，取各自的极值。</summary>
    /// <param name="other">另一个区间。</param>
    /// <returns>合并后的区间。</returns>
    public PeakRange Union(PeakRange other) => new(Math.Min(Min, other.Min), Math.Max(Max, other.Max));

    /// <summary>以 <c>"[-0.12, 0.34]"</c> 形式返回，便于诊断。</summary>
    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"[{Min:0.###}, {Max:0.###}]");
}

/// <summary>
/// 波形峰值金字塔：多分辨率的振幅包络。
/// </summary>
/// <remarks>
/// <para>
/// docs/roadmap.md §8 的 <c>MEDIA-03</c> 要求"波形峰值聚合，时间轴缩放时不会生成过量数据"。
/// 做法是预先算好一组分辨率逐级减半的峰值数组：
/// 缩到整曲时读粗层（每层只有几十个桶），放到毫秒级时读细层。
/// 因此无论怎么缩放，每帧要遍历的桶数都在视口宽度量级。
/// </para>
/// <para>
/// <b>分辨率下限</b>：第 0 层的桶大小是 <see cref="BaseBucketSamples"/> 个采样。
/// 比它更窄的区间只能返回所在桶的峰值（会略有放大）。
/// 界面在超出该精度时应显式提示，而不是让用户误以为看到的是精确波形
/// （docs/development.md §10.3 对"预览质量"的要求同理）。
/// </para>
/// </remarks>
public sealed class WaveformPeakPyramid
{
    /// <summary>默认的第 0 层桶大小（采样数）。</summary>
    /// <remarks>
    /// 64 个采样在 44.1 kHz 下约 1.45 ms，足以支撑逐字打轴所需的精度；
    /// 同时把 5 分钟歌曲的第 0 层控制在约 20 万个桶（约 1.6 MB）。
    /// </remarks>
    public const int DefaultBaseBucketSamples = 64;

    private readonly float[][] _mins;
    private readonly float[][] _maxs;
    private readonly int[] _bucketCounts;

    private WaveformPeakPyramid(
        int sampleRate,
        long sampleCount,
        int baseBucketSamples,
        float[][] mins,
        float[][] maxs,
        int[] bucketCounts)
    {
        SampleRate = sampleRate;
        SampleCount = sampleCount;
        BaseBucketSamples = baseBucketSamples;
        _mins = mins;
        _maxs = maxs;
        _bucketCounts = bucketCounts;
    }

    /// <summary>采样率（Hz）。</summary>
    public int SampleRate { get; }

    /// <summary>总采样数。</summary>
    public long SampleCount { get; }

    /// <summary>第 0 层的桶大小（采样数）。</summary>
    public int BaseBucketSamples { get; }

    /// <summary>金字塔层数。</summary>
    public int LevelCount => _bucketCounts.Length;

    /// <summary>峰值数据的分辨率下限（微秒）。比它更窄的区间无法被精确表示。</summary>
    public long MinimumResolutionUs => SampleRate > 0
        ? (long)Math.Ceiling(BaseBucketSamples * 1_000_000d / SampleRate)
        : 0;

    /// <summary>整曲时长（微秒）。</summary>
    public long DurationUs => SampleRate > 0
        ? (long)Math.Round(SampleCount * 1_000_000d / SampleRate)
        : 0;

    /// <summary>从一段采样构建金字塔。</summary>
    /// <param name="samples">归一化到 <c>[-1, 1]</c> 的单声道采样。</param>
    /// <param name="sampleRate">采样率（Hz），必须为正。</param>
    /// <param name="baseBucketSamples">第 0 层的桶大小，必须为正。</param>
    /// <returns>峰值金字塔。</returns>
    /// <exception cref="ArgumentOutOfRangeException">采样率或桶大小非正时抛出。</exception>
    public static WaveformPeakPyramid Build(
        ReadOnlySpan<float> samples,
        int sampleRate,
        int baseBucketSamples = DefaultBaseBucketSamples)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(baseBucketSamples);

        WaveformPeakBuilder builder = new(baseBucketSamples);
        builder.Append(samples);
        return builder.Build(sampleRate);
    }

    /// <summary>Rebuild the original pyramid from exact base-bucket extrema without synthesizing PCM samples.</summary>
    /// <param name="sampleRate">Original source sample rate.</param>
    /// <param name="sampleCount">Complete decoded mono sample count.</param>
    /// <param name="baseBucketSamples">Original base-bucket size.</param>
    /// <param name="minimums">Exact minimum float values, including the final partial bucket.</param>
    /// <param name="maximums">Exact maximum float values, including the final partial bucket.</param>
    /// <returns>An independent pyramid using the same pairwise merge as the original builder.</returns>
    public static WaveformPeakPyramid FromBaseBuckets(int sampleRate, long sampleCount, int baseBucketSamples,
        ReadOnlySpan<float> minimums, ReadOnlySpan<float> maximums)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        ArgumentOutOfRangeException.ThrowIfNegative(sampleCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(baseBucketSamples);
        long expected = sampleCount / baseBucketSamples + (sampleCount % baseBucketSamples == 0 ? 0 : 1);
        if (minimums.Length != maximums.Length || minimums.Length != expected)
            throw new ArgumentException("Base-bucket count does not describe the complete decoded samples.");
        for (int index = 0; index < minimums.Length; index++)
            if (!float.IsFinite(minimums[index]) || !float.IsFinite(maximums[index]) || minimums[index] > maximums[index])
                throw new ArgumentException("Base-bucket extrema must be finite and ordered.");

        int levelCount = 1, count = minimums.Length;
        while (count > 1) { count = (count + 1) / 2; levelCount++; }
        float[][] mins = new float[levelCount][];
        float[][] maxs = new float[levelCount][];
        int[] bucketCounts = new int[levelCount];
        mins[0] = minimums.ToArray(); maxs[0] = maximums.ToArray(); bucketCounts[0] = minimums.Length;
        for (int level = 1; level < levelCount; level++)
        {
            int previousCount = bucketCounts[level - 1], mergedCount = (previousCount + 1) / 2;
            float[] levelMins = new float[mergedCount], levelMaxs = new float[mergedCount];
            for (int index = 0; index < mergedCount; index++)
            {
                int left = index * 2, right = left + 1;
                levelMins[index] = mins[level - 1][left]; levelMaxs[index] = maxs[level - 1][left];
                if (right < previousCount)
                {
                    levelMins[index] = Math.Min(levelMins[index], mins[level - 1][right]);
                    levelMaxs[index] = Math.Max(levelMaxs[index], maxs[level - 1][right]);
                }
            }
            mins[level] = levelMins; maxs[level] = levelMaxs; bucketCounts[level] = mergedCount;
        }
        return new(sampleRate, sampleCount, baseBucketSamples, mins, maxs, bucketCounts);
    }

    /// <summary>取某一层某个桶的峰值。</summary>
    /// <param name="level">层号。</param>
    /// <param name="bucketIndex">桶序号。</param>
    /// <returns>该桶的峰值；越界时返回静音。</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="level"/> 不在有效范围内时抛出。</exception>
    public PeakRange GetBucketPeak(int level, int bucketIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(level);

        if (level >= LevelCount)
        {
            throw new ArgumentOutOfRangeException(nameof(level), level, $"层号超出范围（共 {LevelCount} 层）。");
        }

        if (bucketIndex < 0 || bucketIndex >= _bucketCounts[level])
        {
            return PeakRange.Silence;
        }

        return new PeakRange(_mins[level][bucketIndex], _maxs[level][bucketIndex]);
    }

    /// <summary>取某一层的桶大小（采样数）。</summary>
    /// <param name="level">层号。</param>
    /// <returns>该层的桶大小。</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="level"/> 不在有效范围内时抛出。</exception>
    public int BucketSamples(int level)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(level);

        if (level >= LevelCount)
        {
            throw new ArgumentOutOfRangeException(nameof(level), level, $"层号超出范围（共 {LevelCount} 层）。");
        }

        return checked(BaseBucketSamples << level);
    }

    /// <summary>取某一层的桶数量。</summary>
    /// <param name="level">层号。</param>
    /// <returns>该层桶的数量。</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="level"/> 不在有效范围内时抛出。</exception>
    public int BucketCount(int level)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(level);

        if (level >= LevelCount)
        {
            throw new ArgumentOutOfRangeException(nameof(level), level, $"层号超出范围（共 {LevelCount} 层）。");
        }

        return _bucketCounts[level];
    }

    /// <summary>
    /// 为给定的采样跨度挑选合适的层：不超过该跨度的最粗一层。
    /// </summary>
    /// <param name="sampleSpan">采样跨度。</param>
    /// <returns>层号；跨度不大于第 0 层桶大小时返回 0。</returns>
    /// <remarks>
    /// 选最粗的一层是为了让"每像素要遍历的桶数"保持在常数级，
    /// 这是缩放时不卡的关键。
    /// </remarks>
    public int SelectLevel(long sampleSpan)
    {
        if (sampleSpan <= BaseBucketSamples)
        {
            return 0;
        }

        int level = 0;

        while (level + 1 < LevelCount && BucketSamples(level + 1) <= sampleSpan)
        {
            level++;
        }

        return level;
    }

    /// <summary>
    /// 取一段采样区间的峰值，自动选择分辨率。
    /// </summary>
    /// <param name="startSample">区间起点（含）。</param>
    /// <param name="endSample">区间终点（不含）。</param>
    /// <returns>该区间的峰值。</returns>
    /// <remarks>
    /// 桶边界按采样 0 对齐，因此区间端点落在桶内部时会把该桶的完整极值算进来，
    /// 视觉上会略微放大。这对"字头对波形峰"的判断没有影响，
    /// 但意味着<b>不能用峰值反推精确采样值</b>。
    /// </remarks>
    public PeakRange GetPeak(long startSample, long endSample)
    {
        long clampedStart = Math.Clamp(startSample, 0, SampleCount);
        long clampedEnd = Math.Clamp(endSample, 0, SampleCount);

        if (clampedEnd <= clampedStart)
        {
            return PeakRange.Silence;
        }

        int level = SelectLevel(clampedEnd - clampedStart);
        int bucketSamples = BucketSamples(level);
        int firstBucket = (int)(clampedStart / bucketSamples);
        int lastBucket = (int)((clampedEnd - 1) / bucketSamples);

        float min = float.PositiveInfinity;
        float max = float.NegativeInfinity;

        for (int bucket = firstBucket; bucket <= lastBucket; bucket++)
        {
            if (bucket < 0 || bucket >= _bucketCounts[level])
            {
                continue;
            }

            min = Math.Min(min, _mins[level][bucket]);
            max = Math.Max(max, _maxs[level][bucket]);
        }

        return float.IsPositiveInfinity(min) ? PeakRange.Silence : new PeakRange(min, max);
    }

    /// <summary>以摘要形式返回，便于诊断与日志。</summary>
    public override string ToString() => string.Format(
        CultureInfo.InvariantCulture,
        "WaveformPeakPyramid({0} samples @ {1} Hz, {2} levels, base bucket {3})",
        SampleCount,
        SampleRate,
        LevelCount,
        BaseBucketSamples);
}
