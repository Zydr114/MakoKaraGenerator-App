namespace MakoKaraGenerator.Backend.Hosting.Tests;

internal static class PreviewPngFixture
{
    // A real 1x1, non-interlaced RGBA PNG with a red pixel, valid zlib data and chunk CRCs.
    internal static byte[] Create() => Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGP4z8DwHwAFAAH/iZk9HQAAAABJRU5ErkJggg==");
}
