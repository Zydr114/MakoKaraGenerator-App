using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Scripting;
using SkiaSharp;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed class PackageSvgFrameBorrowTests
{
    [Fact]
    public void 矢量逐帧只借实际依赖并与原role图片去重且退役场景仍可借用()
    {
        var contents = Package(); using var surfaces = new ImageSurfaceBudgetSession();
        using var images = PackageGraphImages.Load(contents);
        using var owner = PackageSvgResources.LoadReferenceWithSession(contents, EffectPackageSvgResources.Read(contents),
            SvgResourceBudget.Default, surfaces, images, default);
        using var scene = owner.AcquireLease(); using var frame = surfaces.BeginFrame();
        long fixedBytes = surfaces.Usage.EstimatedBytes;
        Assert.Equal(64 + 16 + 256, fixedBytes);
        using (var original = surfaces.Borrow(frame, images.BorrowImage("images/red.png").Handle, 64))
        {
            var first = owner.BorrowVectorInputs(Identity(contents, "a"), frame);
            var second = owner.BorrowVectorInputs(Identity(contents, "a"), frame);
            Assert.Equal(2, frame.Usage.ActiveSurfaces); Assert.Equal(80, frame.Usage.EstimatedBytes);
            Assert.Equal(fixedBytes, surfaces.Usage.EstimatedBytes);
            using (var other = owner.BorrowVectorInputs(Identity(contents, "b"), frame))
            { Assert.Equal(3, frame.Usage.ActiveSurfaces); Assert.Equal(fixedBytes, frame.Usage.EstimatedBytes); }
            first.Dispose(); Assert.Equal(2, frame.Usage.ActiveSurfaces);
            second.Dispose(); Assert.Equal(1, frame.Usage.ActiveSurfaces);
        }
        Assert.Equal(0, frame.Usage.ActiveSurfaces);
        owner.Dispose(); Assert.True(owner.IsRetiring); Assert.False(owner.IsDisposed);
        using (owner.BorrowVectorInputs(Identity(contents, "a"), frame)) Assert.Equal(2, frame.Usage.ActiveSurfaces);
        scene.Dispose(); Assert.True(owner.IsDisposed); Assert.Equal(0, surfaces.Usage.EstimatedBytes);
    }

    [Fact]
    public void 矢量依赖借用失败撤销已取得声明并拒绝跨会话帧账()
    {
        var contents = Package();
        using var surfaces = new ImageSurfaceBudgetSession(new() { MaximumSurfaces = 3, MaximumBytes = 1_000_000 });
        using var images = PackageGraphImages.Load(contents);
        using var owner = PackageSvgResources.LoadReferenceWithSession(contents, EffectPackageSvgResources.Read(contents),
            SvgResourceBudget.Default, surfaces, images, default);
        using var frame = surfaces.BeginFrame();
        using (var occupied = frame.Reserve(2, 8, "TestOccupiedFrame"))
        {
            Assert.Throws<ImageSurfaceBudgetException>(() => owner.BorrowVectorInputs(Identity(contents, "a"), frame));
            Assert.Equal(2, frame.Usage.ActiveSurfaces); Assert.Equal(8, frame.Usage.EstimatedBytes);
        }
        using var foreign = new ImageSurfaceBudgetSession(); using var foreignFrame = foreign.BeginFrame();
        Assert.Throws<InvalidOperationException>(() => owner.BorrowVectorInputs(Identity(contents, "a"), foreignFrame));
        Assert.Equal(0, foreign.Usage.EstimatedBytes); Assert.Equal(0, frame.Usage.ActiveSurfaces);
    }

    private static ResourceIdentity Identity(EffectPackageContents contents, string id) => new(contents.PackageId, contents.Revision, id);
    private static byte[] Png(int size, SKColor color)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(size, size, SKColorType.Bgra8888, SKAlphaType.Premul));
        bitmap.Erase(color); using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100); return data.ToArray();
    }
    private static EffectPackageContents Package()
    {
        Dictionary<string, byte[]> files = new(StringComparer.Ordinal)
        { ["images/red.png"] = Png(4, SKColors.Red), ["images/blue.png"] = Png(8, SKColors.Blue) };
        string yellow = Convert.ToBase64String(Png(2, SKColors.Yellow));
        foreach (string id in new[] { "a", "b" })
        {
            string body = id == "a" ? "<image href='images/red.png' width='50' height='100'/>"
                + "<image href='data:image/png;base64," + yellow + "' x='50' width='50' height='100'/>"
                : "<image href='images/blue.png' width='100' height='100'/>";
            files.Add("assets/" + id + ".svg", Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 100 100'>" + body + "</svg>"));
            JsonObject descriptor = new()
            {
                ["svgResourceVersion"] = 1, ["id"] = id, ["source"] = "assets/" + id + ".svg",
                ["profile"] = EffectPackageSvgResources.StaticProfile, ["mode"] = "vector",
                ["viewport"] = new JsonObject { ["width"] = 100, ["height"] = 100 }, ["fonts"] = new JsonArray(),
                ["images"] = new JsonArray("images/red.png", "images/blue.png"),
            };
            files.Add("resources/" + id + ".json", Encoding.UTF8.GetBytes(descriptor.ToJsonString()));
        }
        JsonArray entries = [];
        foreach (var pair in files) entries.Add(new JsonObject { ["path"] = pair.Key,
            ["role"] = pair.Key.EndsWith(".svg", StringComparison.Ordinal) ? "svg" : pair.Key.EndsWith(".json", StringComparison.Ordinal) ? "svgResource" : "image",
            ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(pair.Value)) });
        JsonObject manifest = new()
        {
            ["packageVersion"] = EffectPackageProfiles.V2.PackageVersion, ["sdkVersion"] = EffectPackageProfiles.V2.SdkVersion,
            ["shaderProfile"] = EffectPackageProfiles.V2.ShaderProfile, ["packageId"] = "svg.frame.tests", ["name"] = "SVG frame",
            ["version"] = "1.0.0", ["requires"] = new JsonArray(EffectPackageSvgResources.VectorCapability),
            ["lineExit"] = new JsonObject { ["durationUs"] = 0 }, ["emitters"] = new JsonArray(), ["files"] = entries,
            ["parameters"] = new JsonArray(), ["exports"] = new JsonArray(),
        };
        return EffectPackageContents.CreateSupported(Encoding.UTF8.GetBytes(manifest.ToJsonString()),
            files.Select(pair => KeyValuePair.Create(pair.Key, (ReadOnlyMemory<byte>)pair.Value)));
    }
}
