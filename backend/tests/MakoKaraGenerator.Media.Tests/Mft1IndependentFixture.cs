using System.Buffers.Binary;
using System.Security.Cryptography;
using MakoKaraGenerator.Core.Media;

namespace MakoKaraGenerator.Media.Tests;

/// <summary>Deliberately handwritten wire fixture. It never invokes the production codec or its internal helpers.</summary>
internal sealed class Mft1IndependentFixture
{
    internal MediaFeatureFileSource Source { get; }
    internal byte[] Bytes { get; }
    internal long Count { get; }
    internal long FrameUs { get; }
    internal int GridOffset => 256 + checked((int)Count * 64);
    internal int PrefixOffset => GridOffset + checked((int)Count * 9216);
    internal long? ExplicitDurationUs { get; set; }
    internal long? Duration => Source.Kind == MediaSourceKind.Still ? null : ExplicitDurationUs ?? Count * FrameUs;
    internal MediaArtifactIdentity Identity => MakeIdentity();

    internal Mft1IndependentFixture(MediaSourceKind kind, int count = 1, long frameUs = 1000)
    {
        Count = count; FrameUs = frameUs; Source = MakeSource(kind);
        int prefixCount = kind == MediaSourceKind.Still ? 0 : (count + 63) / 64 + 1;
        Bytes = new byte[checked(256 + count * 64 + count * 9216 + prefixCount * 32272)];
        "MKMFTRK1"u8.CopyTo(Bytes);
        U32(8, 1); U32(12, 256); U32(16, (uint)kind);
        U16(24, 32); U16(26, 18); U16(28, 16); U16(30, 7);
        U32(32, 65535); U32(36, 64); U64(40, count); U64(48, Duration ?? 0);
        U64(56, 256); U64(64, count * 64); U64(72, GridOffset); U64(80, count * 9216);
        U64(88, PrefixOffset); U64(96, prefixCount * 32272); U64(104, prefixCount);
        Convert.FromHexString(Source.Key.Digest.LowerHex).CopyTo(Bytes, 112);
        Convert.FromHexString(Source.Profile.Digest.LowerHex).CopyTo(Bytes, 144);
        Convert.FromHexString(Source.Presentation.Digest.LowerHex).CopyTo(Bytes, 208);
        var sums = new ulong[7];
        int prefixOrdinal = 1;
        for (int frame = 0; frame < count; frame++)
        {
            int interval = 256 + frame * 64;
            if (kind == MediaSourceKind.Still) U32(interval + 48, 4);
            else
            {
                // Retain a signed negative absolute source origin and a reduced microsecond time base.
                I64(interval, -7_000_000 + frame * frameUs); I32(interval + 8, 1); I32(interval + 12, 1_000_000);
                I64(interval + 16, -7_000_000 + (frame + 1) * frameUs); I32(interval + 24, 1); I32(interval + 28, 1_000_000);
                U64(interval + 32, frame * frameUs); U64(interval + 40, (frame + 1) * frameUs);
                U32(interval + 48, frame == count - 1 ? 3u : 1u);
            }
            for (int cell = 0; cell < 576; cell++)
                for (int moment = 0; moment < 7; moment++)
                    U16(GridOffset + frame * 9216 + cell * 16 + moment * 2, Q(frame, moment));
            if (kind == MediaSourceKind.Video)
            {
                for (int moment = 0; moment < 7; moment++) sums[moment] += (ulong)Q(frame, moment) * (ulong)frameUs;
                if ((frame + 1) % 64 == 0 || frame + 1 == count)
                {
                    int prefix = PrefixOffset + prefixOrdinal++ * 32272;
                    U64(prefix, frame + 1); U64(prefix + 8, (frame + 1) * frameUs);
                    for (int cell = 0; cell < 576; cell++)
                        for (int moment = 0; moment < 7; moment++)
                            BinaryPrimitives.WriteUInt64LittleEndian(Bytes.AsSpan(prefix + 16 + (cell * 7 + moment) * 8), sums[moment]);
                }
            }
        }
        RefreshIndexHash();
    }

    internal static ushort Q(long frame, int moment) => (ushort)((frame * 313 + moment * 4001 + 101) % 65536);
    internal void RefreshIndexHash() => SHA256.HashData(Bytes.AsSpan(256, checked((int)Count * 64))).CopyTo(Bytes, 176);
    internal void U16(int at, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(Bytes.AsSpan(at), value);
    internal void U32(int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(Bytes.AsSpan(at), value);
    internal void U64(int at, long value) => BinaryPrimitives.WriteUInt64LittleEndian(Bytes.AsSpan(at), checked((ulong)value));
    internal void I32(int at, int value) => BinaryPrimitives.WriteInt32LittleEndian(Bytes.AsSpan(at), value);
    internal void I64(int at, long value) => BinaryPrimitives.WriteInt64LittleEndian(Bytes.AsSpan(at), value);

    internal MediaArtifactIdentity MakeIdentity(byte[]? bytes = null) => new(
        new(Convert.ToHexStringLower(SHA256.HashData(bytes ?? Bytes))), (bytes ?? Bytes).LongLength, MediaProfilesV1.Codec,
        Source.Key, Source.Profile.Digest, Source.Presentation.Digest,
        new(Convert.ToHexStringLower(Bytes.AsSpan(176, 32))), Count, Duration);

    internal static MediaFeatureFileSource MakeSource(MediaSourceKind kind)
    {
        var writer = new MediaAbIdentityWriter();
        var zero = new MediaDigest(new string('0', 64));
        var colorRaw = new MediaColorInterpretation(MediaTransfer.Srgb, MediaMatrix.Rgb, MediaRange.Full,
            MediaAlpha.Straight, MediaChromaLocation.None, MediaNativePixelFormat.Rgba32, false, zero, default);
        var color = new MediaColorInterpretation(colorRaw.Transfer, colorRaw.Matrix, colorRaw.Range, colorRaw.Alpha,
            colorRaw.ChromaLocation, colorRaw.NativePixelFormat, false, zero, writer.ComputeColorDigest(colorRaw));
        var toolRaw = new MediaToolIdentity(zero, zero, zero, default);
        var tool = new MediaToolIdentity(zero, zero, zero, writer.ComputeToolDigest(toolRaw));
        var descriptorRaw = new MediaFilePresentationDescriptor(0, 1, 1, 1, 1,
            kind == MediaSourceKind.Video ? new RationalTimeBase(1, 1_000_000) : null,
            1, 1, MediaQuarterTurn.None, false, color, default);
        var descriptor = new MediaFilePresentationDescriptor(0, 1, 1, 1, 1, descriptorRaw.StreamTimeBase,
            1, 1, MediaQuarterTurn.None, false, color, writer.ComputeDescriptorDigest(descriptorRaw));
        var profileRaw = new MediaProfileBundle(MediaProfilesV1.Presentation, MediaProfilesV1.DecoderPipeline,
            MediaProfilesV1.Color, MediaProfilesV1.Statistics, MediaProfilesV1.Quantization, MediaProfilesV1.Codec,
            MediaProfilesV1.Query, MediaProfilesV1.Gpu, tool.Digest, color.Digest, default);
        var profile = new MediaProfileBundle(profileRaw.Presentation, profileRaw.DecoderPipeline, profileRaw.Color,
            profileRaw.Statistics, profileRaw.Quantization, profileRaw.Codec, profileRaw.Query, profileRaw.Gpu,
            tool.Digest, color.Digest, writer.ComputeProfileDigest(profileRaw));
        var sourceRaw = new MediaFeatureFileSource(default, kind, new MediaFileContent(zero, 1), descriptor, tool, profile);
        return new(writer.ComputeSourceKey(sourceRaw), kind, sourceRaw.Content, descriptor, tool, profile);
    }
}
