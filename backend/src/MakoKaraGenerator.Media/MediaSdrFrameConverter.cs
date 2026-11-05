using System.Buffers.Binary;
using System.Runtime.InteropServices;
using MakoKaraGenerator.Core.Media;

namespace MakoKaraGenerator.Media;

/// <summary>Pure controlled SDR conversion into top-left canonical linear-premultiplied RGBA32F.</summary>
/// <remarks>No decoder, tag guess, ICC conversion, native envelope, allocation, hidden image resampling or ownership transfer.
/// Source admission establishes consistency, not external tag authority. The inspector must supply its controlled source.
/// Owner reserves backing before calling and publishes destination only after successful return. Cancellation or failure
/// may leave destination partially written. Input and destination must remain exclusive and alive during the call.</remarks>
public static class MediaSdrFrameConverter
{
    /// <summary>Computes the exact float count without allocating a canonical buffer.</summary>
    /// <param name="width">The bounded canonical width.</param>
    /// <param name="height">The bounded canonical height.</param>
    public static int GetRequiredCanonicalFloatCount(int width, int height)
        => checked((int)(MediaDecodeAdmissionV1.GetCanonicalFrameBytes(width, height) / sizeof(float)));

    /// <summary>Validates all fixed input and storage before converting a borrowed frame.</summary>
    /// <param name="source">The completely validated source identity fixed by the inspector.</param>
    /// <param name="native">The borrowed native planes with complete charged backing slices.</param>
    /// <param name="destination">Exactly one canonical frame of exclusive caller-owned float backing.</param>
    /// <param name="otherSimultaneousBytes">All other live decode backing already reserved by the owner, including native envelope and queues.</param>
    /// <param name="cancellationToken">Cancellation propagated unchanged, checked before validation and during every row scan.</param>
    public static void Convert(MediaFeatureFileSource source, MediaNativeFrameBorrow native, Span<float> destination,
        long otherSimultaneousBytes = 0, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        new MediaAbValueValidator().ValidateSource(source);
        var descriptor = source.Presentation;
        var geometry = MediaRasterGeometry.Create(descriptor.EncodedWidth, descriptor.EncodedHeight,
            descriptor.SarNumerator, descriptor.SarDenominator, descriptor.Rotation, descriptor.MirrorHorizontallyBeforeRotation);
        if (destination.Length != GetRequiredCanonicalFloatCount(geometry.CanonicalWidth, geometry.CanonicalHeight))
        {
            throw Invalid("Canonical destination must have exactly the required float count.");
        }

        var format = Layout(descriptor.Color.NativePixelFormat);
        int width = descriptor.EncodedWidth, height = descriptor.EncodedHeight;
        ValidatePlane(native.First, checked(width * format.Bytes * format.Components), height, format.Bytes);
        if (format.Planar)
        {
            int chromaWidth = (width + format.SubsampleX - 1) / format.SubsampleX;
            int chromaHeight = (height + format.SubsampleY - 1) / format.SubsampleY;
            ValidatePlane(native.Cb, checked(chromaWidth * format.Bytes), chromaHeight, format.Bytes);
            ValidatePlane(native.Cr, checked(chromaWidth * format.Bytes), chromaHeight, format.Bytes);
        }
        else if (!native.Cb.Bytes.IsEmpty || !native.Cr.Bytes.IsEmpty || native.Cb.StrideBytes != 0 || native.Cr.StrideBytes != 0)
        {
            throw Invalid("Packed RGB must not carry additional native planes.");
        }

        long liveBytes = checked((long)native.First.Bytes.Length + native.Cb.Bytes.Length + native.Cr.Bytes.Length + (long)destination.Length * sizeof(float));
        if (otherSimultaneousBytes < 0)
        {
            throw new MediaAbException(MediaAbFailure.CapacityUnsupported, "Other simultaneous decode backing must be nonnegative.");
        }

        if (liveBytes > MediaAbCapacityV1.MaximumDecodeWorkingBytes || otherSimultaneousBytes > MediaAbCapacityV1.MaximumDecodeWorkingBytes - liveBytes)
        {
            throw new MediaAbException(MediaAbFailure.CapacityExceeded, "Conversion backing exceeds the fixed simultaneous decode ceiling.");
        }

        Span<byte> outputBytes = MemoryMarshal.AsBytes(destination);
        if (native.First.Bytes.Span.Overlaps(outputBytes) || native.Cb.Bytes.Span.Overlaps(outputBytes) || native.Cr.Bytes.Span.Overlaps(outputBytes))
        {
            throw Invalid("Native input backing must not overlap canonical destination backing.");
        }

        // Validate every native 10-bit component before interpreting any matrix sample.
        if (format.Planar && format.Bytes == 2)
        {
            ScanTenBit(native.First, width, height, cancellationToken);
            int chromaWidth = (width + format.SubsampleX - 1) / format.SubsampleX;
            int chromaHeight = (height + format.SubsampleY - 1) / format.SubsampleY;
            ScanTenBit(native.Cb, chromaWidth, chromaHeight, cancellationToken);
            ScanTenBit(native.Cr, chromaWidth, chromaHeight, cancellationToken);
        }

        var interpretation = descriptor.Color;
        for (int y = 0; y < height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int x = 0; x < width; x++)
            {
                if ((x & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
                double red, green, blue, alpha;
                if (!format.Planar)
                {
                    int offset = checked(y * native.First.StrideBytes + x * format.Bytes * format.Components);
                    double maximum = format.Bytes == 1 ? 255 : 65535;
                    red = Sample(native.First, offset, format.Bytes) / maximum;
                    green = Sample(native.First, offset + format.Bytes, format.Bytes) / maximum;
                    blue = Sample(native.First, offset + 2 * format.Bytes, format.Bytes) / maximum;
                    alpha = interpretation.Alpha == MediaAlpha.Opaque ? 1 : Sample(native.First, offset + 3 * format.Bytes, format.Bytes) / maximum;
                }
                else
                {
                    double lumaCode = Sample(native.First, checked(y * native.First.StrideBytes + x * format.Bytes), format.Bytes);
                    double cbCode = Chroma(native.Cb, x, y, width, height, format, interpretation.ChromaLocation);
                    double crCode = Chroma(native.Cr, x, y, width, height, format, interpretation.ChromaLocation);
                    int bits = format.Bytes == 1 ? 8 : 10;
                    double maximum = (1 << bits) - 1, center = 1 << (bits - 1), scale = 1 << (bits - 8);
                    double luma = interpretation.Range == MediaRange.Full ? lumaCode / maximum : (lumaCode - 16 * scale) / (219 * scale);
                    double cb = interpretation.Range == MediaRange.Full ? (cbCode - center) / maximum : (cbCode - 128 * scale) / (224 * scale);
                    double cr = interpretation.Range == MediaRange.Full ? (crCode - center) / maximum : (crCode - 128 * scale) / (224 * scale);
                    bool jpeg = interpretation.Matrix == MediaMatrix.Bt601Jpeg;
                    red = luma + (jpeg ? 1.402 : 1.5748) * cr;
                    blue = luma + (jpeg ? 1.772 : 1.8556) * cb;
                    green = luma - (jpeg ? .34413628620102216 : .1873242729306488) * cb
                        - (jpeg ? .7141362862010221 : .4681242729306488) * cr;
                    // The single normative saturation point is after the declared YUV matrix,
                    // before EOTF. It never repairs unknown tags, malformed planes or alpha.
                    red = Math.Clamp(red, 0, 1); green = Math.Clamp(green, 0, 1); blue = Math.Clamp(blue, 0, 1);
                    alpha = 1;
                }

                (int canonicalX, int canonicalY) = geometry.EncodedToCanonical(x, y);
                int output = checked((canonicalY * geometry.CanonicalWidth + canonicalX) * 4);
                Store(destination.Slice(output, 4), red, green, blue, alpha, interpretation.Transfer);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    private static void Store(Span<float> output, double red, double green, double blue, double alpha, MediaTransfer transfer)
    {
        if (!double.IsFinite(red) || !double.IsFinite(green) || !double.IsFinite(blue) || !double.IsFinite(alpha) ||
            red < 0 || red > 1 || green < 0 || green > 1 || blue < 0 || blue > 1 || alpha < 0 || alpha > 1)
        {
            throw new MediaAbException(MediaAbFailure.ColorUnsupported, "Encoded components must be finite supported SDR values.");
        }

        float a = PositiveZero((float)alpha);
        float r = PositiveZero((float)(Eotf(red, transfer) * alpha));
        float g = PositiveZero((float)(Eotf(green, transfer) * alpha));
        float b = PositiveZero((float)(Eotf(blue, transfer) * alpha));
        if (!float.IsFinite(r) || !float.IsFinite(g) || !float.IsFinite(b) || !float.IsFinite(a) ||
            r < 0 || g < 0 || b < 0 || r > a || g > a || b > a || a < 0 || a > 1)
        {
            throw new MediaAbException(MediaAbFailure.ColorUnsupported, "Converted canonical components are not valid linear premultiplied SDR.");
        }

        output[0] = r; output[1] = g; output[2] = b; output[3] = a;
    }

    private static double Eotf(double encoded, MediaTransfer transfer) => transfer switch
    {
        MediaTransfer.Srgb => encoded <= .04045 ? encoded / 12.92 : Math.Pow((encoded + .055) / 1.055, 2.4),
        MediaTransfer.Bt709 => encoded < .081 ? encoded / 4.5 : Math.Pow((encoded + .099) / 1.099, 1 / .45),
        _ => throw new MediaAbException(MediaAbFailure.ColorUnsupported, "Transfer function is outside the controlled SDR profile."),
    };

    private static float PositiveZero(float value) => value == 0 ? 0 : value;

    private static double Chroma(MediaNativePlaneBorrow plane, int x, int y, int width, int height, PixelLayout format, MediaChromaLocation location)
    {
        if (format.SubsampleX == 1 && format.SubsampleY == 1)
            return Sample(plane, checked(y * plane.StrideBytes + x * format.Bytes), format.Bytes);
        double horizontal = ((x + .5) - (location == MediaChromaLocation.Left ? .5 : 1)) / format.SubsampleX;
        double vertical = format.SubsampleY == 1 ? y : ((y + .5) - 1) / format.SubsampleY;
        int floorX = (int)Math.Floor(horizontal), floorY = (int)Math.Floor(vertical);
        double fractionX = horizontal - floorX, fractionY = vertical - floorY;
        int chromaWidth = (width + format.SubsampleX - 1) / format.SubsampleX;
        int chromaHeight = (height + format.SubsampleY - 1) / format.SubsampleY;
        int x0 = Math.Clamp(floorX, 0, chromaWidth - 1), x1 = Math.Clamp(floorX + 1, 0, chromaWidth - 1);
        int y0 = Math.Clamp(floorY, 0, chromaHeight - 1), y1 = Math.Clamp(floorY + 1, 0, chromaHeight - 1);
        double topLeft = Sample(plane, checked(y0 * plane.StrideBytes + x0 * format.Bytes), format.Bytes);
        double topRight = Sample(plane, checked(y0 * plane.StrideBytes + x1 * format.Bytes), format.Bytes);
        double bottomLeft = Sample(plane, checked(y1 * plane.StrideBytes + x0 * format.Bytes), format.Bytes);
        double bottomRight = Sample(plane, checked(y1 * plane.StrideBytes + x1 * format.Bytes), format.Bytes);
        double top = topLeft + (topRight - topLeft) * fractionX;
        double bottom = bottomLeft + (bottomRight - bottomLeft) * fractionX;
        return top + (bottom - top) * fractionY;
    }

    private static int Sample(MediaNativePlaneBorrow plane, int offset, int bytes)
        => bytes == 1 ? plane.Bytes.Span[offset] : BinaryPrimitives.ReadUInt16LittleEndian(plane.Bytes.Span.Slice(offset, 2));

    private static void ValidatePlane(MediaNativePlaneBorrow plane, int rowBytes, int height, int sampleBytes)
    {
        if (plane.StrideBytes < rowBytes || plane.StrideBytes % sampleBytes != 0)
            throw Invalid("Native plane stride cannot represent its complete positive rows.");
        long minimum = checked((long)(height - 1) * plane.StrideBytes + rowBytes);
        long maximum = checked((long)height * plane.StrideBytes);
        if (plane.Bytes.Length < minimum || plane.Bytes.Length > maximum)
            throw Invalid("Native plane backing does not match its complete bounded row layout.");
    }

    private static void ScanTenBit(MediaNativePlaneBorrow plane, int width, int height, CancellationToken cancellationToken)
    {
        for (int y = 0; y < height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int x = 0; x < width; x++)
            {
                if ((x & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
                if (Sample(plane, checked(y * plane.StrideBytes + x * 2), 2) > 1023)
                    throw new MediaAbException(MediaAbFailure.ColorUnsupported, "Ten bit native samples contain nonzero unsupported high bits.");
            }
        }
    }

    private static PixelLayout Layout(MediaNativePixelFormat format) => format switch
    {
        MediaNativePixelFormat.Rgb24 => new(false, 1, 3, 1, 1),
        MediaNativePixelFormat.Rgba32 => new(false, 1, 4, 1, 1),
        MediaNativePixelFormat.Rgb48Le => new(false, 2, 3, 1, 1),
        MediaNativePixelFormat.Rgba64Le => new(false, 2, 4, 1, 1),
        MediaNativePixelFormat.Yuv420P8 => new(true, 1, 1, 2, 2),
        MediaNativePixelFormat.Yuv422P8 => new(true, 1, 1, 2, 1),
        MediaNativePixelFormat.Yuv444P8 => new(true, 1, 1, 1, 1),
        MediaNativePixelFormat.Yuv420P10Le => new(true, 2, 1, 2, 2),
        MediaNativePixelFormat.Yuv422P10Le => new(true, 2, 1, 2, 1),
        MediaNativePixelFormat.Yuv444P10Le => new(true, 2, 1, 1, 1),
        _ => throw new MediaAbException(MediaAbFailure.ColorUnsupported, "Native pixel format is outside the controlled SDR profile."),
    };

    private static MediaAbException Invalid(string detail) => new(MediaAbFailure.PresentationInvalid, detail);
    private readonly record struct PixelLayout(bool Planar, int Bytes, int Components, int SubsampleX, int SubsampleY);
}
