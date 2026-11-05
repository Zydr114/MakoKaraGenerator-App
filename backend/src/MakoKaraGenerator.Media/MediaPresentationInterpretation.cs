using System.Security.Cryptography;
using System.Text;
using MakoKaraGenerator.Core.Media;

namespace MakoKaraGenerator.Media;

internal static class MediaPresentationInterpretation
{
    internal static MediaFeatureFileSource Create(MediaFileContent content, MediaPresentationInspectionRequest request,
        MediaNativePresentationMetadata frame, MediaStillStructure? still, MediaToolIdentity tool)
    {
        Need(frame.Ordinal == 0 && frame.StreamIndex == request.StreamIndex, "Initial decoded frame does not bind the selected stream and presentation ordinal.");
        if (still is not null) Need(frame.Family == still.Family && frame.Width == still.Width && frame.Height == still.Height, "Decoded still dimensions or format disagree with source structure.");
        MediaColorInterpretation color = Color(frame, still, request.MissingTagOverride);
        (MediaQuarterTurn rotation, bool mirror) = Geometry(frame, still);
        (int sarN, int sarD) = Sar(frame, still);
        MediaRasterGeometry geometry = MediaRasterGeometry.Create(frame.Width, frame.Height, sarN, sarD, rotation, mirror);
        var writer = new MediaAbIdentityWriter();
        var preliminary = new MediaFilePresentationDescriptor(request.StreamIndex, frame.Width, frame.Height, geometry.CanonicalWidth, geometry.CanonicalHeight,
            request.Kind == MediaSourceKind.Video ? frame.Pts.TimeBase : null, sarN, sarD, rotation, mirror, color, default);
        var descriptor = new MediaFilePresentationDescriptor(preliminary.StreamIndex, preliminary.EncodedWidth, preliminary.EncodedHeight,
            preliminary.CanonicalWidth, preliminary.CanonicalHeight, preliminary.StreamTimeBase, sarN, sarD, rotation, mirror, color, writer.ComputeDescriptorDigest(preliminary));
        var profile0 = new MediaProfileBundle(MediaProfilesV1.Presentation, MediaProfilesV1.DecoderPipeline, MediaProfilesV1.Color, MediaProfilesV1.Statistics,
            MediaProfilesV1.Quantization, MediaProfilesV1.Codec, MediaProfilesV1.Query, MediaProfilesV1.Gpu, tool.Digest, color.Digest, default);
        var profile = new MediaProfileBundle(profile0.Presentation, profile0.DecoderPipeline, profile0.Color, profile0.Statistics, profile0.Quantization, profile0.Codec,
            profile0.Query, profile0.Gpu, profile0.DecoderIdentity, profile0.ColorInterpretation, writer.ComputeProfileDigest(profile0));
        var source0 = new MediaFeatureFileSource(default, request.Kind, content, descriptor, tool, profile);
        var source = new MediaFeatureFileSource(writer.ComputeSourceKey(source0), request.Kind, content, descriptor, tool, profile);
        new MediaAbValueValidator().ValidateSource(source); return source;
    }

    internal static void VerifyFrame(MediaFeatureFileSource source, MediaNativePresentationMetadata frame, MediaStillStructure? still)
    {
        MediaColorInterpretation known = source.Presentation.Color;
        TrustedMediaColorInterpretation? authority = known.FromTrustedMissingTagOverride
            ? new(known.Transfer, known.Matrix, known.Range, known.Alpha, known.ChromaLocation, known.AuthorityDigest) : null;
        MediaFeatureFileSource observed = Create(source.Content, new(source.Kind, source.Presentation.StreamIndex, authority),
            WithZeroOrdinal(frame), still, source.Tool);
        if (observed.Key != source.Key || observed.Presentation.Digest != source.Presentation.Digest || observed.Profile.Digest != source.Profile.Digest)
            throw new MediaAbException(MediaAbFailure.PresentationMismatch, "A decoded frame changed selected stream dimensions time base color SAR or orthogonal geometry.");
    }

    private static MediaNativePresentationMetadata WithZeroOrdinal(MediaNativePresentationMetadata f) =>
        new(0, f.PacketId, f.PacketPosition, f.PacketBytes, f.Pts, f.DecoderDurationTicks, f.Width, f.Height, f.PixelFormat,
            f.Primaries, f.Transfer, f.Matrix, f.Range, f.Chroma, f.Alpha, f.SarNumerator, f.SarDenominator, f.DisplayMatrix,
            f.PlaneCount, f.FirstStride, f.FirstBytes, f.CbStride, f.CbBytes, f.CrStride, f.CrBytes, f.StreamId, f.StreamIndex, f.Family, f.PacketHash);

    private static MediaColorInterpretation Color(MediaNativePresentationMetadata f, MediaStillStructure? still, TrustedMediaColorInterpretation? supplied)
    {
        bool packed = f.PixelFormat is MediaNativePixelFormat.Rgb24 or MediaNativePixelFormat.Rgba32 or MediaNativePixelFormat.Rgb48Le or MediaNativePixelFormat.Rgba64Le;
        bool alphaBearing = f.PixelFormat is MediaNativePixelFormat.Rgba32 or MediaNativePixelFormat.Rgba64Le;
        bool subsampled = f.PixelFormat is MediaNativePixelFormat.Yuv420P8 or MediaNativePixelFormat.Yuv422P8 or MediaNativePixelFormat.Yuv420P10Le or MediaNativePixelFormat.Yuv422P10Le;
        if (!Enum.IsDefined(f.PixelFormat) || f.Alpha is < 0 or > 2 || f.Alpha == 1) throw UnsupportedColor("Native pixel format or premultiplied input alpha is unsupported.");
        MediaTransfer transfer; MediaMatrix matrix; MediaRange range; MediaAlpha alpha; MediaChromaLocation chroma; bool missing = false;
        if (still is not null)
        {
            if (still.Family == 3)
            {
                if (!packed || (still.Bits == 16) != (f.PixelFormat is MediaNativePixelFormat.Rgb48Le or MediaNativePixelFormat.Rgba64Le) || still.HasAlpha && !alphaBearing)
                    throw UnsupportedColor("PNG decoder representation disagrees with its exact source bit depth or transparency.");
                transfer = MediaTransfer.Srgb; matrix = MediaMatrix.Rgb; range = MediaRange.Full; chroma = MediaChromaLocation.None; alpha = still.HasAlpha ? MediaAlpha.Straight : MediaAlpha.Opaque;
                if (f.Primaries is not (1 or 2) || f.Transfer is not (2 or 13) || f.Matrix is not (0 or 2) || f.Range is not (0 or 2) || f.Chroma != 0)
                    throw UnsupportedColor("PNG decoded tags contradict its controlled sRGB interpretation.");
            }
            else
            {
                transfer = MediaTransfer.Srgb; matrix = still.Matrix; range = MediaRange.Full; alpha = MediaAlpha.Opaque; chroma = still.Chroma;
                MediaNativePixelFormat expected = still.Matrix == MediaMatrix.Rgb ? MediaNativePixelFormat.Rgb24 : still.Subsampling switch
                { 420 => MediaNativePixelFormat.Yuv420P8, 422 => MediaNativePixelFormat.Yuv422P8, 444 => MediaNativePixelFormat.Yuv444P8, _ => throw UnsupportedColor("JPEG native sampling is unsupported.") };
                // The pinned MJPEG decoder initializes BT470BG (5) even for structural
                // RGB JPEGs decoded as GBRP and losslessly packed into RGB24. Source
                // components determine RGB here; a missing color authority still fails below.
                if (f.PixelFormat != expected || f.Primaries is not (1 or 2) || f.Transfer is not (2 or 13) || f.Range is not (0 or 2) ||
                    (packed ? (still.Subsampling == 0 ? f.Matrix is not (0 or 2 or 5 or 6) : f.Matrix is not (0 or 2 or 5)) : f.Matrix is not (2 or 5 or 6)) || (subsampled && f.Chroma is not (0 or 2)) || (!subsampled && f.Chroma is not (0 or 2)))
                    throw UnsupportedColor("JPEG native components or explicit tags contradict the fixed JFIF sRGB BT601 full interpretation.");
            }
            missing = !still.HasColorAuthority;
            if (missing && supplied is null) throw UnsupportedColor("Still lacks a supported explicit color authority and no internal missing-tag interpretation was supplied.");
        }
        else
        {
            if (f.Primaries is not (1 or 2)) throw UnsupportedColor("Video primaries are outside BT709 or contain an explicit unsupported wide-gamut declaration.");
            missing |= f.Primaries == 2;
            transfer = f.Transfer switch { 1 => MediaTransfer.Bt709, 13 when packed => MediaTransfer.Srgb, 2 when supplied is not null => supplied.Transfer, _ => throw UnsupportedColor("Video transfer is missing without authority or outside supported SDR.") };
            missing |= f.Transfer == 2;
            matrix = f.Matrix switch { 0 when packed => MediaMatrix.Rgb, 1 when !packed => MediaMatrix.Bt709, 2 when supplied is not null => supplied.Matrix, _ => throw UnsupportedColor("Video matrix is missing without authority or contradicts its native components.") };
            missing |= f.Matrix == 2;
            range = f.Range switch { 2 => MediaRange.Full, 1 when !packed => MediaRange.Limited, 0 when supplied is not null => supplied.Range, _ => throw UnsupportedColor("Video range is missing without authority or unsupported.") };
            missing |= f.Range == 0;
            if (!alphaBearing) alpha = MediaAlpha.Opaque;
            else if (f.Alpha == 2) alpha = MediaAlpha.Straight;
            else if (supplied is not null) { alpha = supplied.Alpha; missing = true; }
            else throw UnsupportedColor("Alpha-bearing video lacks explicit straight-alpha interpretation.");
            chroma = !subsampled ? MediaChromaLocation.None : f.Chroma switch
            { 1 => MediaChromaLocation.Left, 2 => MediaChromaLocation.Center, 0 when supplied is not null => supplied.ChromaLocation, _ => throw UnsupportedColor("Subsampled video lacks explicit supported chroma sample location.") };
            missing |= subsampled && f.Chroma == 0;
            if (!subsampled && f.Chroma is not (0 or 2)) throw UnsupportedColor("Nonsubsampled native video has contradictory chroma sample location metadata.");
        }
        if (missing && supplied is null) throw UnsupportedColor("Missing color metadata requires explicit internal complete import authority.");
        if (supplied is not null && missing && (supplied.Transfer != transfer || supplied.Matrix != matrix || supplied.Range != range || supplied.Alpha != alpha || supplied.ChromaLocation != chroma))
            throw UnsupportedColor("Trusted missing-tag interpretation contradicts explicit source metadata or the supported format interpretation.");
        MediaDigest authority = missing ? supplied!.AuthorityDigest : Authority(still?.Family == 4 ? MediaProfilesV1.JpegInterpretation : "media-explicit-sdr-tags-png-srgb-v1");
        var color0 = new MediaColorInterpretation(transfer, matrix, range, alpha, chroma, f.PixelFormat, missing, authority, default);
        var writer = new MediaAbIdentityWriter(); MediaDigest digest = writer.ComputeColorDigest(color0);
        var color = new MediaColorInterpretation(transfer, matrix, range, alpha, chroma, f.PixelFormat, missing, authority, digest);
        new MediaAbValueValidator().ValidateColor(color); return color;
    }

    private static MediaDigest Authority(string value) => new(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("mako.media.controlled-color-authority.v1\n" + value + "\n"))));
    private static (int Numerator, int Denominator) Sar(MediaNativePresentationMetadata frame, MediaStillStructure? still)
    {
        if (frame.SarNumerator < 0 || frame.SarDenominator <= 0) throw new MediaAbException(MediaAbFailure.PresentationInvalid, "Decoded sample aspect ratio is negative or invalid.");
        (int n, int d) = frame.SarNumerator == 0 ? (1, 1) : MediaStillStructure.Aspect((uint)frame.SarNumerator, (uint)frame.SarDenominator);
        if (still is not null)
        {
            // The pinned libav PNG decoder exposes the pHYs x/y density in
            // AVFrame SAR. Physical pixel width/height is its reciprocal.
            // Apply only to that exact format; structural pHYs stays authoritative.
            if (still.Family == 3 && frame.SarNumerator > 0) (n, d) = (d, n);
            if (frame.SarNumerator > 0 && (n != still.SarNumerator || d != still.SarDenominator)) throw new MediaAbException(MediaAbFailure.PresentationMismatch, "Decoded still SAR contradicts its structural raster metadata.");
            return (still.SarNumerator, still.SarDenominator);
        }
        return (n, d);
    }

    internal static (MediaQuarterTurn Rotation, bool Mirror) Geometry(MediaNativePresentationMetadata frame, MediaStillStructure? still)
    {
        (MediaQuarterTurn Rotation, bool Mirror) exif = (still?.Orientation ?? 1) switch
        { 1 => (MediaQuarterTurn.None, false), 2 => (MediaQuarterTurn.None, true), 3 => (MediaQuarterTurn.Clockwise180, false), 4 => (MediaQuarterTurn.Clockwise180, true),
          5 => (MediaQuarterTurn.Clockwise270, true), 6 => (MediaQuarterTurn.Clockwise90, false), 7 => (MediaQuarterTurn.Clockwise90, true), 8 => (MediaQuarterTurn.Clockwise270, false), _ => throw new MediaAbException(MediaAbFailure.PresentationInvalid, "Exif orientation is invalid.") };
        MediaNativeDisplayMatrix m = frame.DisplayMatrix; if (!m.IsPresent) return exif;
        Need(m.U == 0 && m.V == 0 && m.W == 1 << 30, "Display perspective or scale is outside the exact orthogonal geometry profile.");
        ReadOnlySpan<int> transforms = [1, 0, 0, 1, 0, 1, -1, 0, -1, 0, 0, -1, 0, -1, 1, 0];
        for (int mirror = 0; mirror <= 1; mirror++) for (int rotation = 0; rotation < 4; rotation++)
        {
            int sign = mirror == 1 ? -1 : 1, a = transforms[rotation * 4] * sign, b = transforms[rotation * 4 + 1] * sign, c = transforms[rotation * 4 + 2], d = transforms[rotation * 4 + 3];
            if (m.A != a * 65536 || m.B != b * 65536 || m.C != c * 65536 || m.D != d * 65536) continue;
            long tx = (a < 0 ? frame.Width : 0L) + (c < 0 ? frame.Height : 0L), ty = (b < 0 ? frame.Width : 0L) + (d < 0 ? frame.Height : 0L);
            Need((m.X == 0 && m.Y == 0) || (m.X == tx * 65536 && m.Y == ty * 65536), "Display translation is outside zero-origin or exact raster-bound orthogonal normalization.");
            var observed = ((MediaQuarterTurn)rotation, mirror != 0);
            Need(still is null || still.Orientation == 1 || observed == exif, "Exif orientation and decoded display matrix contradict one another."); return observed;
        }
        throw new MediaAbException(MediaAbFailure.PresentationInvalid, "Display affine is not one of the eight exact orthogonal raster transforms.");
    }
    private static void Need(bool condition, string detail) { if (!condition) throw new MediaAbException(MediaAbFailure.PresentationInvalid, detail); }
    private static MediaAbException UnsupportedColor(string detail) => new(MediaAbFailure.ColorUnsupported, detail);
}
