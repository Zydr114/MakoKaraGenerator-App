using System.Buffers.Binary;
using MakoKaraGenerator.Core.Media;

namespace MakoKaraGenerator.Media.Tests;

internal static class MediaSdrFixture
{
    internal static MediaFeatureFileSource Source(MediaNativePixelFormat format, int width, int height,
        MediaMatrix matrix = MediaMatrix.Rgb, MediaTransfer transfer = MediaTransfer.Srgb,
        MediaRange range = MediaRange.Full, MediaAlpha alpha = MediaAlpha.Opaque,
        MediaChromaLocation chroma = MediaChromaLocation.None, MediaQuarterTurn turn = MediaQuarterTurn.None, bool mirror = false)
    {
        var writer = new MediaAbIdentityWriter();
        MediaDigest Hash(char digit) => new(new string(digit, 64));
        var rawColor = new MediaColorInterpretation(transfer, matrix, range, alpha, chroma, format, false, Hash('1'), default);
        var color = new MediaColorInterpretation(transfer, matrix, range, alpha, chroma, format, false, rawColor.AuthorityDigest, writer.ComputeColorDigest(rawColor));
        bool video = matrix == MediaMatrix.Bt709;
        bool swapped = turn is MediaQuarterTurn.Clockwise90 or MediaQuarterTurn.Clockwise270;
        var rawDescriptor = new MediaFilePresentationDescriptor(0, width, height, swapped ? height : width, swapped ? width : height,
            video ? new RationalTimeBase(1, 90000) : null, 1, 1, turn, mirror, color, default);
        var descriptor = new MediaFilePresentationDescriptor(0, width, height, rawDescriptor.CanonicalWidth, rawDescriptor.CanonicalHeight,
            rawDescriptor.StreamTimeBase, 1, 1, turn, mirror, color, writer.ComputeDescriptorDigest(rawDescriptor));
        var rawTool = new MediaToolIdentity(Hash('2'), Hash('3'), Hash('4'), default);
        var tool = new MediaToolIdentity(rawTool.Executables, rawTool.Dependencies, rawTool.NormalizedPipeline, writer.ComputeToolDigest(rawTool));
        var rawProfile = new MediaProfileBundle(MediaProfilesV1.Presentation, MediaProfilesV1.DecoderPipeline, MediaProfilesV1.Color,
            MediaProfilesV1.Statistics, MediaProfilesV1.Quantization, MediaProfilesV1.Codec, MediaProfilesV1.Query, MediaProfilesV1.Gpu,
            tool.Digest, color.Digest, default);
        var profile = new MediaProfileBundle(rawProfile.Presentation, rawProfile.DecoderPipeline, rawProfile.Color, rawProfile.Statistics,
            rawProfile.Quantization, rawProfile.Codec, rawProfile.Query, rawProfile.Gpu, tool.Digest, color.Digest, writer.ComputeProfileDigest(rawProfile));
        var raw = new MediaFeatureFileSource(default, video ? MediaSourceKind.Video : MediaSourceKind.Still, new(Hash('5'), 123), descriptor, tool, profile);
        return new(writer.ComputeSourceKey(raw), raw.Kind, raw.Content, descriptor, tool, profile);
    }

    internal static byte[] Plane(int width, int height, int bytes, int padding, Func<int, int, int> sample)
    {
        int stride = width * bytes + padding;
        byte[] result = Enumerable.Repeat((byte)239, stride * height).ToArray();
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
        {
            int offset = y * stride + x * bytes, value = sample(x, y);
            if (bytes == 1) result[offset] = checked((byte)value);
            else BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(offset, 2), checked((ushort)value));
        }
        return result;
    }

    // Independent color oracle derives the matrix from Kr/Kb and evaluates the EOTF
    // via Exp/Log rather than calling the converter or copying its matrix coefficients.
    internal static float[] YuvOracle(double yCode, double cbCode, double crCode, int bits, bool limited, bool jpeg)
    {
        double maximum = Math.Pow(2, bits) - 1, scale = Math.Pow(2, bits - 8);
        double y = limited ? (yCode / scale - 16) / 219 : yCode / maximum;
        double cb = limited ? (cbCode / scale - 128) / 224 : (cbCode - Math.Pow(2, bits - 1)) / maximum;
        double cr = limited ? (crCode / scale - 128) / 224 : (crCode - Math.Pow(2, bits - 1)) / maximum;
        double kr = jpeg ? .299 : .2126, kb = jpeg ? .114 : .0722, kg = 1 - kr - kb;
        double r = y + 2 * (1 - kr) * cr, b = y + 2 * (1 - kb) * cb;
        double g = (y - kr * r - kb * b) / kg;
        return [(float)Transfer(Math.Clamp(r, 0, 1), jpeg), (float)Transfer(Math.Clamp(g, 0, 1), jpeg), (float)Transfer(Math.Clamp(b, 0, 1), jpeg), 1];
    }

    internal static double Transfer(double encoded, bool srgb)
    {
        if (srgb) return encoded <= .04045 ? encoded / 12.92 : Math.Exp(2.4 * Math.Log((encoded + .055) / 1.055));
        return encoded < .081 ? encoded / 4.5 : Math.Exp(Math.Log((encoded + .099) / 1.099) / .45);
    }

    // Independent bilinear oracle sums tent weights over native sample centers.
    // Edge extension is represented by clamping the location to the outermost centers.
    internal static double ChromaOracle(int x, int y, int width, int height, int sx, int sy, bool left, Func<int, int, int> code)
    {
        int columns = (width + sx - 1) / sx, rows = (height + sy - 1) / sy;
        double firstX = sx == 1 || left ? .5 : 1, firstY = sy == 1 ? .5 : 1;
        double px = Math.Clamp(x + .5, firstX, firstX + (columns - 1) * sx);
        double py = Math.Clamp(y + .5, firstY, firstY + (rows - 1) * sy);
        double weighted = 0, weights = 0;
        for (int j = 0; j < rows; j++) for (int i = 0; i < columns; i++)
        {
            double weight = Math.Max(0, 1 - Math.Abs(px - (firstX + i * sx)) / sx)
                * Math.Max(0, 1 - Math.Abs(py - (firstY + j * sy)) / sy);
            weighted += code(i, j) * weight; weights += weight;
        }
        return weighted / weights;
    }

    internal static void Close(float[] expected, ReadOnlySpan<float> actual, double tolerance = 1e-6)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < expected.Length; i++) Assert.InRange(Math.Abs(expected[i] - actual[i]), 0, tolerance);
    }
}
