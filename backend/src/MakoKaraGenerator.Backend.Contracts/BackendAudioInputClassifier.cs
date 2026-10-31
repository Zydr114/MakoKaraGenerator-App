using System.Buffers.Binary;

namespace MakoKaraGenerator.Backend.Contracts;

/// <summary>固定字节的有界候选分类；只检查容器／首帧，不代替Worker完整严格解码。</summary>
public static class BackendAudioInputClassifier
{
    public static BackendAudioInputInfo Classify(Stream stream, long expectedSize)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead || !stream.CanSeek || expectedSize is < 4 or > BackendAudioInputProfiles.MaximumAudioBytes
            || stream.Length != expectedSize) throw Invalid();
        long previous = stream.Position;
        try
        {
            stream.Position = 0; Span<byte> header = stackalloc byte[12];
            stream.ReadExactly(header[..4]);
            if (header[..4].SequenceEqual("RIFF"u8))
            {
                PcmWaveInputInfo wave = PcmWaveInput.Validate(stream, expectedSize);
                return new(BackendAudioInputProfiles.Pcm16Wave, wave.SampleRate, wave.Channels, wave.SampleCount, wave.DurationUs);
            }
            if (header[..4].SequenceEqual("fLaC"u8)) return ClassifyFlac(stream, expectedSize);
            if (header[..4].SequenceEqual("OggS"u8))
            {
                if (expectedSize < 27) throw Invalid();
                stream.ReadExactly(header.Slice(4, 1));
                if (header[4] != 0) throw Invalid();
                return new(BackendAudioInputProfiles.Ogg, 0, 0, null, null);
            }
            if (expectedSize >= 8)
            {
                stream.ReadExactly(header[4..8]);
                if (header[4..8].SequenceEqual("ftyp"u8))
                {
                    if (expectedSize < 16) throw Invalid();
                    return new(BackendAudioInputProfiles.Mov, 0, 0, null, null);
                }
            }
            long offset = 0;
            if (header[..3].SequenceEqual("ID3"u8))
            {
                stream.Position = 0; stream.ReadExactly(header[..10]);
                int version = header[3];
                int allowedFlags = version switch { 2 => 0xc0, 3 => 0xe0, 4 => 0xf0, _ => 0 };
                if (allowedFlags == 0 || header[4] == 255 || (header[5] & ~allowedFlags) != 0
                    || header[6..10].ContainsAnyInRange((byte)128, byte.MaxValue)) throw Invalid();
                int size = header[6] << 21 | header[7] << 14 | header[8] << 7 | header[9];
                offset = 10L + size;
                if (version == 4 && (header[5] & 0x10) != 0)
                {
                    if (offset > expectedSize - 10) throw Invalid();
                    stream.Position = offset; Span<byte> footer = stackalloc byte[10]; stream.ReadExactly(footer);
                    if (!footer[..3].SequenceEqual("3DI"u8) || !footer[3..].SequenceEqual(header[3..10])) throw Invalid();
                    offset += 10;
                }
            }
            if (offset > expectedSize - 4) throw Invalid();
            stream.Position = offset; stream.ReadExactly(header[..4]);
            uint frame = BinaryPrimitives.ReadUInt32BigEndian(header);
            if ((frame & 0xfff00000) == 0xfff00000 && ((frame >> 17) & 3) == 0) return new(BackendAudioInputProfiles.Aac, 0, 0, null, null);
            return ClassifyMp3(frame, expectedSize - offset);
        }
        catch (EndOfStreamException) { throw Invalid(); }
        finally { stream.Position = previous; }
    }

    private static BackendAudioInputInfo ClassifyFlac(Stream stream, long expectedSize)
    {
        if (expectedSize < 42) throw Invalid();
        Span<byte> block = stackalloc byte[38]; stream.ReadExactly(block);
        if ((block[0] & 0x7f) != 0 || block[1] != 0 || block[2] != 0 || block[3] != 34) throw Invalid();
        int minimum = BinaryPrimitives.ReadUInt16BigEndian(block[4..]);
        int maximum = BinaryPrimitives.ReadUInt16BigEndian(block[6..]);
        ulong values = BinaryPrimitives.ReadUInt64BigEndian(block[14..]);
        int rate = (int)(values >> 44), channels = (int)((values >> 41) & 7) + 1;
        int bits = (int)((values >> 36) & 31) + 1;
        if (minimum < 16 || maximum < minimum || rate is < 1 or > 192000 || channels is not (1 or 2) || bits < 4) throw Invalid();
        // STREAMINFO may declare unknown or dishonest total samples. Only full decoding establishes duration.
        return new(BackendAudioInputProfiles.Flac, rate, channels, null, null);
    }

    private static BackendAudioInputInfo ClassifyMp3(uint header, long remaining)
    {
        int version = (int)((header >> 19) & 3), layer = (int)((header >> 17) & 3);
        int bitrateIndex = (int)((header >> 12) & 15), rateIndex = (int)((header >> 10) & 3);
        if ((header & 0xffe00000) != 0xffe00000 || version == 1 || layer != 1 || bitrateIndex is 0 or 15 || rateIndex == 3) throw Invalid();
        int rate = rateIndex switch { 0 => 44100, 1 => 48000, _ => 32000 };
        if (version == 2) rate /= 2;
        else if (version == 0) rate /= 4;
        ReadOnlySpan<int> bitrate = version == 3 ? [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320]
            : [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160];
        int length = (version == 3 ? 144000 : 72000) * bitrate[bitrateIndex] / rate + (int)((header >> 9) & 1);
        if (length < 4 || length > remaining) throw Invalid();
        return new(BackendAudioInputProfiles.Mp3, rate, ((header >> 6) & 3) == 3 ? 1 : 2, null, null);
    }
    private static WorkerProtocolException Invalid() => new("固定音频不是受支持的完整WAV或MP3／FLAC候选。", "AudioInputUnsupported");
}

public sealed record BackendAudioInputInfo(string AudioProfile, int SampleRate, int Channels, long? SourceSampleCount, long? SourceDurationUs);
