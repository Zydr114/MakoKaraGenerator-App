using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering.Skia;
using SkiaSharp;
using Fx = MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed partial class MainCanvasImageEffectsTests
{
    [Theory]
    [InlineData("dot")] [InlineData("segment")] [InlineData("trail")]
    [InlineData("sprite")] [InlineData("path")] [InlineData("atlas")]
    public void B6_六类实际图元按owner过滤并复用生产渲染与真实资源(string kind)
    {
        var request = OwnedRequest(); using var graph = OwnedProbe("a", 480);
        using var device = new CpuSkiaRenderDevice(); using var budget = new ImageSurfaceBudgetSession();
        var contents = OwnedSvgPackage(); var svgDefinition = EffectPackageSvgResources.Read(contents);
        using var package = PackageSvgResources.LoadReferenceWithSession(contents, svgDefinition, SvgResourceBudget.Default, budget, null, default);
        using var svg = new SceneSvgEffectResources([package], device);
        var svgCatalog = new Fx.ResourceCatalog(contents.PackageId, contents.Revision, [], [], [], ["vector"],
            ImmutableDictionary<string, ImmutableArray<string>>.Empty.Add("atlas", ["fixed"]));
        var imageHandle = new Fx.ResourceCatalog("owned-image", "sha256:" + new string('d', 64), [], [], ["white.png"]).GetImage("white.png");
        using var bitmap = new SKBitmap(4, 4); bitmap.Erase(SKColors.White); using var image = SKImage.FromBitmap(bitmap);
        using var input = SkiaGraphSurface.Borrow(image, new(new(0, 0, 4, 4), GraphCoordinateSpace.Canvas, SceneMatrix.Identity, 1, 1), ShaderSurfaceKind.Color);
        using var resources = new SceneImageEffectResources([OwnedProgram("a", graph)], null, [], null, [KeyValuePair.Create(imageHandle, input)]);
        using var renderer = new SkiaRenderPipeline(null, null, budget, resources, false, device, false, svgEffects: svg);
        Fx.OverlayBatch Batch(string owner, Fx.ColorRgba tint) => kind switch
        {
            "dot" => OwnedDot(owner, "dot-" + owner, tint),
            "segment" => new Fx.SegmentBatch(new(owner), new("segment-" + owner), Fx.OverlayLayer.BehindText, [new(new(20, 20), new(40, 20), 12, tint)]),
            "trail" => new Fx.TrailBatch(new(owner), new("trail-" + owner), Fx.OverlayLayer.BehindText,
                [new([new(new(20, 20), 12, tint), new(new(40, 20), 12, tint)])]),
            "sprite" => new Fx.SpriteBatch(new(owner), new("sprite-" + owner), Fx.OverlayLayer.BehindText,
                [new(imageHandle, new(20, 0, 0, 20, 20, 10), tint)]),
            "path" => new Fx.PathBatch(new(owner), new("path-" + owner), Fx.OverlayLayer.BehindText,
                [new(svgCatalog.GetVector("vector"), new(20, 0, 0, 20, 20, 10), tint)]),
            "atlas" => new Fx.AtlasSpriteBatch(new(owner), new("atlas-" + owner), Fx.OverlayLayer.BehindText,
                [new(svgCatalog.GetSprite("atlas", "fixed"), new(20, 0, 0, 20, 20, 10), tint)]),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        var other = Batch("other", new(0, 1, 0, .5)); var owned = Batch("read", new(1, 0, 0, 1));
        using var expected = renderer.Render(request with { PreparedFrame = request.PreparedFrame!.WithOverlayOutput([other], []) });
        using var actual = renderer.Render(request with { PreparedFrame = request.PreparedFrame!.WithOverlayOutput([other, owned], [])
            .WithImageEffects([OwnedBind("read", Line(request), "a")]) });
        Close(OwnedPixel(expected, 30, 20), OwnedPixel(actual, 484, 20), 3);
        Assert.True(OwnedPixel(actual, 484, 20)[1] > 80); Assert.True(OwnedPixel(actual, 484, 20)[2] < 5);
        Assert.True(OwnedPixel(actual, 30, 20)[2] > 240); // Actual main retains owned red.
        Assert.Empty(actual.ImageEffectDiagnostics);
    }

    [Fact]
    public void B7_重绘SVG实际Native工作与原BehindText累计预算且归图元owner()
    {
        var request = OwnedRequest(); using var probe = OwnedProbe("a", 480);
        using var device = new CpuSkiaRenderDevice(); using var budget = new ImageSurfaceBudgetSession();
        var contents = OwnedSvgPackage(false); var definition = EffectPackageSvgResources.Read(contents);
        using var package = PackageSvgResources.LoadReferenceWithSession(contents, definition, SvgResourceBudget.Default, budget, null, default);
        var catalog = new Fx.ResourceCatalog(contents.PackageId, contents.Revision, [], [], [], ["vector"], ImmutableDictionary<string, ImmutableArray<string>>.Empty);
        var vector = package.GetVector(catalog.GetVector("vector").Identity);
        long cost = Math.Max(vector.RecordedOperations, vector.Inspection.EstimatedPictureOperations) + vector.Inspection.ExpandedPathCommands;
        // Two real batches fit, first non-excluded replay crosses the same limit.
        using var svg = new SceneSvgEffectResources([package], device, checked(cost * 2));
        using var resources = new SceneImageEffectResources([OwnedProgram("a", probe)]);
        using var renderer = new SkiaRenderPipeline(null, null, budget, resources, false, device, false, svgEffects: svg);
        Fx.PathBatch Path(string owner) => new(new(owner), new(owner), Fx.OverlayLayer.BehindText,
            [new(catalog.GetVector("vector"), new(20, 0, 0, 20, 20, 10), new(1, 1, 1, 1))]);
        var overlayOwner = new PreparedOverlayOwner(new("other"), "overlay-artifact", new(contents.PackageId, contents.Revision, "overlay-controller"));
        var prepared = request.PreparedFrame!.WithOverlayOutput([Path("read"), Path("other")], [], owners: [overlayOwner])
            .WithImageEffects([OwnedBind("read", Line(request), "a")]);
        var error = Assert.Throws<EffectFailureException>(() => renderer.Render(request with { PreparedFrame = prepared }));
        Assert.Equal("SvgRenderBudgetExceeded", error.Code); Assert.Equal("RenderOverlays", error.Stage);
        Assert.Equal(overlayOwner.InstanceId, error.InstanceId); Assert.Equal(overlayOwner.ArtifactHash, error.ArtifactHash);
        Assert.Equal(0, budget.Usage.ActiveSurfaces); Assert.Equal(0, budget.Usage.EstimatedBytes);
    }

    [Fact]
    public void B8_六类累计重绘预算不按owner分桶或静默截断且释放帧表面()
    {
        var request = OwnedRequest(); using var probe = OwnedProbe("a", 480);
        using var resources = new SceneImageEffectResources([OwnedProgram("a", probe)]);
        using var budget = new ImageSurfaceBudgetSession(); using var renderer = new SkiaRenderPipeline(null, null, budget, resources);
        var overlayOwner = new PreparedOverlayOwner(new("other"), "overlay-artifact", null);
        // Original draw is valid; repeated actual replay has its own finite total.
        var batches = Enumerable.Range(0, 4097).Select(i => (Fx.OverlayBatch)OwnedDot("other", "many-" + i, new(0, 1, 0, 1))).Prepend(
            OwnedDot("read", "own", new(1, 0, 0, 1))).ToImmutableArray();
        var prepared = request.PreparedFrame!.WithOverlayOutput(batches, [], owners: [overlayOwner])
            .WithImageEffects([OwnedBind("read", Line(request), "a")]);
        var error = Assert.Throws<EffectFailureException>(() => renderer.Render(request with { PreparedFrame = prepared }));
        Assert.Equal("GroupCompositionBudgetExceeded", error.Code); Assert.Equal(overlayOwner.InstanceId, error.InstanceId);
        Assert.Equal(0, budget.Usage.ActiveSurfaces); Assert.Equal(0, budget.Usage.EstimatedBytes);
    }

    private static EffectPackageContents OwnedSvgPackage(bool atlas = true)
    {
        Dictionary<string, byte[]> files = new(StringComparer.Ordinal)
        { ["assets/white.svg"] = Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 100 100'><rect width='100' height='100' fill='white'/></svg>") };
        foreach (string mode in atlas ? new[] { "vector", "atlas" } : new[] { "vector" })
        {
            JsonObject descriptor = new()
            {
                ["svgResourceVersion"] = 1, ["id"] = mode, ["source"] = "assets/white.svg", ["profile"] = EffectPackageSvgResources.StaticProfile,
                ["mode"] = mode, ["viewport"] = new JsonObject { ["width"] = 100, ["height"] = 100 }, ["fonts"] = new JsonArray(), ["images"] = new JsonArray(),
            };
            if (mode == "atlas") descriptor["atlas"] = new JsonObject
            { ["variants"] = new JsonArray(new JsonObject { ["id"] = "fixed", ["width"] = 100, ["height"] = 100 }), ["paddingPx"] = 4, ["mipmaps"] = true };
            files.Add("resources/" + mode + ".json", Encoding.UTF8.GetBytes(descriptor.ToJsonString()));
        }
        JsonArray entries = [];
        foreach (var file in files) entries.Add(new JsonObject { ["path"] = file.Key, ["role"] = file.Key.EndsWith(".svg", StringComparison.Ordinal) ? "svg" : "svgResource",
            ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(file.Value)) });
        JsonObject manifest = new()
        {
            ["packageVersion"] = EffectPackageProfiles.V2.PackageVersion, ["sdkVersion"] = EffectPackageProfiles.V2.SdkVersion, ["shaderProfile"] = EffectPackageProfiles.V2.ShaderProfile,
            ["packageId"] = "owned.below.svg", ["name"] = "Owned Below SVG", ["version"] = "1.0.0",
            ["requires"] = atlas ? new JsonArray(EffectPackageSvgResources.VectorCapability, EffectPackageSvgResources.AtlasCapability) : new JsonArray(EffectPackageSvgResources.VectorCapability),
            ["lineExit"] = new JsonObject { ["durationUs"] = 0 }, ["emitters"] = new JsonArray(), ["files"] = entries, ["parameters"] = new JsonArray(), ["exports"] = new JsonArray(),
        };
        return EffectPackageContents.CreateSupported(Encoding.UTF8.GetBytes(manifest.ToJsonString()), files.Select(file => KeyValuePair.Create(file.Key, (ReadOnlyMemory<byte>)file.Value)));
    }
}
