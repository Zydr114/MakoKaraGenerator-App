using System.Text;

namespace MakoKaraGenerator.Core.Media;

internal static class MediaAbRules
{
    internal static void ValidateDigest(MediaDigest digest, MediaAbFailure failure)
    {
        string? text = digest.LowerHex;
        if (text is null || text.Length != 64 || text.Any(static value => value is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw new MediaAbException(failure, "Media digest must contain exactly sixty four lowercase hexadecimal characters.");
        }
    }

    internal static void ValidateCanonicalText(string? value, MediaAbFailure failure, CancellationToken cancellationToken = default)
    {
        if (value is null)
        {
            throw new MediaAbException(failure, "Canonical identity text contains a forbidden null or byte order marker.");
        }

        for (int index = 0; index < value.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (value[index] is '\0' or '\uFEFF')
            {
                throw new MediaAbException(failure, "Canonical identity text contains a forbidden null or byte order marker.");
            }

            if (char.IsHighSurrogate(value[index]))
            {
                if (index + 1 >= value.Length || !char.IsLowSurrogate(value[index + 1]))
                {
                    throw new MediaAbException(failure, "Canonical identity text contains invalid UTF sixteen.");
                }

                index++;
            }
            else if (char.IsLowSurrogate(value[index]))
            {
                throw new MediaAbException(failure, "Canonical identity text contains invalid UTF sixteen.");
            }
        }
    }

    internal static int ValidateLogicalId(string? value, CancellationToken cancellationToken = default)
    {
        // Valid UTF-8 uses at least as many bytes as UTF-16 code units. Reject huge input before scanning it.
        if (value is { Length: > MediaAbCapacityV1.MaximumToolLogicalIdUtf8Bytes })
        {
            throw new MediaAbException(MediaAbFailure.CapacityExceeded, "Tool logical identifier exceeds the fixed UTF eight byte limit.");
        }

        ValidateCanonicalText(value, MediaAbFailure.ProfileUnsupported, cancellationToken);
        if (string.IsNullOrWhiteSpace(value) || value is "." or "..")
        {
            throw new MediaAbException(MediaAbFailure.ProfileUnsupported, "Tool manifest entry must use a nonblank logical identifier without paths or controls.");
        }

        foreach (char character in value)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (char.IsControl(character) || character is '/' or '\\' or ':')
            {
                throw new MediaAbException(MediaAbFailure.ProfileUnsupported, "Tool manifest entry must use a nonblank logical identifier without paths or controls.");
            }
        }

        int utf8Bytes = Encoding.UTF8.GetByteCount(value);
        if (utf8Bytes > MediaAbCapacityV1.MaximumToolLogicalIdUtf8Bytes)
        {
            throw new MediaAbException(MediaAbFailure.CapacityExceeded, "Tool logical identifier exceeds the fixed UTF eight byte limit.");
        }

        return utf8Bytes;
    }

    internal static void ValidateReducedRatio(int numerator, int denominator, MediaAbFailure failure)
    {
        if (numerator <= 0 || denominator <= 0 || GreatestCommonDivisor(numerator, denominator) != 1)
        {
            throw new MediaAbException(failure, "A canonical rational value must be positive and reduced.");
        }
    }

    internal static int GreatestCommonDivisor(int left, int right)
    {
        while (right != 0)
        {
            (left, right) = (right, left % right);
        }

        return left;
    }

    internal static void ValidateDimensions(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            throw new MediaAbException(MediaAbFailure.PresentationInvalid, "Raster dimensions must be positive.");
        }

        if (width > MediaAbCapacityV1.MaximumSourceSide || height > MediaAbCapacityV1.MaximumSourceSide ||
            (long)width * height > MediaAbCapacityV1.MaximumSourcePixels)
        {
            throw new MediaAbException(MediaAbFailure.CapacityExceeded, "Raster dimensions exceed the fixed source limits.");
        }
    }

    internal static void ValidateColorShape(MediaColorInterpretation value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!Enum.IsDefined(value.Transfer) || !Enum.IsDefined(value.Matrix) || !Enum.IsDefined(value.Range) ||
            !Enum.IsDefined(value.Alpha) || !Enum.IsDefined(value.ChromaLocation) || !Enum.IsDefined(value.NativePixelFormat))
        {
            throw new MediaAbException(MediaAbFailure.ColorUnsupported, "Color interpretation contains an unknown enum value.");
        }

        ValidateDigest(value.AuthorityDigest, MediaAbFailure.ColorUnsupported);
        bool packed = value.NativePixelFormat is MediaNativePixelFormat.Rgb24 or MediaNativePixelFormat.Rgba32 or
            MediaNativePixelFormat.Rgb48Le or MediaNativePixelFormat.Rgba64Le;
        bool alphaBearing = value.NativePixelFormat is MediaNativePixelFormat.Rgba32 or MediaNativePixelFormat.Rgba64Le;
        bool subsampled = value.NativePixelFormat is MediaNativePixelFormat.Yuv420P8 or MediaNativePixelFormat.Yuv422P8 or
            MediaNativePixelFormat.Yuv420P10Le or MediaNativePixelFormat.Yuv422P10Le;
        if (packed)
        {
            if (value.Matrix != MediaMatrix.Rgb || value.Range != MediaRange.Full ||
                value.ChromaLocation != MediaChromaLocation.None || (!alphaBearing && value.Alpha != MediaAlpha.Opaque))
            {
                throw new MediaAbException(MediaAbFailure.ColorUnsupported, "Packed RGB requires full range RGB interpretation and compatible explicit alpha.");
            }

            return;
        }

        if (value.Alpha != MediaAlpha.Opaque || (subsampled && value.ChromaLocation == MediaChromaLocation.None) ||
            (!subsampled && value.ChromaLocation != MediaChromaLocation.None))
        {
            throw new MediaAbException(MediaAbFailure.ColorUnsupported, "Planar YUV requires opaque alpha and an explicit compatible chroma location.");
        }

        if (value.Matrix == MediaMatrix.Bt709 && value.Transfer == MediaTransfer.Bt709)
        {
            return;
        }

        bool jpeg8 = value.NativePixelFormat is MediaNativePixelFormat.Yuv420P8 or MediaNativePixelFormat.Yuv422P8 or MediaNativePixelFormat.Yuv444P8;
        if (value.Matrix == MediaMatrix.Bt601Jpeg && value.Transfer == MediaTransfer.Srgb && value.Range == MediaRange.Full &&
            jpeg8 && (!subsampled || value.ChromaLocation == MediaChromaLocation.Center))
        {
            return;
        }

        throw new MediaAbException(MediaAbFailure.ColorUnsupported, "Planar YUV interpretation is outside the supported SDR combinations.");
    }

    internal static void ValidateToolShape(MediaToolIdentity value)
    {
        ArgumentNullException.ThrowIfNull(value);
        ValidateDigest(value.Executables, MediaAbFailure.ProfileUnsupported);
        ValidateDigest(value.Dependencies, MediaAbFailure.ProfileUnsupported);
        ValidateDigest(value.NormalizedPipeline, MediaAbFailure.ProfileUnsupported);
    }

    internal static void ValidateProfileShape(MediaProfileBundle value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Presentation != MediaProfilesV1.Presentation || value.DecoderPipeline != MediaProfilesV1.DecoderPipeline ||
            value.Color != MediaProfilesV1.Color || value.Statistics != MediaProfilesV1.Statistics ||
            value.Quantization != MediaProfilesV1.Quantization || value.Codec != MediaProfilesV1.Codec ||
            value.Query != MediaProfilesV1.Query || value.Gpu != MediaProfilesV1.Gpu)
        {
            throw new MediaAbException(MediaAbFailure.ProfileUnsupported, "Media profile bundle is not the complete supported version one bundle.");
        }

        ValidateDigest(value.DecoderIdentity, MediaAbFailure.ProfileUnsupported);
        ValidateDigest(value.ColorInterpretation, MediaAbFailure.ProfileUnsupported);
    }

    internal static void ValidateDescriptorShape(MediaFilePresentationDescriptor value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.StreamIndex < 0 || !Enum.IsDefined(value.Rotation))
        {
            throw new MediaAbException(MediaAbFailure.PresentationInvalid, "Selected stream or quarter turn is invalid.");
        }

        ValidateDimensions(value.EncodedWidth, value.EncodedHeight);
        ValidateDimensions(value.CanonicalWidth, value.CanonicalHeight);
        ValidateReducedRatio(value.SarNumerator, value.SarDenominator, MediaAbFailure.PresentationInvalid);
        if (value.StreamTimeBase is { } timeBase)
        {
            ValidateReducedRatio(timeBase.Numerator, timeBase.Denominator, MediaAbFailure.PresentationInvalid);
        }

        bool swapped = value.Rotation is MediaQuarterTurn.Clockwise90 or MediaQuarterTurn.Clockwise270;
        if (value.CanonicalWidth != (swapped ? value.EncodedHeight : value.EncodedWidth) ||
            value.CanonicalHeight != (swapped ? value.EncodedWidth : value.EncodedHeight))
        {
            throw new MediaAbException(MediaAbFailure.PresentationInvalid, "Canonical raster dimensions must exactly match the declared quarter turn.");
        }

        ValidateColorShape(value.Color);
        ValidateDigest(value.Color.Digest, MediaAbFailure.ColorUnsupported);
    }

    internal static void ValidateSourceShape(MediaFeatureFileSource value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Kind is not (MediaSourceKind.Still or MediaSourceKind.Video))
        {
            throw new MediaAbException(MediaAbFailure.PresentationInvalid, "Only still and video have file source identities.");
        }

        ValidateDigest(value.Content.Sha256, MediaAbFailure.SourceChanged);
        if (value.Content.ByteLength < 0)
        {
            throw new MediaAbException(MediaAbFailure.SourceChanged, "Source byte length must be nonnegative.");
        }

        if (value.Content.ByteLength > MediaAbCapacityV1.MaximumSourceBytes)
        {
            throw new MediaAbException(MediaAbFailure.CapacityExceeded, "Source content exceeds the fixed byte limit.");
        }

        ValidateDescriptorShape(value.Presentation);
        ValidateToolShape(value.Tool);
        ValidateProfileShape(value.Profile);
        ValidateDigest(value.Presentation.Digest, MediaAbFailure.PresentationInvalid);
        ValidateDigest(value.Tool.Digest, MediaAbFailure.ProfileUnsupported);
        ValidateDigest(value.Profile.Digest, MediaAbFailure.ProfileUnsupported);
        if ((value.Kind == MediaSourceKind.Still && value.Presentation.StreamTimeBase is not null) ||
            (value.Kind == MediaSourceKind.Video && value.Presentation.StreamTimeBase is null))
        {
            throw new MediaAbException(MediaAbFailure.PresentationInvalid, "Only video sources require a positive reduced stream time base.");
        }

        MediaColorInterpretation color = value.Presentation.Color;
        bool packed16 = color.NativePixelFormat is MediaNativePixelFormat.Rgb48Le or MediaNativePixelFormat.Rgba64Le;
        if ((value.Kind == MediaSourceKind.Still && color.Matrix == MediaMatrix.Bt709) ||
            (value.Kind == MediaSourceKind.Video && (color.Matrix == MediaMatrix.Bt601Jpeg || packed16)))
        {
            throw new MediaAbException(MediaAbFailure.ColorUnsupported, "The native color representation is unsupported for this source family.");
        }
    }
}
