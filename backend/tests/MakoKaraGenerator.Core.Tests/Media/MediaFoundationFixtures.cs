using MakoKaraGenerator.Core.Media;

namespace MakoKaraGenerator.Core.Tests.Media;

internal static class MediaFoundationFixtures
{
    internal static readonly MediaAbIdentityWriter Writer = new();
    internal static readonly MediaAbValueValidator Validator = new();
    internal static MediaDigest Hash(char digit) => new(new string(digit, 64));

    internal static MediaColorInterpretation Color(MediaNativePixelFormat format = MediaNativePixelFormat.Rgb24,
        MediaTransfer transfer = MediaTransfer.Srgb, MediaMatrix matrix = MediaMatrix.Rgb,
        MediaRange range = MediaRange.Full, MediaAlpha alpha = MediaAlpha.Opaque,
        MediaChromaLocation chroma = MediaChromaLocation.None, bool trustedMissing = false,
        MediaDigest? authority = null, MediaDigest? ownDigest = null)
    {
        var raw = new MediaColorInterpretation(transfer, matrix, range, alpha, chroma, format,
            trustedMissing, authority ?? Hash('0'), default);
        return new MediaColorInterpretation(transfer, matrix, range, alpha, chroma, format,
            trustedMissing, raw.AuthorityDigest, ownDigest ?? Writer.ComputeColorDigest(raw));
    }

    internal static MediaToolIdentity Tool(MediaDigest? ownDigest = null)
    {
        var raw = new MediaToolIdentity(Hash('0'), Hash('1'), Hash('2'), default);
        return new MediaToolIdentity(raw.Executables, raw.Dependencies, raw.NormalizedPipeline,
            ownDigest ?? Writer.ComputeToolDigest(raw));
    }

    internal static MediaProfileBundle Profile(MediaDigest tool, MediaDigest color, MediaDigest? ownDigest = null)
    {
        var raw = new MediaProfileBundle(MediaProfilesV1.Presentation, MediaProfilesV1.DecoderPipeline,
            MediaProfilesV1.Color, MediaProfilesV1.Statistics, MediaProfilesV1.Quantization,
            MediaProfilesV1.Codec, MediaProfilesV1.Query, MediaProfilesV1.Gpu, tool, color, default);
        return new MediaProfileBundle(raw.Presentation, raw.DecoderPipeline, raw.Color, raw.Statistics,
            raw.Quantization, raw.Codec, raw.Query, raw.Gpu, tool, color, ownDigest ?? Writer.ComputeProfileDigest(raw));
    }

    internal static MediaFilePresentationDescriptor Descriptor(MediaSourceKind kind = MediaSourceKind.Still,
        MediaColorInterpretation? color = null, MediaQuarterTurn rotation = MediaQuarterTurn.None,
        bool mirror = false, int sarNumerator = 16, int sarDenominator = 15,
        MediaDigest? ownDigest = null, int? canonicalWidth = null, RationalTimeBase? explicitTimeBase = null,
        bool omitVideoTimeBase = false)
    {
        bool swapped = rotation is MediaQuarterTurn.Clockwise90 or MediaQuarterTurn.Clockwise270;
        var raw = new MediaFilePresentationDescriptor(kind == MediaSourceKind.Video ? 2 : 0,
            720, 576, canonicalWidth ?? (swapped ? 576 : 720), swapped ? 720 : 576,
            explicitTimeBase ?? (kind == MediaSourceKind.Video && !omitVideoTimeBase ? new RationalTimeBase(1, 90000) : null),
            sarNumerator, sarDenominator, rotation, mirror, color ?? Color(), default);
        return new MediaFilePresentationDescriptor(raw.StreamIndex, raw.EncodedWidth, raw.EncodedHeight,
            raw.CanonicalWidth, raw.CanonicalHeight, raw.StreamTimeBase, raw.SarNumerator, raw.SarDenominator,
            raw.Rotation, raw.MirrorHorizontallyBeforeRotation, raw.Color, ownDigest ?? Writer.ComputeDescriptorDigest(raw));
    }

    internal static MediaFeatureFileSource Source(MediaSourceKind kind = MediaSourceKind.Still,
        MediaFilePresentationDescriptor? descriptor = null, MediaToolIdentity? tool = null,
        MediaProfileBundle? profile = null, MediaSourceKey? key = null)
    {
        descriptor ??= Descriptor(kind);
        tool ??= Tool();
        profile ??= Profile(tool.Digest, descriptor.Color.Digest);
        var raw = new MediaFeatureFileSource(default, kind, new MediaFileContent(Hash('3'), 12345), descriptor, tool, profile);
        return new MediaFeatureFileSource(key ?? Writer.ComputeSourceKey(raw), kind, raw.Content, descriptor, tool, profile);
    }

    internal static MediaArtifactIdentity Artifact(MediaFeatureFileSource source, long count = 1,
        long? duration = null, long? byteLength = null, MediaDigest? profileDigest = null) =>
        new(Hash('4'), byteLength ?? MediaTrackLayoutV1.GetArtifactBytes(source.Kind, count), MediaProfilesV1.Codec,
            source.Key, profileDigest ?? source.Profile.Digest, source.Presentation.Digest, Hash('5'), count, duration);
}
