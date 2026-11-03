using MakoKaraGenerator.Scripting.Compilation;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using MakoKaraGenerator.Backend.Rendering;
using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Media;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Runtime.Hosting;
using MakoKaraGenerator.Scripting;
using SkiaSharp;

if (args.Length != 6) throw new ArgumentException("saved-directory output-prefix timeUs transparent layout owned-audio-declaration");
var project = ProjectStore.Load(args[0]).Project ?? throw new InvalidOperationException("Saved HTTP fixture did not load.");
if (!Path.IsPathFullyQualified(args[5]) || !File.Exists(args[5])) throw new ArgumentException("Owned pinned audio declaration must exist and be absolute.");
project = project with { AudioPath = args[5] };
var snapshot = ProjectSnapshot.FromProject(project);
using var videos = new FfmpegVideoFrameProvider();
var factory = new ProjectRenderSessionFactory(new ScriptCompiler(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"), args[1] + "-cache"), videos, ReplayReason.Initial);
await using var session = await factory.CreateAsync(snapshot);
var measurable = (ISceneMeasurementSession)session;
using var measured = await measurable.MeasureAsync(RenderRequest.At(snapshot, long.Parse(args[2], CultureInfo.InvariantCulture)) with
{
    Target = new() { PixelWidth = 320, PixelHeight = 180 }, TransparentBackground = bool.Parse(args[3]),
}, bool.Parse(args[4]));
var frame = measured.Frame;
if (frame.Format != PixelFormat.Bgra8888Premultiplied || frame.Stride != frame.Width * 4) throw new InvalidOperationException("Unexpected pixels.");
byte[] pixels = frame.PixelMemory.ToArray();
using var bitmap = new SKBitmap(new SKImageInfo(frame.Width, frame.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
Marshal.Copy(pixels, 0, bitmap.GetPixels(), pixels.Length);
using var image = SKImage.FromBitmap(bitmap);
using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
byte[] png = encoded.ToArray();
await File.WriteAllBytesAsync(args[1] + ".png", png);
await File.WriteAllBytesAsync(args[1] + ".bgra", pixels);
var active = measured.Scene.Elements.ToDictionary(element => element.Id, StringComparer.Ordinal);
var elements = measured.Scene.Definition.Elements.Select(definition =>
{
    active.TryGetValue(definition.Id, out var element);
    if (element is null && definition.SourceBinding.LineId is null)
        throw new InvalidOperationException("Persistent definition missing from direct active elements: " + definition.Id);
    return new
    {
        definition.Id, IsVisible = element?.IsVisible ?? false, Opacity = element?.Opacity ?? 0,
        WorldMatrix = element?.WorldMatrix, Pivot = element?.Pivot, WorldCorners = element?.WorldCorners ?? [],
        definition.SourceBinding.BranchId, definition.SourceBinding.LineId, definition.SourceBinding.TokenId, definition.SourceBinding.RubyId,
    };
}).ToArray();
await File.WriteAllTextAsync(args[1] + ".json", JsonSerializer.Serialize(new
{
    Scope = "Independent ProjectStore.Load and direct Runtime.MeasureAsync; only AudioPath declaration normalized to verified same-byte owned pinned closure. Does not invoke Worker or HTTP measurement.",
    EngineIdentity = StaticProjectSnapshotCodec.EngineIdentity,
    SourceSha256 = Convert.ToHexStringLower(SHA256.HashData(StaticProjectSnapshotCodec.Encode(snapshot))),
    PngSha256 = Convert.ToHexStringLower(SHA256.HashData(png)), BgraSha256 = Convert.ToHexStringLower(SHA256.HashData(pixels)), Elements = elements,
}, OracleJson.Options));

internal static class OracleJson
{
    internal static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
}
