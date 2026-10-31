using System.Buffers.Binary;

namespace MakoKaraGenerator.Backend.Contracts;

/// <summary>固定音频输入的纯BCL自闭包检查，不调用解码器、不解析任何外部路径。</summary>
public static class PcmWaveInput
{
    /// <summary>严格接受 RIFF PCM16 mono/stereo WAV，返回完整采样帧及其派生的整数时长。</summary>
    public static PcmWaveInputInfo Validate(Stream stream, long expectedSize)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead || !stream.CanSeek || expectedSize is < 44 or > BackendAudioInputProfiles.MaximumAudioBytes || stream.Length != expectedSize)
            throw Invalid();
        long previous = stream.Position;
        try
        {
            stream.Position = 0;
            Span<byte> header = stackalloc byte[16];
            stream.ReadExactly(header[..12]);
            if (!header[..4].SequenceEqual("RIFF"u8) || !header.Slice(8, 4).SequenceEqual("WAVE"u8)
                || (long)BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(4, 4)) + 8 != expectedSize) throw Invalid();
            int rate = 0, align = 0, channelCount = 0; long samples = 0; bool format = false, data = false;
            int chunks = 0;
            while (stream.Position < expectedSize)
            {
                if (++chunks > 4096 || expectedSize - stream.Position < 8) throw Invalid();
                stream.ReadExactly(header[..8]);
                uint length = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(4, 4));
                long end = stream.Position + length + (length & 1);
                if (end > expectedSize) throw Invalid();
                if (header[..4].SequenceEqual("fmt "u8))
                {
                    if (format || length is not (16 or 18)) throw Invalid();
                    format = true; stream.ReadExactly(header);
                    int tag = BinaryPrimitives.ReadUInt16LittleEndian(header);
                    int channels = BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(2, 2));
                    uint sampleRate = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(4, 4));
                    uint byteRate = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(8, 4));
                    align = BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(12, 2));
                    int bits = BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(14, 2));
                    if (tag != 1 || channels is not (1 or 2) || bits != 16 || sampleRate is < 1 or > 192000
                        || align != channels * 2 || byteRate != sampleRate * align) throw Invalid();
                    rate = (int)sampleRate; channelCount = channels;
                    if (length == 18)
                    { stream.ReadExactly(header[..2]); if (BinaryPrimitives.ReadUInt16LittleEndian(header) != 0) throw Invalid(); }
                }
                else if (header[..4].SequenceEqual("data"u8))
                {
                    if (!format || data || length == 0 || length % align != 0) throw Invalid();
                    data = true; samples = length / align;
                }
                stream.Position = end;
            }
            if (!format || !data || samples == 0) throw Invalid();
            return new(rate, channelCount, samples, samples * 1_000_000 / rate);
        }
        catch (EndOfStreamException) { throw Invalid(); }
        finally { stream.Position = previous; }
    }
    private static WorkerProtocolException Invalid() => new("固定音频仅支持完整自闭包 PCM16 单声道或双声道 WAV。", "PcmWaveInputUnsupported");
}

public sealed record PcmWaveInputInfo(int SampleRate, int Channels, long SampleCount, long DurationUs);
