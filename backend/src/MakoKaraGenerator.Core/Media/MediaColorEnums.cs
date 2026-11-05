namespace MakoKaraGenerator.Core.Media;

/// <summary>The normative source family.</summary>
public enum MediaSourceKind : uint
{
    /// <summary>A timeless single-frame file.</summary>
    Still = 1,

    /// <summary>A PTS-indexed file stream.</summary>
    Video = 2,

    /// <summary>A direct color without a file artifact.</summary>
    Solid = 3,

}

/// <summary>The supported encoded RGB transfer functions.</summary>
public enum MediaTransfer : uint
{
    /// <summary>The IEC sRGB piecewise transfer.</summary>
    Srgb = 1,

    /// <summary>The BT.709 piecewise transfer.</summary>
    Bt709 = 2,

}

/// <summary>The supported native component interpretations.</summary>
public enum MediaMatrix : uint
{
    /// <summary>Native packed RGB components.</summary>
    Rgb = 1,

    /// <summary>The explicit BT.709 YUV matrix.</summary>
    Bt709 = 2,

    /// <summary>The explicit full-range BT.601 JPEG matrix with sRGB interpretation.</summary>
    Bt601Jpeg = 3,

}

/// <summary>The declared native component code range.</summary>
public enum MediaRange : uint
{
    /// <summary>Full component code range.</summary>
    Full = 1,

    /// <summary>Declared limited YUV code range.</summary>
    Limited = 2,

}

/// <summary>The supported native alpha representations.</summary>
public enum MediaAlpha : uint
{
    /// <summary>No transparency; alpha is identically one.</summary>
    Opaque = 1,

    /// <summary>Explicit unassociated alpha in packed RGBA.</summary>
    Straight = 2,

}

/// <summary>The native chroma sample position.</summary>
public enum MediaChromaLocation : uint
{
    /// <summary>No chroma subsampling.</summary>
    None = 0,

    /// <summary>Explicit left chroma sample centers.</summary>
    Left = 1,

    /// <summary>Explicit centered chroma sample centers.</summary>
    Center = 2,

}

/// <summary>A clockwise raster rotation applied after horizontal mirroring.</summary>
public enum MediaQuarterTurn : uint
{
    /// <summary>No rotation.</summary>
    None = 0,

    /// <summary>One clockwise quarter turn.</summary>
    Clockwise90 = 1,

    /// <summary>Two clockwise quarter turns.</summary>
    Clockwise180 = 2,

    /// <summary>Three clockwise quarter turns.</summary>
    Clockwise270 = 3,

}

/// <summary>Only these native representations enter the first SDR converter.</summary>
public enum MediaNativePixelFormat : uint
{
    /// <summary>Packed 8-bit RGB.</summary>
    Rgb24 = 1,

    /// <summary>Packed 8-bit RGBA.</summary>
    Rgba32 = 2,

    /// <summary>Packed little-endian 16-bit RGB.</summary>
    Rgb48Le = 3,

    /// <summary>Packed little-endian 16-bit RGBA.</summary>
    Rgba64Le = 4,

    /// <summary>Planar 8-bit 4:2:0 YUV.</summary>
    Yuv420P8 = 5,

    /// <summary>Planar 8-bit 4:2:2 YUV.</summary>
    Yuv422P8 = 6,

    /// <summary>Planar 8-bit 4:4:4 YUV.</summary>
    Yuv444P8 = 7,

    /// <summary>Planar little-endian 10-bit 4:2:0 YUV.</summary>
    Yuv420P10Le = 8,

    /// <summary>Planar little-endian 10-bit 4:2:2 YUV.</summary>
    Yuv422P10Le = 9,

    /// <summary>Planar little-endian 10-bit 4:4:4 YUV.</summary>
    Yuv444P10Le = 10,

}
