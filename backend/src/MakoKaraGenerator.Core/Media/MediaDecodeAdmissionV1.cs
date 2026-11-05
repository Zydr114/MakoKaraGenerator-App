namespace MakoKaraGenerator.Core.Media;

/// <summary>Checked total-capacity arithmetic without native allocation or an owner reservation ledger.</summary>
public static class MediaDecodeAdmissionV1
{
    /// <summary>Computes the backing for one full canonical packed RGBA32F raster.</summary>
    /// <param name="width">The positive bounded raster width.</param>
    /// <param name="height">The positive bounded raster height.</param>
    /// <returns>Exactly width times height times sixteen bytes.</returns>
    /// <exception cref="MediaAbException">Dimensions or pixel capacity are invalid.</exception>
    public static long GetCanonicalFrameBytes(int width, int height)
    {
        MediaAbRules.ValidateDimensions(width, height);
        return checked((long)width * height * 4 * sizeof(float));
    }

    /// <summary>Admits all simultaneously live decode backing before allocation or process start.</summary>
    /// <param name="workingSet">Explicit conservative reservations supplied by a controlled implementation.</param>
    /// <returns>The checked total charged bytes.</returns>
    /// <exception cref="MediaAbException">A component is invalid, the native envelope is unproven, or the total exceeds the fixed decode ceiling.</exception>
    /// <remarks>One 16M-pixel canonical frame consumes the entire 256MiB ceiling. Any additional backing requires refusal. The owning layer separately enforces atomic reservations.</remarks>
    public static long Admit(MediaDecodeWorkingSet workingSet)
    {
        ArgumentNullException.ThrowIfNull(workingSet);
        if (!workingSet.NativeEnvelopeProven)
        {
            throw new MediaAbException(MediaAbFailure.CapacityUnsupported, "The native decoder envelope cannot be proved and enforced.");
        }

        long[] components = [workingSet.NativeEnvelopeBytes, workingSet.CanonicalPixelBytes,
            workingSet.ConversionBytes, workingSet.MetadataBytes, workingSet.PipeBytes, workingSet.OtherSimultaneousBytes];
        long total = 0;
        foreach (long component in components)
        {
            if (component < 0)
            {
                throw new MediaAbException(MediaAbFailure.CapacityUnsupported, "A decode working reservation is negative.");
            }

            if (component > MediaAbCapacityV1.MaximumDecodeWorkingBytes - total)
            {
                throw new MediaAbException(MediaAbFailure.CapacityExceeded, "Simultaneous decode backing exceeds the fixed working limit.");
            }

            total += component;
        }

        return total;
    }
}
