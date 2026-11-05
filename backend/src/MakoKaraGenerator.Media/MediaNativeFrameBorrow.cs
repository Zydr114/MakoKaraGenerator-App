namespace MakoKaraGenerator.Media;

/// <summary>A top-left positive-stride native plane borrowed for one synchronous conversion.</summary>
/// <param name="Bytes">The complete backing slice, including any charged row padding.</param>
/// <param name="StrideBytes">The positive distance between successive rows.</param>
public readonly record struct MediaNativePlaneBorrow(ReadOnlyMemory<byte> Bytes, int StrideBytes);

/// <summary>One packed RGB plane or exactly three planar Y, Cb and Cr planes.</summary>
/// <param name="First">The packed plane or luma plane.</param>
/// <param name="Cb">The chroma blue plane, empty for packed RGB.</param>
/// <param name="Cr">The chroma red plane, empty for packed RGB.</param>
/// <remarks>The owner keeps all native bytes stable and alive through Convert. No lease is transferred.</remarks>
public readonly record struct MediaNativeFrameBorrow(
    MediaNativePlaneBorrow First, MediaNativePlaneBorrow Cb, MediaNativePlaneBorrow Cr);
