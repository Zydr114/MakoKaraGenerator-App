using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Rendering.Skia;
using SkiaSharp;

internal sealed record RubyLegacyBaselineEntry(string RubyText, long TimeUs, bool Transparent,
    string PixelSha256, string PngSha256, string GeometryJson);

internal static class RubyLegacyBaselineProbe
{
    internal static RubyLegacyBaselineEntry[] Capture()
    {
        // These historical pixel/PNG hashes describe the CPU rasterizer. Keep the
        // original renderer explicit even when the surrounding suite selects Vulkan.
        using var device = SkiaRenderDeviceFactory.Create(new(SkiaRenderBackend.Cpu));
        using SkiaRenderPipeline pipeline = new(null, null, null, null, false, device, false);
        List<RubyLegacyBaselineEntry> result = [];
        foreach (string text in new[] { "せん", "e\u0301office", "👩‍💻" })
        {
            var snapshot = TimedRubyFixture.Create([RubyTextSegment.Create("legacy", text)]);
            foreach (long time in new long[] { 0, 400_000, 450_000, 500_000, 550_000, 600_000 })
            {
                var scene = pipeline.GetSceneFrame(snapshot, time);
                var token = scene.TokenGeometries.Single();
                var ruby = token.Ruby.Single();
                string geometry = JsonSerializer.Serialize(new { token.BaseGeometry.AdvanceBounds,
                    token.BaseGeometry.InkBounds, token.BaseGeometry.BaseWorldMatrix,
                    FamilyCorners = token.FamilyGeometry.WorldInkCorners, RubyCorners = ruby.Geometry.WorldInkCorners,
                    DrawUnits = ruby.Geometry.DrawUnits.Select(unit => unit.Value) });
                foreach (bool transparent in new[] { false, true })
                {
                    using var frame = pipeline.Render(RenderRequest.At(snapshot, time) with { TransparentBackground = transparent });
                    using var bitmap = new SKBitmap(new SKImageInfo(frame.Width, frame.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
                    var pixels = frame.PixelMemory.ToArray(); Marshal.Copy(pixels, 0, bitmap.GetPixels(), pixels.Length);
                    using var image = SKImage.FromBitmap(bitmap); using var png = image.Encode(SKEncodedImageFormat.Png, 100);
                    result.Add(new(text, time, transparent, Hash(pixels), Hash(png.ToArray()), geometry));
                }
            }
        }
        return [.. result];
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
