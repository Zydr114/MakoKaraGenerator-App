using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Rendering.Skia;
using SkiaSharp;
using Fx = MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed class SvgOverlayRenderingTests
{
    private static readonly Fx.ColorRgba White = new(1, 1, 1, 1);
    private static readonly Fx.Affine2 Placement = new(100, 0, 0, 100, 40, 20);

    [Theory]
    [InlineData("path", false)] [InlineData("path", true)]
    [InlineData("gradient-clip-use", false)] [InlineData("gradient-clip-use", true)]
    [InlineData("image", false)] [InlineData("image", true)]
    [InlineData("filter", false)] [InlineData("filter", true)]
    public void 四类真实SVG分别经矢量与atlas生产overlay产生有限透明像素(string kind, bool atlas)
    {
        using var fixture = new Fixture(Package(Body(kind)));
        using var frame = fixture.Render(fixture.Batch(atlas, Placement, White));
        using var baseline = fixture.Render();
        Assert.True(Different(frame, baseline) > 100);
        Pixel(frame, 35, 15, 0, 0, 0, 0);
        Assert.Equal(fixture.FixedBytes, fixture.Budget.Usage.EstimatedBytes);
        Assert.Equal(0, fixture.FrameInputsAfterRender);
        if (kind == "path") { Pixel(frame, 90, 70, 224, 64, 32, 255); Pixel(frame, 45, 25, 0, 0, 0, 0); }
        if (kind == "gradient-clip-use")
        {
            Pixel(frame, 42, 22, 0, 0, 0, 0);
            Assert.True(frame.TryGetPixel(90, 70, out var center)); Assert.InRange(center.Alpha, 185, 195);
        }
        if (kind == "image") Pixel(frame, 90, 70, 20, 180, 80, 255);
        if (kind == "filter")
        {
            Assert.True(frame.TryGetPixel(90, 70, out var center)); Assert.True(center.Alpha > 240);
            Assert.True(frame.TryGetPixel(62, 70, out var blur)); Assert.True(blur.Alpha > 0);
        }
    }

    [Theory]
    [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public void 完整斜切镜像仿射和整体Tint只乘一次且重叠不增Alpha(bool atlas, bool mirror)
    {
        using var fixture = new Fixture(Package(Overlap));
        Fx.Affine2 matrix = mirror ? new(-100, 25, 30, 70, 160, 20) : new(100, 25, 30, 70, 40, 20);
        using var frame = fixture.Render(fixture.Batch(atlas, matrix, new(.5, 1, .5, .5)));
        foreach (double x in new[] { .15, .5, .85 })
        {
            var point = matrix.Map(new(x, .5)); Pixel(frame, (int)point.X, (int)point.Y, 100, 100, 25, 128);
        }
        var outside = matrix.Map(new(-.1, .5)); Pixel(frame, (int)outside.X, (int)outside.Y, 0, 0, 0, 0);
        Assert.Equal(fixture.FixedBytes, fixture.Budget.Usage.EstimatedBytes);
    }

    [Fact]
    public void 场景lease精确修订档位与设备关闭检查并保留真实作者失败()
    {
        using var fixture = new Fixture(Package(Overlap));
        var batch = fixture.Batch(false, Placement, White);
        fixture.PackageOwner.Dispose();
        Assert.True(fixture.PackageOwner.IsRetiring); Assert.False(fixture.PackageOwner.IsDisposed);
        using (var frame = fixture.Render(batch)) Pixel(frame, 90, 70, 200, 100, 50, 255);
        var foreign = Catalog(Package("<rect width='100' height='100' fill='blue'/>")).GetVector("vector");
        var invalid = new Fx.PathBatch(fixture.Owner.InstanceId, new("foreign"), Fx.OverlayLayer.AboveText, [new(foreign, Placement, White)]);
        AssertOwner(fixture, Assert.Throws<EffectFailureException>(() => fixture.Render(invalid)), "SvgVectorResourceUnavailable");
        var wrongVariant = new Fx.ResourceCatalog(fixture.Catalog.PackageId!, fixture.Catalog.Revision!, [], [], [], [],
            ImmutableDictionary<string, ImmutableArray<string>>.Empty.Add("atlas", ["other"])).GetSprite("atlas", "other");
        var invalidAtlas = new Fx.AtlasSpriteBatch(fixture.Owner.InstanceId, new("foreign-variant"), Fx.OverlayLayer.AboveText,
            [new(wrongVariant, Placement, White)]);
        AssertOwner(fixture, Assert.Throws<EffectFailureException>(() => fixture.Render(invalidAtlas)), "SvgAtlasResourceUnavailable");
        using var otherDevice = new CpuSkiaRenderDevice();
        Assert.Equal("SvgDeviceMismatch", Assert.Throws<PreparedFrameException>(() => fixture.Scene.GetVector(fixture.Catalog.GetVector("vector"), otherDevice)).Code);
        Assert.Equal("SvgDeviceMismatch", Assert.Throws<PreparedFrameException>(() => fixture.Scene.GetSprite(fixture.Catalog.GetSprite("atlas", "fixed"), otherDevice)).Code);
        fixture.Scene.Dispose(); Assert.True(fixture.PackageOwner.IsDisposed);
        AssertOwner(fixture, Assert.Throws<EffectFailureException>(() => fixture.Render(batch)), "OverlayRenderFailed");
    }

    [Fact]
    public void 缺SVG集合和极端精度失败都保留作者及既有stage()
    {
        var contents = Package(Overlap); var catalog = Catalog(contents);
        using var renderer = new SkiaRenderPipeline(); var snapshot = VisualTestFixture.Snapshot();
        var owner = new PreparedOverlayOwner(new("svg"), "artifact-svg", new(contents.PackageId, contents.Revision, "controller"));
        PreparedRenderFrame basis = new(snapshot, 18_001, Fx.OutputMode.Transparent, VisualTestFixture.Target(snapshot), renderer.GetSceneFrame(snapshot, 18_001), []);
        foreach (bool atlas in new[] { false, true })
        {
            Fx.OverlayBatch batch = atlas
                ? new Fx.AtlasSpriteBatch(owner.InstanceId, new("atlas"), Fx.OverlayLayer.AboveText, [new(catalog.GetSprite("atlas", "fixed"), Placement, White)])
                : new Fx.PathBatch(owner.InstanceId, new("path"), Fx.OverlayLayer.AboveText, [new(catalog.GetVector("vector"), Placement, White)]);
            var prepared = basis.WithOverlayOutput([batch], [], owners: [owner]);
            var error = Assert.Throws<EffectFailureException>(() => renderer.Render(RenderRequest.At(snapshot, basis.TimeUs) with { PreparedFrame = prepared, TransparentBackground = true }));
            Assert.Equal("SvgResourcesRequired", error.Code); Assert.Equal("RenderOverlays", error.Stage); Assert.Equal(owner.PackageIdentity, error.PackageIdentity);
        }
        using var fixture = new Fixture(contents);
        var extreme = fixture.Batch(false, new(1e100, 0, 0, 1, 0, 0), White);
        AssertOwner(fixture, Assert.Throws<EffectFailureException>(() => fixture.Render(extreme)), "UnsupportedNativeOverlay");
    }

    [Fact]
    public void 复合Native仿射溢出下溢退化或透视不能通过double账本掩盖()
    {
        foreach (var pair in new[]
        {
            (Canvas: SKMatrix.CreateScale(2, 2), Local: new Fx.Affine2(2e38, 0, 0, 1, 0, 0)),
            (Canvas: SKMatrix.CreateScale(1e-25f, 1e-25f), Local: new Fx.Affine2(1e-25, 0, 0, 1e-25, 0, 0)),
            (Canvas: new SKMatrix(1, 0, 0, 0, 1, 0, .01f, 0, 1), Local: Fx.Affine2.Identity),
        })
            Assert.Equal("UnsupportedNativeOverlay", Assert.Throws<PreparedFrameException>(() => SkiaRenderPipeline.SvgPhysicalMatrix(pair.Canvas, pair.Local)).Code);
        using var fixture = new Fixture(Package(Overlap), target: new RenderTarget { PixelWidth = 1280, PixelHeight = 720 });
        foreach (bool atlas in new[] { false, true })
        {
            var batch = fixture.Batch(atlas, new(2e38, 0, 0, 1, 0, 0), White);
            AssertOwner(fixture, Assert.Throws<EffectFailureException>(() => fixture.Render(batch)), "UnsupportedNativeOverlay");
        }
        Assert.Equal(fixture.FixedBytes, fixture.Budget.Usage.EstimatedBytes);
    }

    [Fact]
    public void 两层累计完整Picture操作预算超限归属跨线实例且后续帧恢复()
    {
        var contents = Package(Overlap, atlas: false);
        using var probe = new Fixture(contents);
        var program = probe.PackageOwner.GetVector(probe.Catalog.GetVector("vector").Identity);
        long cost = Math.Max(program.RecordedOperations, program.Inspection.EstimatedPictureOperations) + program.Inspection.ExpandedPathCommands;
        using var fixture = new Fixture(contents, maximumOperations: checked(cost * 2 - 1));
        var below = fixture.Batch(false, Placement, White, Fx.OverlayLayer.BehindText, "below");
        var above = fixture.Batch(false, Placement, White, Fx.OverlayLayer.AboveText, "above");
        AssertOwner(fixture, Assert.Throws<EffectFailureException>(() => fixture.Render(below, above)), "SvgRenderBudgetExceeded");
        Assert.Equal(0, fixture.Budget.Usage.EstimatedBytes);
        using var recovered = fixture.Render(above); Pixel(recovered, 90, 70, 200, 100, 50, 255);
    }

    [Fact]
    public void 整体Tint使用共享表面账且失败取消后归零并允许下一合法帧()
    {
        using var fixture = new Fixture(Package(Overlap, atlas: false), surfaceLimits: new() { MaximumBytes = 10_000 });
        var tinted = fixture.Batch(false, Placement, new(1, 1, 1, .5));
        AssertOwner(fixture, Assert.Throws<EffectFailureException>(() => fixture.Render(tinted)), "ImageSurfaceBudgetExceeded");
        Assert.Equal(0, fixture.Budget.Usage.EstimatedBytes);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => fixture.RenderWithToken(cancellation.Token, tinted));
        Assert.Equal(0, fixture.Budget.Usage.EstimatedBytes);
        using var recovered = fixture.Render(fixture.Batch(false, new(40, 0, 0, 40, 40, 20), new(1, 1, 1, .5)));
        Pixel(recovered, 60, 40, 200, 100, 50, 128); Assert.Equal(0, fixture.Budget.Usage.EstimatedBytes);
        Assert.True(fixture.Budget.Usage.PeakBytes > 0);
    }

    [Fact]
    public void Filter按完整非等比输出密度平方计累计预算且透明Tint不能绕过()
    {
        using var fixture = new Fixture(Package(Body("filter"), atlas: false), maximumFilterBytes: 1);
        var filtered = fixture.Batch(false, new(200, 80, 100, 100, 40, 20), new(1, 1, 1, 0));
        AssertOwner(fixture, Assert.Throws<EffectFailureException>(() => fixture.Render(filtered)), "SvgRenderBudgetExceeded");
        Assert.Equal(0, fixture.Budget.Usage.EstimatedBytes);
    }

    [Fact]
    public void Vulkan真实mipAtlas经生产仿射overlay缩小细纹且邻页不串色()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("MAKO_GPU_SMOKE"), "1", StringComparison.Ordinal)) return;
        string stripes = string.Concat(Enumerable.Range(0, 100).Select(x => $"<rect x='{x}' width='1' height='100' fill='{(x % 2 == 0 ? "white" : "black")}'/>"));
        var contents = Package(stripes, neighbor: true);
        using var fixture = new Fixture(contents, gpu: true);
        // Avoid a harmonic inverse footprint: (9,2,3,9) steps exactly 12/-4
        // stripe texels per pixel, accidentally hiding no-mip aliasing entirely.
        var matrix = new Fx.Affine2(11, 2, 3, 9, 40, 20);
        Fx.OverlayBatch neighbor = new Fx.AtlasSpriteBatch(fixture.Owner.InstanceId, new("neighbor"), Fx.OverlayLayer.AboveText,
            [new(fixture.Catalog.GetSprite("neighbor", "fixed"), new(9, 2, 3, 9, 54, 20), White)]);
        using var frame = fixture.Render(fixture.Batch(true, matrix, White), neighbor);
        var page = fixture.PackageOwner.GetSprite(fixture.Catalog.GetSprite("atlas", "fixed").Identity, "fixed");
        Assert.True(page.IsTextureBacked); Assert.True(page.MipmapsRequested); Assert.Equal(page.Plan.MipBytes, page.StorageBytes);
        foreach (double x in new[] { .3, .5, .7 })
        {
            var point = matrix.Map(new(x, .5)); Assert.True(frame.TryGetPixel((int)point.X, (int)point.Y, out var pixel));
            Assert.InRange(pixel.Red, 70, 185); Assert.InRange(pixel.Green, 70, 185); Assert.InRange(pixel.Blue, 70, 185);
            Assert.InRange(Math.Abs(pixel.Red - pixel.Green), 0, 2); Assert.InRange(Math.Abs(pixel.Red - pixel.Blue), 0, 2);
        }
        Pixel(frame, 60, 25, 255, 0, 0, 255);
        using var reference = PackageSvgResources.LoadReference(contents, EffectPackageSvgResources.Read(contents));
        var rawPage = reference.GetSprite(fixture.Catalog.GetSprite("atlas", "fixed").Identity, "fixed");
        using var noMip = fixture.Device.CreateTextureImage(rawPage.Image, false); Assert.NotNull(noMip);
        using var noMipPixels = SampleNoMip(fixture.Device, noMip, matrix);
        var positions = Enumerable.Range(2, 7).Select(index => matrix.Map(new(index / 10d, .5))).ToArray();
        double mipVariation = Variation(positions.Select(point => { Assert.True(frame.TryGetPixel((int)point.X, (int)point.Y, out var pixel)); return (double)pixel.Red; }));
        double rawVariation = Variation(positions.Select(point => (double)noMipPixels.GetPixel((int)point.X, (int)point.Y).Red));
        Assert.True(mipVariation + 5 < rawVariation, $"生产 atlas mip 应改善完整仿射缩小采样：mip={mipVariation:F2}, no-mip={rawVariation:F2}。");
        Assert.Equal(fixture.FixedBytes, fixture.Budget.Usage.EstimatedBytes);
    }

    private static SKBitmap SampleNoMip(ISkiaRenderDevice device, SKImage image, Fx.Affine2 matrix)
    {
        using var srgb = SKColorSpace.CreateSrgb();
        var info = new SKImageInfo(640, 360, SKColorType.Bgra8888, SKAlphaType.Premul, srgb);
        using var surface = device.CreateSurface(info); surface.Canvas.Clear(SKColors.Transparent);
        surface.Canvas.Concat(new SKMatrix((float)matrix.A, (float)matrix.C, (float)matrix.Tx, (float)matrix.B, (float)matrix.D, (float)matrix.Ty, 0, 0, 1));
        surface.Canvas.ClipRect(new(0, 0, 1, 1));
        surface.Canvas.DrawImage(image, new SKRect(-.04f, -.04f, 1.04f, 1.04f), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
        device.FlushAndSubmit(surface);
        var pixels = new SKBitmap(info);
        if (!device.ReadPixels(surface, info, pixels.GetPixels(), pixels.RowBytes)) { pixels.Dispose(); throw new InvalidOperationException("无法读取无 mip 对照。"); }
        return pixels;
    }
    private static double Variation(IEnumerable<double> source)
    {
        double[] values = source.ToArray(); double mean = values.Average(); return Math.Sqrt(values.Average(value => (value - mean) * (value - mean)));
    }

    private static void AssertOwner(Fixture fixture, EffectFailureException error, string code)
    {
        Assert.Equal(code, error.Code); Assert.Equal("RenderOverlays", error.Stage); Assert.Equal(fixture.Owner.InstanceId, error.InstanceId);
        Assert.Equal(fixture.Owner.ArtifactHash, error.ArtifactHash); Assert.Equal(fixture.Owner.PackageIdentity, error.PackageIdentity);
    }
    private static void Pixel(RenderFrame frame, int x, int y, int red, int green, int blue, int alpha)
    {
        Assert.True(frame.TryGetPixel(x, y, out var pixel));
        Assert.InRange(Math.Abs(pixel.Red * pixel.Alpha - red * alpha), 0, 510);
        Assert.InRange(Math.Abs(pixel.Green * pixel.Alpha - green * alpha), 0, 510);
        Assert.InRange(Math.Abs(pixel.Blue * pixel.Alpha - blue * alpha), 0, 510);
        Assert.InRange(Math.Abs(pixel.Alpha - alpha), 0, 2);
    }
    private static int Different(RenderFrame a, RenderFrame b)
    {
        int count = 0; for (int index = 0; index < a.Pixels.Length; index++) if (a.Pixels[index] != b.Pixels[index]) count++; return count;
    }
    private const string Overlap = "<rect width='70' height='100' fill='#c86432'/><rect x='30' width='70' height='100' fill='#c86432'/>";
    private static string Body(string kind) => kind switch
    {
        "path" => "<path d='M10 10 H90 V90 H10 Z' fill='#e04020' stroke='#2040e0' stroke-width='2'/>",
        "gradient-clip-use" => """
            <style>.ink { fill: url(#paint) }</style><defs>
            <linearGradient id="paint" gradientUnits="objectBoundingBox"><stop offset="0" stop-color="red"/><stop offset="1" stop-color="blue"/></linearGradient>
            <clipPath id="clip" clipPathUnits="userSpaceOnUse"><circle cx="50" cy="50" r="35"/></clipPath>
            <symbol id="tile" viewBox="0 0 10 10"><rect class="ink" width="10" height="10"/></symbol>
            </defs><g clip-path="url(#clip)" opacity="0.75"><use href="#tile" x="10" y="10" width="80" height="80"/></g>
            """,
        "image" => "<image href='data:image/png;base64," + Convert.ToBase64String(Png()) + "' width='100' height='100' preserveAspectRatio='none'/>",
        "filter" => """
            <defs><filter id="f" filterUnits="userSpaceOnUse" primitiveUnits="userSpaceOnUse" x="0" y="0" width="100" height="100">
            <feGaussianBlur in="SourceGraphic" stdDeviation="3" result="blur"/><feMerge><feMergeNode in="blur"/><feMergeNode in="SourceGraphic"/></feMerge>
            </filter></defs><rect x="25" y="25" width="50" height="50" fill="#2070e0" filter="url(#f)"/>
            """,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
    private static byte[] Png()
    {
        using var bitmap = new SKBitmap(2, 2); bitmap.Erase(new SKColor(20, 180, 80, 255));
        using var image = SKImage.FromBitmap(bitmap); using var encoded = image.Encode(SKEncodedImageFormat.Png, 100); return encoded.ToArray();
    }
    private static Fx.ResourceCatalog Catalog(EffectPackageContents contents)
    {
        var resources = EffectPackageSvgResources.Read(contents);
        return new(contents.PackageId, contents.Revision, [], [], [], [.. resources.ById.Values.Where(value => value.Mode == SvgResourceMode.Vector).Select(value => value.Id)],
            resources.ById.Values.Where(value => value.Mode == SvgResourceMode.Atlas).ToImmutableDictionary(value => value.Id, value => value.Variants.Select(variant => variant.Id).ToImmutableArray(), StringComparer.Ordinal));
    }
    private static EffectPackageContents Package(string body, bool atlas = true, bool neighbor = false)
    {
        Dictionary<string, byte[]> files = new(StringComparer.Ordinal)
        { ["assets/main.svg"] = Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 100 100'>" + body + "</svg>") };
        foreach (string mode in atlas ? new[] { "vector", "atlas" } : new[] { "vector" })
        {
            JsonObject descriptor = new()
            {
                ["svgResourceVersion"] = 1, ["id"] = mode, ["source"] = "assets/main.svg", ["profile"] = EffectPackageSvgResources.StaticProfile,
                ["mode"] = mode, ["viewport"] = new JsonObject { ["width"] = 100, ["height"] = 100 }, ["fonts"] = new JsonArray(), ["images"] = new JsonArray(),
            };
            if (mode == "atlas") descriptor["atlas"] = new JsonObject
            {
                ["variants"] = new JsonArray(new JsonObject { ["id"] = "fixed", ["width"] = 100, ["height"] = 100 }), ["paddingPx"] = 4, ["mipmaps"] = true,
            };
            files.Add("resources/" + mode + ".json", Encoding.UTF8.GetBytes(descriptor.ToJsonString()));
        }
        if (neighbor)
        {
            files.Add("assets/neighbor.svg", Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 100 100'><rect width='100' height='100' fill='red'/></svg>"));
            var descriptor = JsonNode.Parse(files["resources/atlas.json"])!.AsObject(); descriptor["id"] = "neighbor"; descriptor["source"] = "assets/neighbor.svg";
            files.Add("resources/neighbor.json", Encoding.UTF8.GetBytes(descriptor.ToJsonString()));
        }
        JsonArray entries = [];
        foreach (var pair in files) entries.Add(new JsonObject { ["path"] = pair.Key, ["role"] = pair.Key.EndsWith(".svg", StringComparison.Ordinal) ? "svg" : "svgResource", ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(pair.Value)) });
        JsonObject manifest = new()
        {
            ["packageVersion"] = EffectPackageProfiles.V2.PackageVersion, ["sdkVersion"] = EffectPackageProfiles.V2.SdkVersion, ["shaderProfile"] = EffectPackageProfiles.V2.ShaderProfile,
            ["packageId"] = "svg.overlay.tests", ["name"] = "SVG overlay", ["version"] = "1.0.0", ["requires"] = atlas
                ? new JsonArray(EffectPackageSvgResources.VectorCapability, EffectPackageSvgResources.AtlasCapability) : new JsonArray(EffectPackageSvgResources.VectorCapability),
            ["lineExit"] = new JsonObject { ["durationUs"] = 0 }, ["emitters"] = new JsonArray(), ["files"] = entries, ["parameters"] = new JsonArray(), ["exports"] = new JsonArray(),
        };
        return EffectPackageContents.CreateSupported(Encoding.UTF8.GetBytes(manifest.ToJsonString()), files.Select(pair => KeyValuePair.Create(pair.Key, (ReadOnlyMemory<byte>)pair.Value)));
    }

    private sealed class Fixture : IDisposable
    {
        internal Fixture(EffectPackageContents contents, GraphExecutionBudget? surfaceLimits = null, long maximumOperations = 200_000,
            long maximumFilterBytes = 128L * 1024 * 1024, bool gpu = false, RenderTarget? target = null)
        {
            List<IDisposable> initialized = [];
            try
            {
                Device = SkiaRenderDeviceFactory.Create(new(gpu ? SkiaRenderBackend.Vulkan : SkiaRenderBackend.Cpu)); initialized.Add(Device);
                Budget = new(surfaceLimits); initialized.Add(Budget); Catalog = SvgOverlayRenderingTests.Catalog(contents);
                PackageOwner = gpu ? PackageSvgResources.LoadWithDevice(contents, EffectPackageSvgResources.Read(contents), SvgResourceBudget.Default, Device, Budget, null, default)
                    : PackageSvgResources.LoadReferenceWithSession(contents, EffectPackageSvgResources.Read(contents), SvgResourceBudget.Default, Budget, null, default);
                initialized.Add(PackageOwner); FixedBytes = Budget.Usage.EstimatedBytes;
                Scene = new([PackageOwner], Device, maximumOperations, maximumFilterBytes); initialized.Add(Scene);
                Renderer = new(null, null, Budget, null, false, Device, false, svgEffects: Scene); initialized.Add(Renderer);
                // Pixel expectations isolate SVG from glyphs underneath translucent tint.
                var snapshot = ProjectSnapshot.FromProject(Project.Create("svg-overlay",
                    Composition.Create(320, 180, FrameRate.Fps30), LyricsDocument.Create([])));
                Basis = new(snapshot, 18_001, Fx.OutputMode.Transparent, target ?? VisualTestFixture.Target(snapshot), Renderer.GetSceneFrame(snapshot, 18_001), []);
                Owner = new(new("svg"), "artifact-svg", new(contents.PackageId, contents.Revision, "controller"));
            }
            catch
            {
                foreach (var item in initialized.AsEnumerable().Reverse())
                    try { item.Dispose(); } catch { /* 保留 fixture 初始化的原始失败。 */ }
                throw;
            }
        }
        internal ISkiaRenderDevice Device { get; }
        internal ImageSurfaceBudgetSession Budget { get; }
        internal PackageSvgResources PackageOwner { get; }
        internal SceneSvgEffectResources Scene { get; }
        internal SkiaRenderPipeline Renderer { get; }
        internal Fx.ResourceCatalog Catalog { get; }
        internal PreparedRenderFrame Basis { get; }
        internal PreparedOverlayOwner Owner { get; }
        internal long FixedBytes { get; }
        internal long FrameInputsAfterRender => Budget.Usage.EstimatedBytes - FixedBytes;
        internal Fx.OverlayBatch Batch(bool atlas, Fx.Affine2 placement, Fx.ColorRgba tint, Fx.OverlayLayer layer = Fx.OverlayLayer.AboveText, string id = "svg")
            => atlas ? new Fx.AtlasSpriteBatch(Owner.InstanceId, new(id), layer, [new(Catalog.GetSprite("atlas", "fixed"), placement, tint)])
                : new Fx.PathBatch(Owner.InstanceId, new(id), layer, [new(Catalog.GetVector("vector"), placement, tint)]);
        internal RenderFrame Render(params Fx.OverlayBatch[] batches) => RenderWithToken(default, batches);
        internal RenderFrame RenderWithToken(CancellationToken token, params Fx.OverlayBatch[] batches)
        {
            var prepared = Basis.WithOverlayOutput([.. batches], [], owners: [Owner]);
            return Renderer.Render(RenderRequest.At(prepared.Snapshot, Basis.TimeUs) with { Target = Basis.Target, PreparedFrame = prepared, TransparentBackground = true, CancellationToken = token });
        }
        public void Dispose() { Renderer.Dispose(); Scene.Dispose(); PackageOwner.Dispose(); Budget.Dispose(); Device.Dispose(); }
    }
}
