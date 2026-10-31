using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Media;

namespace MakoKaraGenerator.Backend.Hosting;

/// <summary>Host内共享、按完整峰值身份去重的缓存；只有会话binding可以读取，无按hash授权入口。</summary>
public sealed class WaveformCache
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly long _maximumBytes;
    private long _retainedBytes;

    public WaveformCache(long maximumBytes = WaveformLimits.MaxRetainedBytes)
    {
        if (maximumBytes is < 0 or > WaveformLimits.MaxRetainedBytes) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        _maximumBytes = maximumBytes;
    }

    /// <summary>实际持有的flat peak payload字节，不含元数据、临时prepare和已生成窗口响应。</summary>
    public long RetainedBytes { get { lock (_gate) return _retainedBytes; } }
    public int EntryCount { get { lock (_gate) return _entries.Count; } }

    internal Binding? TryRetain(LoadedWaveform waveform, string sourceSha256, string profile)
    {
        WaveformPeakPyramid peaks = waveform.Peaks;
        long length = 0;
        for (int level = 0; level < peaks.LevelCount; level++) length = checked(length + (long)peaks.BucketCount(level) * 8);
        if (length > _maximumBytes) return null;
        // Hash without allocating a second complete peak payload. Include interpretation as well as float bits.
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendString(hash, WaveformLimits.IdentityProfile); AppendString(hash, WaveformLimits.Format);
        AppendString(hash, profile); AppendString(hash, sourceSha256);
        AppendInteger(hash, peaks.SampleRate); AppendInteger(hash, peaks.SampleCount);
        AppendInteger(hash, peaks.BaseBucketSamples); AppendInteger(hash, waveform.Info.Channels);
        AppendInteger(hash, peaks.LevelCount);
        Span<byte> pair = stackalloc byte[8];
        for (int level = 0; level < peaks.LevelCount; level++)
        {
            AppendInteger(hash, (long)peaks.BaseBucketSamples << level);
            AppendInteger(hash, peaks.BucketCount(level));
            for (int bucket = 0; bucket < peaks.BucketCount(level); bucket++)
            {
                PeakRange value = peaks.GetBucketPeak(level, bucket);
                BinaryPrimitives.WriteSingleLittleEndian(pair, value.Min);
                BinaryPrimitives.WriteSingleLittleEndian(pair[4..], value.Max);
                hash.AppendData(pair);
            }
        }
        string key = Convert.ToHexStringLower(hash.GetHashAndReset());
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out Entry? entry))
            {
                if (length > _maximumBytes - _retainedBytes) return null;
                byte[] payload = new byte[checked((int)length)];
                int[] offsets = new int[peaks.LevelCount];
                WaveformLevelDto[] levels = new WaveformLevelDto[peaks.LevelCount];
                int offset = 0;
                for (int level = 0; level < peaks.LevelCount; level++)
                {
                    offsets[level] = offset;
                    levels[level] = new(level, ((long)peaks.BaseBucketSamples << level).ToString(CultureInfo.InvariantCulture), peaks.BucketCount(level));
                    for (int bucket = 0; bucket < peaks.BucketCount(level); bucket++)
                    {
                        PeakRange value = peaks.GetBucketPeak(level, bucket);
                        BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(offset, 4), value.Min);
                        BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(offset + 4, 4), value.Max);
                        offset += 8;
                    }
                }
                entry = new(key, "waveform-" + key, sourceSha256, profile, peaks.SampleRate, peaks.SampleCount,
                    peaks.DurationUs, waveform.Info.Channels, peaks.BaseBucketSamples, levels, offsets, payload);
                _entries.Add(key, entry);
                _retainedBytes += payload.Length;
            }
            entry.References++;
            return new(this, entry);
        }
    }

    private static void AppendInteger(IncrementalHash hash, long value)
    { Span<byte> bytes = stackalloc byte[8]; BinaryPrimitives.WriteInt64LittleEndian(bytes, value); hash.AppendData(bytes); }
    private static void AppendString(IncrementalHash hash, string value)
    { byte[] bytes = Encoding.UTF8.GetBytes(value); AppendInteger(hash, bytes.Length); hash.AppendData(bytes); }

    internal sealed record Entry(string Key, string Id, string SourceSha256, string Profile, int SampleRate,
        long SampleCount, long DurationUs, int SourceChannels, int BaseBucketSamples, WaveformLevelDto[] Levels,
        int[] Offsets, byte[] Payload)
    { internal int References; }

    internal sealed class Binding : IDisposable
    {
        private readonly WaveformCache _owner;
        private Entry? _entry;
        internal Binding(WaveformCache owner, Entry entry) => (_owner, _entry) = (owner, entry);

        internal ProjectWaveformResponse? Metadata(string sessionId)
        {
            lock (_owner._gate)
            {
                if (_entry is not { } e) return null;
                return new(sessionId, true, WaveformId: e.Id, SourceSha256: e.SourceSha256, Profile: e.Profile,
                    SampleRate: e.SampleRate, SampleCount: e.SampleCount.ToString(CultureInfo.InvariantCulture),
                    DurationUs: e.DurationUs.ToString(CultureInfo.InvariantCulture), SourceChannels: e.SourceChannels,
                    WaveformChannels: 1, BaseBucketSamples: e.BaseBucketSamples, Format: WaveformLimits.Format, Levels: [.. e.Levels]);
            }
        }

        internal BackendOperationResult<WaveformWindow> Read(string waveformId, int level, int start, int count)
        {
            lock (_owner._gate)
            {
                if (_entry is not { } e || waveformId != e.Id)
                    return BackendOperationResult<WaveformWindow>.Rejected(new(BackendErrorCode.ResourceNotFound, "波形身份不存在。", "waveformId"));
                string? invalid = level < 0 || level >= e.Levels.Length ? "level"
                    : count is < 1 or > WaveformLimits.MaxWindowBuckets ? "count"
                    : start < 0 || start > e.Levels[level].BucketCount - count ? "start" : null;
                if (invalid is not null)
                    return BackendOperationResult<WaveformWindow>.Rejected(new(BackendErrorCode.InvalidRequest, "波形窗口参数越界。", invalid));
                // The independent <=64KiB response can complete after close without retaining a full cache entry.
                byte[] bytes = e.Payload.AsSpan(e.Offsets[level] + start * 8, count * 8).ToArray();
                using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                AppendString(hash, e.Id); AppendInteger(hash, level); AppendInteger(hash, start); AppendInteger(hash, count);
                hash.AppendData(bytes);
                return BackendOperationResult<WaveformWindow>.Success(new(bytes, "\"" + Convert.ToHexStringLower(hash.GetHashAndReset()) + "\""));
            }
        }

        public void Dispose()
        {
            lock (_owner._gate)
            {
                if (_entry is not { } entry) return;
                _entry = null;
                if (--entry.References == 0)
                {
                    _owner._entries.Remove(entry.Key);
                    _owner._retainedBytes -= entry.Payload.Length;
                }
            }
        }
    }
}

/// <summary>独立有界窗口；只含本次响应字节，不引用完整缓存或会话。</summary>
public sealed record WaveformWindow(ReadOnlyMemory<byte> Bytes, string ETag);
