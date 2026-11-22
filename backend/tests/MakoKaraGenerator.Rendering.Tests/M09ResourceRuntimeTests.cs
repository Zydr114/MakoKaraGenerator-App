using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Media;
using MakoKaraGenerator.Runtime.Hosting;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Rendering.Abstractions;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Scripting;
using MakoKaraGenerator.Scripting.Compilation;
using MakoKaraGenerator.TestSupport;
using SkiaSharp;

namespace MakoKaraGenerator.Rendering.Tests;

// Exercises real runtime assembly with an independent CPU pixel oracle.
// The GPU paths require MAKO_GPU_SMOKE=1; they explicitly request Vulkan and cannot fall back.
// CPU is an independent oracle, never the production backend for an enabled v3 effect.
public sealed class M09ResourceRuntimeTests
{
    private static readonly string FontFamily = PackageFontFixture.Family.Replace("Mako", "M09X", StringComparison.Ordinal);
    private static bool GpuRequested => Environment.GetEnvironmentVariable("MAKO_GPU_SMOKE") == "1";

    [Theory]
    [InlineData("cpu")] [InlineData("vulkan")]
    public async Task 当前v3固定字体实际用于歌词和Ruby且包外同名字体不存在(string backend)
    {
        if (backend == "vulkan" && !GpuRequested) return;
        using TemporaryDirectory temp = new(); using FfmpegVideoFrameProvider videos = new();
        byte[] bytes = PackageFontFixture.FontBytes();
        byte[] oldName = Encoding.BigEndianUnicode.GetBytes("Mako"), newName = Encoding.BigEndianUnicode.GetBytes("M09X");
        int replacements = 0;
        for (int i = 0; i <= bytes.Length - oldName.Length; i++)
            if (bytes.AsSpan(i, oldName.Length).SequenceEqual(oldName)) { newName.CopyTo(bytes, i); replacements++; }
        Assert.True(replacements > 0);
        Assert.DoesNotContain(SKFontManager.Default.FontFamilies, family => family.Equals(FontFamily, StringComparison.OrdinalIgnoreCase));
        var package = Package("m09.fixed-font", new() { ["fonts/fixed-cjk.ttc"] = ("font", bytes) }, [], []);
        Assert.Equal(EffectPackageProfiles.V2, package.Profile);
        Assert.Equal(bytes, Assert.Single(package.Files).Content.ToArray());
        using var fixedFonts = new SkiaTextLayoutEngine([bytes]);
        using var systemFonts = new SkiaTextLayoutEngine();
        Assert.Equal(FontFamily, fixedFonts.ResolveFamilyName(FontFamily));
        Assert.NotEqual(FontFamily, systemFonts.ResolveFamilyName(FontFamily));
        foreach (string text in new[] { "歌词", "かし" })
        {
            var shaped = fixedFonts.Shape(text, new() { FamilyName = FontFamily, FontSizePx = 40 });
            Assert.Equal(0, shaped.MissingGlyphCount); Assert.NotEmpty(shaped.DrawUnits);
            Assert.All(shaped.DrawUnits, unit => Assert.Equal(FontFamily, unit.FamilyName));
        }
        var project = TextProject() with { Effects = new([package], []),
            LyricStyle = TextProject().LyricStyle with { FontFamilyName = FontFamily } };
        var snapshot = ProjectSnapshot.FromProject(project);
        var factory = Factory(temp, videos, backend);
        await using var session = await factory.CreateAsync(snapshot);
        var request = RenderRequest.At(snapshot, 1_250_000) with { TransparentBackground = true };
        using var cpu = new CpuSkiaRenderDevice();
        using var reference = new SkiaRenderPipeline(fixedFonts, null, null, null, false, cpu, false);
        using var expected = reference.Render(request); using var actual = await session.RenderAsync(request);
        Close(expected.PixelMemory.ToArray(), actual.PixelMemory.ToArray(), 2);
        Assert.True(actual.PixelMemory.ToArray().Where((_, i) => i % 4 == 3).Count(a => a > 0) > 100);
        var scene = reference.GetSceneFrame(snapshot, request.TimeUs);
        var ruby = Assert.Single(scene.Elements.Where(item => item.SourceBinding.RubyId == "ruby"));
        Assert.NotEmpty(ruby.WorldCorners);
        int left = (int)Math.Floor(ruby.WorldCorners.Min(p => p.X)), right = (int)Math.Ceiling(ruby.WorldCorners.Max(p => p.X));
        int top = (int)Math.Floor(ruby.WorldCorners.Min(p => p.Y)), bottom = (int)Math.Ceiling(ruby.WorldCorners.Max(p => p.Y));
        int visibleRuby = 0;
        for (int y = Math.Max(0, top); y < Math.Min(actual.Height, bottom); y++)
            for (int x = Math.Max(0, left); x < Math.Min(actual.Width, right); x++)
                if (Pixel(actual, x, y).Alpha > 0) visibleRuby++;
        Assert.True(visibleRuby > 10);
        await using var reopened = await factory.CreateAsync(snapshot);
        using var replay = await reopened.RenderAsync(request);
        Assert.Equal(actual.PixelMemory.ToArray(), replay.PixelMemory.ToArray());
    }

    [Theory]
    [Trait("Category", "GpuSmoke")]
    [InlineData(320, 180)] [InlineData(640, 360)] [InlineData(640, 180)]
    public async Task 普通ImageHandle经真实v3声明上传裁片完整仿射Tint与独立CPU多边形一致(int width, int height)
    {
        if (!GpuRequested) return;
        using TemporaryDirectory temp = new(); using FfmpegVideoFrameProvider videos = new();
        var package = SpritePackage();
        var snapshot = EmptySnapshot(package);
        await using var session = await Factory(temp, videos, "vulkan").CreateAsync(snapshot);
        var request = RenderRequest.At(snapshot, 1_250_000) with { TransparentBackground = true,
            Target = new() { PixelWidth = width, PixelHeight = height } };
        using var actual = await session.RenderAsync(request);
        // CPU oracle paints constant cropped colors as polygons. It does not use Sprite,
        // ImageHandle, DrawImage/DrawAtlas, the v3 evaluator, or the production affine mapper.
        using var srgb = SKColorSpace.CreateSrgb();
        using var oracle = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul, srgb));
        using (var canvas = new SKCanvas(oracle))
        {
            canvas.Clear(SKColors.Transparent); canvas.Scale(width / 320f, height / 180f);
            Polygon(canvas, [new(20, 20), new(60, 30), new(72, 54), new(32, 44)], new(100, 100, 25, 64));
            Polygon(canvas, [new(120, 20), new(120, 50), new(100, 50), new(100, 20)], new(20, 40, 240, 255));
            Polygon(canvas, [new(200, 20), new(170, 28), new(176, 46), new(206, 38)], new(20, 40, 240, 255));
        }
        // AA kernels can differ at an edge. The stable mask comes only from the CPU
        // oracle: all 5x5 neighbors must have identical premultiplied RGBA. This checks
        // every stable interior AND exterior pixel, without selecting by GPU differences.
        int colored = 0, transparent = 0;
        for (int y = 2; y < height - 2; y++) for (int x = 2; x < width - 2; x++)
        {
            var expected = oracle.GetPixel(x, y); bool stable = true;
            for (int dy = -2; dy <= 2 && stable; dy++) for (int dx = -2; dx <= 2; dx++)
                if (oracle.GetPixel(x + dx, y + dy) != expected) { stable = false; break; }
            if (!stable) continue;
            var pixel = Pixel(actual, x, y);
            CloseColor(expected, pixel, 2);
            if (expected.Alpha > 0) colored++; else transparent++;
        }
        Assert.True(colored > 400); Assert.True(transparent > width * height / 2);
        Assert.Empty(actual.ImageEffectDiagnostics); Assert.Empty(actual.MaterialDiagnostics);
        using var repeat = await session.RenderAsync(request with { TimeUs = 0 });
        Assert.Equal(actual.PixelMemory.ToArray(), repeat.PixelMemory.ToArray());
    }

    [Fact]
    [Trait("Category", "GpuSmoke")]
    public async Task 当前v3主画布BackgroundMedia与Below分别响应媒体与下层且透明回退带真实诊断()
    {
        if (!GpuRequested) return;
        using TemporaryDirectory temp = new(); using FfmpegVideoFrameProvider videos = new();
        string red = WritePng(temp.FilePath("red.png"), SKColors.Red), blue = WritePng(temp.FilePath("blue.png"), SKColors.Blue);
        // A different real v3 controller owns the full-canvas BehindText sprite.
        var green = BackgroundOverlay(SKColors.Lime);
        var yellow = BackgroundOverlay(SKColors.Yellow);
        using var cpu = new CpuSkiaRenderDevice(); using var maskRenderer = new SkiaRenderPipeline(null, null, null, null, false, cpu, false);
        var plain = ProjectSnapshot.FromProject(TextProject());
        using var mask = maskRenderer.Render(RenderRequest.At(plain, 1_250_000) with { TransparentBackground = true });
        var inside = Enumerable.Range(0, mask.Width * mask.Height).Where(i => Pixel(mask, i % mask.Width, i / mask.Width).Alpha == 255).ToArray();
        Assert.True(inside.Length > 30);
        foreach (bool media in new[] { true, false })
        {
            var material = BackgroundMaterial(media);
            var redGreen = MaterialSnapshot(material, green, red);
            var blueGreen = MaterialSnapshot(material, green, blue);
            var redYellow = MaterialSnapshot(material, yellow, red);
            using var first = await Render(redGreen, false);
            using var changedMedia = await Render(blueGreen, false);
            using var changedBelow = await Render(redYellow, false);
            foreach (int index in inside)
            {
                int x = index % mask.Width, y = index / mask.Width;
                CloseColor(media ? SKColors.Red : SKColors.Lime, Pixel(first, x, y), 2);
                CloseColor(media ? SKColors.Blue : SKColors.Lime, Pixel(changedMedia, x, y), 2);
                CloseColor(media ? SKColors.Red : SKColors.Yellow, Pixel(changedBelow, x, y), 2);
            }
            Assert.Empty(first.MaterialDiagnostics);
            using var transparent = await Render(redGreen, true);
            foreach (int index in inside) CloseColor(media ? SKColors.White : SKColors.Lime,
                Pixel(transparent, index % mask.Width, index / mask.Width), 2);
            if (media)
            {
                Assert.NotEmpty(transparent.MaterialDiagnostics);
                Assert.All(transparent.MaterialDiagnostics, diagnostic =>
                {
                    Assert.Equal("TransparentBackgroundDependency", diagnostic.Code);
                    Assert.Equal("background", diagnostic.InputId);
                    Assert.IsType<PreparedMaterialOwner.Source>(diagnostic.Owner);
                    Assert.Equal(material.PackageId, diagnostic.Material.Identity.PackageId);
                    Assert.Equal(material.Revision, diagnostic.Material.Identity.Revision);
                });
            }
            else Assert.Empty(transparent.MaterialDiagnostics);
        }
        async Task<RenderFrame> Render(ProjectSnapshot snapshot, bool transparent)
        {
            await using var session = await Factory(temp, videos, "vulkan").CreateAsync(snapshot);
            return await session.RenderAsync(RenderRequest.At(snapshot, 1_250_000) with { TransparentBackground = transparent });
        }
    }

    private static Project TextProject()
    {
        var line = LyricLine.Create("row", "歌词", new(0, 5_000_000), TimingStatus.Edited,
            [LyricToken.Create("token", "歌词", new(0, 5_000_000), TimingStatus.Edited)]) with
            { RubyAnnotations = [RubyAnnotation.Create("ruby", "token", "かし")] };
        return Project.Create("M09 fixed resources", Composition.Create(320, 180, FrameRate.Fps30, outputDurationUs: 5_000_000),
            LyricsDocument.Create([line]), lyricStyle: LyricStyle.Default with { FontFamilyName = "DejaVu Sans", FontSizePx = 40,
                SungColor = RgbaColor.White, UnsungColor = RgbaColor.White, Stroke = null, Shadow = null, Glow = null, Opacity = 1 })
            with { EnterAnimation = null };
    }

    private static ProjectRenderSessionFactory Factory(TemporaryDirectory temp, IVideoFrameProvider videos, string backend)
        => new(new ScriptCompiler(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"), temp.FilePath("cache")),
            videos, renderBackend: backend);

    private static ProjectEffectInstance Instance(EffectPackageContents package) => new()
    { Id = "overlay", Name = "overlay", PackageId = package.PackageId, Revision = package.Revision, ExportId = "controller", Subscription = new("project") };

    private static ProjectSnapshot EmptySnapshot(EffectPackageContents package)
        => ProjectSnapshot.FromProject(Project.Create("M09 sprite", Composition.Create(320, 180, FrameRate.Fps30, outputDurationUs: 5_000_000),
            LyricsDocument.Create([])) with { EnterAnimation = null, Effects = new([package], [Instance(package)]) });

    private static ProjectSnapshot MaterialSnapshot(EffectPackageContents material, EffectPackageContents overlay, string media)
    {
        var project = TextProject();
        var spec = new MaterialSpec.PackageMaterial(material.PackageId, material.Revision, "ink", EffectParameterOverrides.Parse("{\"amount\":1}"));
        return ProjectSnapshot.FromProject(project with { Background = BackgroundSource.ForImage(media, BackgroundFit.Cover),
            Effects = new([material, overlay], [Instance(overlay)]), LyricStyle = project.LyricStyle with
            { UnsungFill = new() { Material = spec }, SungFill = new() { Material = spec } } });
    }

    private static EffectPackageContents BackgroundMaterial(bool media)
    {
        var original = MixedEffectPackageFixture.Contents(
            manifestChange: manifest => manifest["exports"]![0]!["coverageMode"] = "unmasked",
            pure: true, background: media,
            fallback: new JsonObject { ["kind"] = "solid", ["color"] = "#FFFFFFFF" },
            graphChange: graph =>
            {
                if (!media)
                {
                    var input = EffectPackageGraphFixture.Input("background", "BackdropBelow"); input.Remove("capture");
                    graph["inputs"]!.AsArray().Add(input);
                }
                graph["output"] = "node:probe";
                graph["nodes"]![0]!["children"] = new JsonObject { ["background"] = "input:background" };
            },
            programChange: program => program["children"] = new JsonArray(new JsonObject { ["name"] = "background", ["type"] = "color" }),
            shaderSource: "uniform float amount; uniform shader background; half4 main(float2 p) { return background.eval(p)*amount; }");
        var manifest = JsonNode.Parse(original.Manifest.AsSpan())!.AsObject();
        manifest["packageVersion"] = EffectPackageProfiles.V2.PackageVersion; manifest["sdkVersion"] = EffectPackageProfiles.V2.SdkVersion;
        manifest["shaderProfile"] = EffectPackageProfiles.V2.ShaderProfile; manifest["packageId"] = media ? "m09.media-material" : "m09.below-material";
        manifest["requires"] = new JsonArray("style.fill", "image.graph"); manifest["lineExit"] = new JsonObject { ["durationUs"] = 0 }; manifest["emitters"] = new JsonArray();
        return EffectPackageContents.CreateSupported(EffectPackageFixture.Bytes(manifest), original.Files.Select(file =>
            KeyValuePair.Create(file.Path, (ReadOnlyMemory<byte>)file.Content.ToArray())));
    }

    private static EffectPackageContents SpritePackage() => VisualPackage("m09.image-affine", Atlas(), """
        output.DrawSprites(new("drawing"), [
            new(d.Image, new Affine2(40,10,12,24,20,20), new ColorRgba(.5,1,.5,.5), new Rect2(0,0,.5,1)),
            new(d.Image, new Affine2(0,30,-20,0,120,20), new ColorRgba(1,1,1,1), new Rect2(.5,0,1,1)),
            new(d.Image, new Affine2(-30,8,6,18,200,20), new ColorRgba(1,1,1,1), new Rect2(.5,0,1,1))
        ], OverlayLayer.AboveText);
        """, false);

    private static EffectPackageContents BackgroundOverlay(SKColor color) => VisualPackage("m09.background-overlay", Png(color), """
        output.DrawSprites(new("drawing"), [new(d.Image, new Affine2(320,0,0,180,0,0), new ColorRgba(1,1,1,1))], OverlayLayer.BehindText);
        """, true);

    private static EffectPackageContents VisualPackage(string id, byte[] image, string draw, bool behind)
    {
        string source = """
            using MakoKaraGenerator.Scripting;
            using MakoKaraGenerator.Scripting.V2;
            public sealed record Data(ImageHandle Image) : PreparedEffect;
            public sealed class Probe : IPureKaraokeEffect, IPureVisualEffect
            {
                public PreparedEffect Declare(DeclareContext c) => new Data(c.Resources.GetImage("assets/atlas.png"))
                { VisualOutputs = [new(new("drawing"), VisualPrimitiveKind.Sprites, LAYER)] };
                public void Evaluate(in TextFrame frame, PreparedEffect prepared, TextOutput output) { }
                public void EvaluateVisuals(in VisualFrame frame, PreparedEffect prepared, VisualOutput output)
                { var d = (Data)prepared; DRAW }
            }
            """.Replace("LAYER", behind ? "OverlayLayer.BehindText" : "OverlayLayer.AboveText", StringComparison.Ordinal)
                .Replace("DRAW", draw, StringComparison.Ordinal);
        JsonObject export = new() { ["id"] = "controller", ["kind"] = "controller", ["name"] = "M09 resource probe", ["entryType"] = "Probe",
            ["parameters"] = new JsonArray(), ["bindingKinds"] = new JsonArray("project"), ["outputs"] = new JsonArray(new JsonObject
            { ["id"] = "drawing", ["kind"] = "Overlay", ["target"] = "canvas", ["requires"] = new JsonArray("visuals.sprites"), ["dependsOn"] = new JsonArray() }) };
        return Package(id, new() { ["assets/atlas.png"] = ("image", image), ["src/Probe.cs"] = ("csharp", Encoding.UTF8.GetBytes(source)) },
            ["visuals.sprites"], [export]);
    }

    private static EffectPackageContents Package(string id, Dictionary<string, (string Role, byte[] Bytes)> files, string[] capabilities, JsonObject[] exports)
    {
        JsonObject manifest = new() { ["packageVersion"] = EffectPackageProfiles.V2.PackageVersion,
            ["sdkVersion"] = EffectPackageProfiles.V2.SdkVersion, ["shaderProfile"] = EffectPackageProfiles.V2.ShaderProfile,
            ["packageId"] = id, ["name"] = id, ["version"] = "2.0.0", ["parameters"] = new JsonArray(),
            ["requires"] = new JsonArray(capabilities.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()),
            ["lineExit"] = new JsonObject { ["durationUs"] = 0 }, ["emitters"] = new JsonArray(),
            ["exports"] = new JsonArray(exports.Select(value => (JsonNode?)value).ToArray()),
            ["files"] = new JsonArray(files.Select(pair => (JsonNode?)new JsonObject { ["path"] = pair.Key, ["role"] = pair.Value.Role,
                ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(pair.Value.Bytes)) }).ToArray()) };
        return EffectPackageContents.CreateSupported(EffectPackageFixture.Bytes(manifest), files.Select(pair =>
            KeyValuePair.Create(pair.Key, (ReadOnlyMemory<byte>)pair.Value.Bytes)));
    }

    private static byte[] Atlas()
    {
        using var bitmap = new SKBitmap(8, 4); bitmap.Erase(new(200, 100, 50, 128));
        using var canvas = new SKCanvas(bitmap); using var paint = new SKPaint { Color = new(20, 40, 240, 255) };
        canvas.DrawRect(4, 0, 4, 4, paint); using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100); return data.ToArray();
    }
    private static byte[] Png(SKColor color)
    { using var bitmap = new SKBitmap(8, 8); bitmap.Erase(color); using var image = SKImage.FromBitmap(bitmap); using var data = image.Encode(SKEncodedImageFormat.Png, 100); return data.ToArray(); }
    private static string WritePng(string path, SKColor color) { File.WriteAllBytes(path, Png(color)); return path; }
    private static void Polygon(SKCanvas canvas, SKPoint[] points, SKColor color)
    { using var path = new SKPath(); path.AddPoly(points, true); using var paint = new SKPaint { Color = color, IsAntialias = true }; canvas.DrawPath(path, paint); }
    private static RgbaColor Pixel(RenderFrame frame, int x, int y) { Assert.True(frame.TryGetPixel(x, y, out var pixel)); return pixel; }
    private static void CloseColor(SKColor expected, RgbaColor actual, int tolerance)
    {
        Assert.InRange(Math.Abs(expected.Alpha - actual.Alpha), 0, tolerance);
        // Compare premultiplied channels: low-alpha unpremultiplication can amplify rounding.
        Assert.InRange(Math.Abs(expected.Red * expected.Alpha / 255d - actual.Red * actual.Alpha / 255d), 0, tolerance);
        Assert.InRange(Math.Abs(expected.Green * expected.Alpha / 255d - actual.Green * actual.Alpha / 255d), 0, tolerance);
        Assert.InRange(Math.Abs(expected.Blue * expected.Alpha / 255d - actual.Blue * actual.Alpha / 255d), 0, tolerance);
    }
    private static void Close(byte[] expected, byte[] actual, int tolerance)
    { Assert.Equal(expected.Length, actual.Length); for (int i = 0; i < expected.Length; i++) Assert.InRange(Math.Abs(expected[i] - actual[i]), 0, tolerance); }
}
