using System.Buffers.Binary;
using System.Text.Json;
using MakoKaraGenerator.Backend.Contracts;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

public sealed class BackendAudioInputClassifierTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MP3首帧有界ID3与footer候选不冒估时且恢复流位置(bool footer)
    {
        byte[] frame = Mp3(); byte[] header = [73, 68, 51, 4, 0, (byte)(footer ? 16 : 0), 0, 0, 0, 3];
        byte[] bytes = footer ? [.. header, 1, 2, 3, 51, 68, 73, .. header[3..], .. frame] : [.. header, 1, 2, 3, .. frame];
        using MemoryStream stream = new(bytes); stream.Position = 7;
        BackendAudioInputInfo info = BackendAudioInputClassifier.Classify(stream, bytes.Length);
        Assert.Equal(BackendAudioInputProfiles.Mp3, info.AudioProfile); Assert.Equal(44100, info.SampleRate); Assert.Equal(2, info.Channels);
        Assert.Null(info.SourceSampleCount); Assert.Null(info.SourceDurationUs); Assert.Equal(7, stream.Position);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(36_000)]
    public void nativeFLAC只解析必要STREAMINFO总采样字段不能作为精确时长(long declaredSamples)
    {
        byte[] bytes = Flac(declaredSamples); using MemoryStream stream = new(bytes);
        BackendAudioInputInfo info = BackendAudioInputClassifier.Classify(stream, bytes.Length);
        Assert.Equal(BackendAudioInputProfiles.Flac, info.AudioProfile); Assert.Equal(48000, info.SampleRate); Assert.Equal(2, info.Channels);
        Assert.Null(info.SourceSampleCount); Assert.Null(info.SourceDurationUs);
    }

    [Fact]
    public void WAV候选保持精确采样帧时长且预算与实际长度一致()
    {
        byte[] wave = Wave(); using MemoryStream stream = new(wave); stream.Position = 3;
        BackendAudioInputInfo info = BackendAudioInputClassifier.Classify(stream, wave.Length);
        Assert.Equal(BackendAudioInputProfiles.Pcm16Wave, info.AudioProfile); Assert.Equal(64, info.SourceSampleCount); Assert.Equal(8000, info.SourceDurationUs);
        Assert.Equal(3, stream.Position); Assert.Throws<WorkerProtocolException>(() => BackendAudioInputClassifier.Classify(stream, wave.Length + 1));
        Assert.Throws<WorkerProtocolException>(() => BackendAudioInputClassifier.Classify(stream, BackendAudioInputProfiles.MaximumAudioBytes + 1));
    }

    [Theory]
    [InlineData("m4aaac", BackendAudioInputProfiles.Mov, 1)]
    [InlineData("m4aaac", BackendAudioInputProfiles.Mov, 64)]
    [InlineData("m4aalac", BackendAudioInputProfiles.Mov, 1)]
    [InlineData("m4aalac", BackendAudioInputProfiles.Mov, 64)]
    [InlineData("oggvorbis", BackendAudioInputProfiles.Ogg, 1)]
    [InlineData("oggvorbis", BackendAudioInputProfiles.Ogg, 64)]
    [InlineData("oggopus", BackendAudioInputProfiles.Ogg, 1)]
    [InlineData("oggopus", BackendAudioInputProfiles.Ogg, 64)]
    public void 真实MOV与Ogg候选读取容器字段且分块读取后恢复位置(string fixture, string profile, int chunkSize)
    {
        byte[] bytes = Container(fixture);
        using ChunkedReadStream stream = new(bytes, chunkSize); stream.Position = 3;
        BackendAudioInputInfo info = BackendAudioInputClassifier.Classify(stream, bytes.Length);
        Assert.Equal(profile, info.AudioProfile);
        Assert.Equal(0, info.SampleRate); Assert.Equal(0, info.Channels);
        Assert.Null(info.SourceSampleCount); Assert.Null(info.SourceDurationUs);
        Assert.Equal(3, stream.Position);
    }

    [Theory]
    [InlineData("oggvorbis", 1)]
    [InlineData("oggvorbis", 64)]
    [InlineData("oggopus", 1)]
    [InlineData("oggopus", 64)]
    public void Ogg非零版本按实际输入拒绝且恢复位置(string fixture, int chunkSize)
    {
        byte[] bytes = Container(fixture); bytes[4] = 1;
        using ChunkedReadStream stream = new(bytes, chunkSize); stream.Position = 2;
        WorkerProtocolException failure = Assert.Throws<WorkerProtocolException>(() => BackendAudioInputClassifier.Classify(stream, bytes.Length));
        Assert.Equal("AudioInputUnsupported", failure.Code); Assert.Equal(2, stream.Position);
    }

    [Theory]
    [InlineData("oggvorbis", 4)]
    [InlineData("oggvorbis", 5)]
    [InlineData("oggvorbis", 26)]
    [InlineData("m4aaac", 4)]
    [InlineData("m4aaac", 7)]
    [InlineData("m4aaac", 8)]
    [InlineData("m4aaac", 15)]
    public void 不完整容器头拒绝且分块读取后恢复位置(string fixture, int length)
    {
        byte[] bytes = Container(fixture)[..length];
        using ChunkedReadStream stream = new(bytes, 1); stream.Position = 1;
        WorkerProtocolException failure = Assert.Throws<WorkerProtocolException>(() => BackendAudioInputClassifier.Classify(stream, bytes.Length));
        Assert.Equal("AudioInputUnsupported", failure.Code); Assert.Equal(1, stream.Position);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(11)]
    public void 短AAC候选不要求其他容器头字节且恢复位置(int length)
    {
        byte[] bytes = new byte[length]; bytes[0] = 255; bytes[1] = 241;
        using ChunkedReadStream stream = new(bytes, 1); stream.Position = 2;
        Assert.Equal(BackendAudioInputProfiles.Aac, BackendAudioInputClassifier.Classify(stream, bytes.Length).AudioProfile);
        Assert.Equal(2, stream.Position);
    }

    [Theory]
    [InlineData("oggvorbis")]
    [InlineData("m4aaac")]
    public void 容器头读取提前结束拒绝且恢复位置(string fixture)
    {
        byte[] bytes = Container(fixture);
        using EarlyEndStream stream = new(bytes[..4], bytes.Length); stream.Position = 1;
        WorkerProtocolException failure = Assert.Throws<WorkerProtocolException>(() => BackendAudioInputClassifier.Classify(stream, bytes.Length));
        Assert.Equal("AudioInputUnsupported", failure.Code); Assert.Equal(1, stream.Position);
    }

    [Theory]
    [InlineData("layer2")]
    [InlineData("free")]
    [InlineData("reservedRate")]
    [InlineData("shortFrame")]
    [InlineData("id3Size")]
    [InlineData("id3Flags")]
    [InlineData("id3Footer")]
    [InlineData("playlist")]
    [InlineData("flacBlock")]
    [InlineData("flacChannels")]
    [InlineData("flacRate")]
    public void 伪后缀和不支持的首帧或容器拒绝且保留流位置(string invalid)
    {
        byte[] bytes = invalid.StartsWith("flac", StringComparison.Ordinal) ? Flac() : Mp3();
        switch (invalid)
        {
            case "layer2": bytes[1] = 0xfd; break;
            case "free": bytes[2] = 0; break;
            case "reservedRate": bytes[2] |= 12; break;
            case "shortFrame": bytes = bytes[..20]; break;
            case "id3Size": bytes = [73, 68, 51, 4, 0, 0, 128, 0, 0, 0, .. bytes]; break;
            case "id3Flags": bytes = [73, 68, 51, 4, 0, 1, 0, 0, 0, 0, .. bytes]; break;
            case "id3Footer": bytes = [73, 68, 51, 4, 0, 16, 0, 0, 0, 0, .. bytes]; break;
            case "playlist": bytes = "#EXTM3U\nfile:///song.mp3\n"u8.ToArray(); break;
            case "flacBlock": bytes[4] = 1; break;
            case "flacChannels": bytes[20] |= 14; break;
            case "flacRate": bytes[18] = 255; break;
        }
        using MemoryStream stream = new(bytes); stream.Position = 2;
        Assert.Throws<WorkerProtocolException>(() => BackendAudioInputClassifier.Classify(stream, bytes.Length)); Assert.Equal(2, stream.Position);
    }

    [Theory]
    [InlineData(BackendAudioInputProfiles.LegacyAnalysisSource, null, true)]
    [InlineData(BackendAudioInputProfiles.LegacyAnalysisSource, BackendAudioInputProfiles.Mp3, false)]
    [InlineData(BackendAudioInputProfiles.CompressedAnalysisSource, null, false)]
    [InlineData(BackendAudioInputProfiles.CompressedAnalysisSource, BackendAudioInputProfiles.Pcm16Wave, false)]
    [InlineData(BackendAudioInputProfiles.CompressedAnalysisSource, BackendAudioInputProfiles.Flac, true)]
    [InlineData("future", BackendAudioInputProfiles.Mp3, false)]
    public void 来源与音频profile严格匹配而旧缺字段唯一映射WAV(string source, string? audio, bool valid) =>
        Assert.Equal(valid, BackendAudioInputProfiles.TryResolveAnalysisAudioProfile(source, audio, out _));

    [Fact]
    public void 默认序列化保持旧WAV输入实际wire而压缩明确格式并省略未知时长()
    {
        const string oldWire = """{"SourceKind":"project","SourceId":"session","SourceRevision":"0","SourceProfile":"pcm16-wave-analysis-v1","Audio":{"AssetId":"asset","SizeBytes":128,"Sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"},"SourceDurationUs":"8000"}""";
        BackendAnalysisJobInputDto old = new("project", "session", "0", BackendAudioInputProfiles.LegacyAnalysisSource, new("asset", 128, new string('a', 64)), "8000");
        Assert.Equal(oldWire, JsonSerializer.Serialize(old)); Assert.Equal(old, JsonSerializer.Deserialize<BackendAnalysisJobInputDto>(oldWire));
        BackendAnalysisJobInputDto compressed = old with { SourceProfile = BackendAudioInputProfiles.CompressedAnalysisSource,
            Audio = old.Audio with { AudioProfile = BackendAudioInputProfiles.Mp3 }, SourceDurationUs = null };
        string wire = JsonSerializer.Serialize(compressed); using JsonDocument json = JsonDocument.Parse(wire);
        Assert.False(json.RootElement.TryGetProperty("SourceDurationUs", out _));
        Assert.Equal(BackendAudioInputProfiles.Mp3, json.RootElement.GetProperty("Audio").GetProperty("AudioProfile").GetString());
        Assert.Equal(compressed, JsonSerializer.Deserialize<BackendAnalysisJobInputDto>(wire));
    }

    internal static byte[] Mp3()
    { byte[] bytes = new byte[417]; bytes[0] = 255; bytes[1] = 251; bytes[2] = 144; return bytes; }
    internal static byte[] Flac(long declaredSamples = 0)
    {
        byte[] bytes = new byte[43]; "fLaC"u8.CopyTo(bytes); bytes[4] = 128; bytes[7] = 34;
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(8), 16); BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(10), 4096);
        ulong packed = 48000UL << 44 | 1UL << 41 | 15UL << 36 | (ulong)declaredSamples;
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(18), packed); return bytes;
    }
    private static byte[] Container(string fixture) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
        "Fixtures", "AudioPreparationBaseline", fixture + ".input"));
    private sealed class ChunkedReadStream(byte[] bytes, int chunkSize) : MemoryStream(bytes)
    {
        public override int Read(Span<byte> buffer) => base.Read(buffer[..Math.Min(chunkSize, buffer.Length)]);
    }
    private sealed class EarlyEndStream(byte[] bytes, long reportedLength) : MemoryStream(bytes)
    {
        public override long Length => reportedLength;
    }
    private static byte[] Wave()
    {
        using MemoryStream stream = new(); using BinaryWriter writer = new(stream);
        writer.Write("RIFF"u8); writer.Write(164); writer.Write("WAVEfmt "u8); writer.Write(16); writer.Write((short)1); writer.Write((short)1);
        writer.Write(8000); writer.Write(16000); writer.Write((short)2); writer.Write((short)16); writer.Write("data"u8); writer.Write(128); writer.Write(new byte[128]); return stream.ToArray();
    }
}
