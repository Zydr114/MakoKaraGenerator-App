using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Rendering.Skia.Effects.Svg;
using MakoKaraGenerator.Scripting;
using SkiaSharp;
using Xunit.Abstractions;
using S = ShimSkiaSharp;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed class PackageSvgNativeResourcesTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("path")]
    [InlineData("gradient-clip-use")]
    [InlineData("embedded-image")]
    [InlineData("bounded-filter")]
    public void 四类真实静态图形分别记录矢量并生成固定atlas参考像素(string kind)
    {
        var contents = Package(Fixture(kind), image: kind == "embedded-image" ? Png() : null);
        using var owner = PackageSvgResources.LoadReference(contents, EffectPackageSvgResources.Read(contents));
        var vector = owner.GetVector(Identity(contents, "vector"));
        var atlas = owner.GetSprite(Identity(contents, "atlas"), "fixed");
        using var vectorPixels = Raster(vector.Picture);
        using var srgb = SKColorSpace.CreateSrgb();
        using var atlasPixels = new SKBitmap(new SKImageInfo(100, 100, SKColorType.Bgra8888, SKAlphaType.Premul, srgb));
        using (var canvas = new SKCanvas(atlasPixels))
        {
            canvas.Clear(SKColors.Transparent);
            canvas.DrawImage(atlas.Image, atlas.ContentRect, new(0, 0, 100, 100), new SKSamplingOptions(SKFilterMode.Nearest));
        }
        if (string.Equals(Environment.GetEnvironmentVariable("MAKO_SVG_DIAGNOSTIC"), "1", StringComparison.Ordinal))
            DumpDiagnostics(contents, vectorPixels);
        Assert.Contains(Enumerable.Range(0, 100).SelectMany(y => Enumerable.Range(0, 100).Select(x => vectorPixels.GetPixel(x, y))),
            color => color.Alpha > 100);
        for (int y = 0; y < 100; y++) for (int x = 0; x < 100; x++)
        {
            var left = vectorPixels.GetPixel(x, y); var right = atlasPixels.GetPixel(x, y);
            if (Math.Abs(left.Alpha - right.Alpha) > 2)
                output.WriteLine($"PIXEL MISMATCH ({x},{y}) vector={left}, atlas={right}, content={atlas.ContentRect}.");
            Assert.InRange(Math.Abs(left.Alpha - right.Alpha), 0, 2);
            // Compare premultiplied channels: SKBitmap.GetPixel returns straight channels near transparency.
            Assert.InRange(Math.Abs(left.Red * left.Alpha - right.Red * right.Alpha), 0, 510);
            Assert.InRange(Math.Abs(left.Green * left.Alpha - right.Green * right.Alpha), 0, 510);
            Assert.InRange(Math.Abs(left.Blue * left.Alpha - right.Blue * right.Alpha), 0, 510);
        }
        switch (kind)
        {
            case "path":
                Assert.True(vectorPixels.GetPixel(15, 50).Red > vectorPixels.GetPixel(15, 50).Blue);
                Assert.Equal(0, vectorPixels.GetPixel(50, 50).Alpha);
                break;
            case "gradient-clip-use":
                Assert.Equal(0, vectorPixels.GetPixel(5, 5).Alpha);
                Assert.True(vectorPixels.GetPixel(30, 50).Red > vectorPixels.GetPixel(70, 50).Red);
                Assert.True(vectorPixels.GetPixel(30, 50).Blue < vectorPixels.GetPixel(70, 50).Blue);
                break;
            case "embedded-image":
                // Detect a swallowed image failure even when the blue vector backdrop still rendered.
                Assert.True(vectorPixels.GetPixel(25, 25).Red > 200);
                Assert.True(vectorPixels.GetPixel(62, 25).Red > 200);
                Assert.True(owner.DecodedImageBytes > 0);
                break;
            case "bounded-filter":
                Assert.True(vectorPixels.GetPixel(73, 73).Alpha > 0);
                Assert.True(vectorPixels.GetPixel(73, 73).Red > vectorPixels.GetPixel(73, 73).Blue);
                break;
        }
        Assert.True(owner.RecordedOperations > 0); Assert.True(vector.RecordedOperations > 0);
        Assert.Equal(new SKRect(4, 4, 104, 104), atlas.ContentRect);
        Assert.Equal(atlas.Plan.Levels.Sum(level => level.Bytes), atlas.Plan.MipBytes);
        Assert.False(atlas.MipmapsRequested); Assert.False(atlas.IsTextureBacked);
    }

    [Fact]
    public void owner退休等待最后租约后确定释放Picture图片和全部固定预算()
    {
        var contents = Package(Fixture("embedded-image"), Png());
        var owner = PackageSvgResources.LoadReference(contents, EffectPackageSvgResources.Read(contents));
        var lease = owner.AcquireLease(); var second = owner.AcquireLease();
        var vector = owner.GetVector(Identity(contents, "vector")); var page = owner.GetSprite(Identity(contents, "atlas"), "fixed");
        Assert.True(owner.FixedUsage.EstimatedBytes > 0); Assert.True(owner.DecodedImageBytes > 0);
        owner.Dispose(); Assert.True(owner.IsRetiring); Assert.False(owner.IsDisposed);
        Assert.Throws<ObjectDisposedException>(() => owner.AcquireLease());
        Assert.NotEqual(IntPtr.Zero, vector.Picture.Handle); Assert.NotEqual(IntPtr.Zero, page.Image.Handle);
        lease.Dispose(); Assert.False(owner.IsDisposed);
        second.Dispose(); Assert.True(owner.IsDisposed);
        Assert.Equal(IntPtr.Zero, vector.Picture.Handle); Assert.Equal(IntPtr.Zero, page.Image.Handle);
        Assert.Equal(0, owner.FixedUsage.EstimatedBytes); Assert.Equal(0, owner.FixedUsage.ActiveSurfaces);
        owner.Dispose(); second.Dispose();
    }

    [Fact]
    public void role图片借用现有解码而data图片由SVG独立拥有并确定释放()
    {
        byte[] image = Png();
        byte[] distinct;
        using (var bitmap = new SKBitmap(new SKImageInfo(2, 2, SKColorType.Bgra8888, SKAlphaType.Premul)))
        {
            bitmap.Erase(SKColors.Yellow); using var encoded = SKImage.FromBitmap(bitmap);
            using var data = encoded.Encode(SKEncodedImageFormat.Png, 100); distinct = data.ToArray();
        }
        string body = "<image href='images/pixel.bin' width='50' height='100' preserveAspectRatio='none'/>"
            + "<image href='data:image/png;base64," + Convert.ToBase64String(distinct) + "' x='50' width='50' height='100' preserveAspectRatio='none'/>";
        var contents = Package(body, image); using var images = PackageGraphImages.Load(contents);
        var borrowed = images.BorrowImage("images/pixel.bin");
        var owner = PackageSvgResources.LoadReference(contents, EffectPackageSvgResources.Read(contents), images: images);
        Assert.Equal(4 * 4 * 4, owner.BorrowedImageBytes); Assert.Equal(2 * 2 * 4, owner.DecodedImageBytes);
        owner.Dispose(); Assert.Equal(0, owner.FixedUsage.EstimatedBytes);
        Assert.NotEqual(IntPtr.Zero, borrowed.Handle);
        images.Dispose(); Assert.Equal(IntPtr.Zero, borrowed.Handle);
    }

    [Fact]
    public void native资源只接受精确包修订并拒绝错误执行方式与档位()
    {
        var contents = Package(Fixture("path"));
        using var owner = PackageSvgResources.LoadReference(contents, EffectPackageSvgResources.Read(contents));
        var foreign = new ResourceIdentity(contents.PackageId, "sha256:" + new string('0', 64), "vector");
        Assert.Equal("PackageSvgResourceOwnershipMismatch", Assert.Throws<EffectPackageException>(() => owner.GetVector(foreign)).Code);
        var another = Package("<rect width='10' height='10'/>");
        using var otherImages = PackageGraphImages.Load(another);
        Assert.Equal("PackageSvgResourceOwnershipMismatch", Assert.Throws<EffectPackageException>(() =>
            PackageSvgResources.LoadReference(contents, EffectPackageSvgResources.Read(contents), images: otherImages)).Code);
        Assert.Equal("PackageSvgVectorMissing", Assert.Throws<EffectPackageException>(() => owner.GetVector(Identity(contents, "atlas"))).Code);
        Assert.Equal("PackageSvgAtlasMissing", Assert.Throws<EffectPackageException>(() => owner.GetSprite(Identity(contents, "atlas"), "missing")).Code);
    }

    [Fact]
    public void native固定像素预算在分配前拒绝并可在失败后重新创建()
    {
        var contents = Package(Fixture("embedded-image"), Png());
        var limits = new GraphExecutionBudget { MaximumBytes = 16, MaximumSurfaces = 64 };
        Assert.Throws<ImageSurfaceBudgetException>(() => PackageSvgResources.LoadReference(contents,
            EffectPackageSvgResources.Read(contents), surfaceLimits: limits));
        using var retry = PackageSvgResources.LoadReference(contents, EffectPackageSvgResources.Read(contents));
        Assert.NotEqual(IntPtr.Zero, retry.GetSprite(Identity(contents, "atlas"), "fixed").Image.Handle);
    }

    [Fact]
    public void strictloader拒绝未验证字节并保留上游可能吞掉的错误()
    {
        var contents = Package(Fixture("path"));
        var compiled = SvgStaticCompiler.CompilePackage(contents, EffectPackageSvgResources.Read(contents), SvgResourceBudget.Default, default);
        var loader = new StrictSvgAssetLoader(compiled.Documents[0], default);
        Assert.Throws<EffectPackageException>(() => loader.LoadImage(new MemoryStream([1, 2, 3])));
        Assert.Throws<EffectPackageException>(() => loader.ThrowIfFaulted());
        Assert.False(loader.EnableSvgFonts); Assert.False(loader.EnableTextReferences);
        Assert.Throws<EffectPackageException>(() => loader.FindTypefaces("text", new()));
    }

    [Fact]
    public void 已取消准备不进入native创建()
    {
        var contents = Package(Fixture("path")); using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => PackageSvgResources.LoadReference(contents,
            EffectPackageSvgResources.Read(contents), token: cancellation.Token));
    }

    [Fact]
    public void Vulkan真实上传完整mip预算且缩小采样降低高频条纹混叠()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("MAKO_GPU_SMOKE"), "1", StringComparison.Ordinal)) return;
        string stripes = string.Concat(Enumerable.Range(0, 100).Select(x =>
            $"<rect x='{x}' width='1' height='100' fill='{(x % 2 == 0 ? "white" : "black")}'/>"));
        var contents = Package(stripes);
        using var device = SkiaRenderDeviceFactory.Create(new(SkiaRenderBackend.Vulkan));
        using var surfaces = new ImageSurfaceBudgetSession();
        using var owner = PackageSvgResources.LoadWithDevice(contents, EffectPackageSvgResources.Read(contents),
            SvgResourceBudget.Default, device, surfaces, null, default);
        var page = owner.GetSprite(Identity(contents, "atlas"), "fixed");
        Assert.True(page.MipmapsRequested); Assert.True(page.IsTextureBacked); Assert.Same(device, page.Device);
        Assert.Equal(page.Plan.MipBytes, page.StorageBytes);
        Assert.Equal(page.StorageBytes, surfaces.Usage.EstimatedBytes);
        using var reference = PackageSvgResources.LoadReference(contents, EffectPackageSvgResources.Read(contents));
        lock (device.SynchronizationRoot)
        {
            using var noMip = device.CreateTextureImage(reference.GetSprite(Identity(contents, "atlas"), "fixed").Image, false);
            Assert.NotNull(noMip); Assert.True(noMip.IsTextureBacked);
            using var mipPixels = Sample(device, page.Image, SKMipmapMode.Linear);
            using var rawPixels = Sample(device, noMip, SKMipmapMode.None);
            double mipVariation = Variation(mipPixels), rawVariation = Variation(rawPixels);
            Assert.True(mipVariation + 5 < rawVariation,
                $"实际缩小采样应改变LOD边缘：mip variation={mipVariation:F2}, no-mip variation={rawVariation:F2}。");
        }
        owner.Dispose(); Assert.Equal(0, surfaces.Usage.EstimatedBytes);
    }

    private static SKBitmap Sample(ISkiaRenderDevice device, SKImage image, SKMipmapMode mipmap)
    {
        using var surface = device.CreateSurface(new SKImageInfo(11, 11, SKColorType.Bgra8888, SKAlphaType.Premul));
        surface.Canvas.Clear(SKColors.Transparent);
        // Sample the whole independent page, preserving derivatives and its transparent padding.
        surface.Canvas.DrawImage(image, new SKRect(0, 0, 11, 11), new SKSamplingOptions(SKFilterMode.Linear, mipmap));
        device.FlushAndSubmit(surface);
        var pixels = new SKBitmap(new SKImageInfo(11, 11, SKColorType.Bgra8888, SKAlphaType.Premul));
        if (!device.ReadPixels(surface, pixels.Info, pixels.GetPixels(), pixels.RowBytes))
        { pixels.Dispose(); throw new InvalidOperationException("无法读取 atlas Vulkan 缩小采样像素。"); }
        return pixels;
    }
    private static double Variation(SKBitmap pixels)
    {
        var values = Enumerable.Range(1, 9).Select(x => (double)pixels.GetPixel(x, 5).Red).ToArray();
        double average = values.Average(); return Math.Sqrt(values.Average(value => Math.Pow(value - average, 2)));
    }

    private static SKBitmap Raster(SKPicture picture)
    {
        using var srgb = SKColorSpace.CreateSrgb();
        var pixels = new SKBitmap(new SKImageInfo(100, 100, SKColorType.Bgra8888, SKAlphaType.Premul, srgb));
        using var canvas = new SKCanvas(pixels); canvas.Clear(SKColors.Transparent); canvas.Scale(100, 100); canvas.DrawPicture(picture);
        return pixels;
    }
    private static ResourceIdentity Identity(EffectPackageContents contents, string id) => new(contents.PackageId, contents.Revision, id);
    private void DumpDiagnostics(EffectPackageContents contents, SKBitmap pixels)
    {
        var document = SvgStaticCompiler.CompilePackage(contents, EffectPackageSvgResources.Read(contents), SvgResourceBudget.Default, default)
            .Documents.Single(document => document.Inspection.Id == "vector");
        output.WriteLine(document.SafeXml);
        using var text = new StringReader(document.SafeXml);
        using var reader = System.Xml.XmlReader.Create(text, new() { DtdProcessing = System.Xml.DtdProcessing.Prohibit, XmlResolver = null });
        var fragment = global::Svg.Model.Services.SvgService.Open(reader, false);
        var loader = new StrictSvgAssetLoader(document, default);
        Assert.True(global::Svg.Skia.SvgSceneRuntime.TryCompile(fragment, loader, global::Svg.Model.DrawAttributes.None,
            new(0, 0, 100, 100), out var scene));
        loader.ThrowIfFaulted(); Assert.NotNull(scene);
        output.WriteLine("SCENE " + scene.CullRect);
        Nodes(scene.Root, "");
        Assert.NotNull(scene.CreateModel()); Commands(scene.CreateModel()!, "");
        for (int y = 65; y <= 79; y += 2)
            output.WriteLine("PIXELS " + y + ": " + string.Join(" ", Enumerable.Range(65, 15).Where(x => x % 2 == 1).Select(x => x + "=" + pixels.GetPixel(x, y))));

        void Nodes(global::Svg.Skia.SvgSceneNode node, string prefix)
        {
            output.WriteLine(prefix + node.ElementTypeName + "#" + node.ElementId + " render=" + node.IsRenderable
                + " bounds=" + node.GeometryBounds + " transformed=" + node.TransformedBounds + " matrix=" + node.Transform
                + " clip=" + node.Clip + " inner=" + node.InnerClip + " filterClip=" + node.FilterClip);
            foreach (var child in node.Children) Nodes(child, prefix + "  ");
        }
        void Commands(S.SKPicture picture, string prefix)
        {
            output.WriteLine(prefix + "PICTURE " + picture.CullRect);
            foreach (var command in picture.Commands ?? [])
            {
                output.WriteLine(prefix + command);
                if (command is S.SaveLayerCanvasCommand layer) Paint(layer.Paint, prefix + "  ");
                if (command is S.DrawPathCanvasCommand path) Paint(path.Paint, prefix + "  ");
                if (command is S.DrawPictureCanvasCommand { Picture: { } child }) Commands(child, prefix + "  ");
            }
        }
        void Paint(S.SKPaint? paint, string prefix)
        {
            if (paint is null) return;
            output.WriteLine(prefix + "PAINT color=" + paint.Color + " blend=" + paint.BlendMode + " shader=" + paint.Shader);
            Filter(paint.ImageFilter, prefix);
        }
        void Filter(S.SKImageFilter? filter, string prefix)
        {
            if (filter is null) return;
            output.WriteLine(prefix + "FILTER " + filter);
            switch (filter)
            {
                case S.BlurImageFilter item: Filter(item.Input, prefix + "  "); break;
                case S.OffsetImageFilter item: Filter(item.Input, prefix + "  "); break;
                case S.ColorFilterImageFilter item: Filter(item.Input, prefix + "  "); break;
                case S.MergeImageFilter item: foreach (var input in item.Filters ?? []) Filter(input, prefix + "  "); break;
                case S.BlendModeImageFilter item: Filter(item.Background, prefix + "  "); Filter(item.Foreground, prefix + "  "); break;
                case S.PictureImageFilter { Picture: { } picture }: Commands(picture, prefix + "  "); break;
            }
        }
    }
    private static string Fixture(string kind) => kind switch
    {
        "path" => """
            <g opacity="0.8"><path d="M10 10 L90 10 L90 90 L10 90 Z M30 30 Q50 10 70 30 C80 40 80 60 70 70 A20 20 0 0 1 30 70 Z"
              fill="#e04020" fill-rule="evenodd" stroke="#2040e0" stroke-width="3" stroke-dasharray="4 2"/></g>
            """,
        "gradient-clip-use" => """
            <style>.ink { fill: url(#paint); stroke: #17304f; stroke-width: 2 }</style>
            <defs>
              <linearGradient id="base" gradientUnits="objectBoundingBox"><stop offset="0" stop-color="#e02050"/><stop offset="1" stop-color="#2070e0"/></linearGradient>
              <linearGradient id="paint" gradientUnits="objectBoundingBox" href="#base" x1="0" x2="1"/>
              <clipPath id="clip" clipPathUnits="userSpaceOnUse"><circle cx="50" cy="50" r="35"/></clipPath>
              <symbol id="tile" viewBox="0 0 10 10"><rect class="ink" width="10" height="10"/></symbol>
            </defs><g clip-path="url(#clip)" opacity="0.75"><use href="#tile" x="10" y="10" width="80" height="80"/></g>
            """,
        "embedded-image" => "<rect x='10' y='10' width='80' height='80' fill='#2040b0'/><image href='images/pixel.bin' x='20' y='20' width='40' height='60' preserveAspectRatio='none'/>"
            + "<image href='data:image/png;base64," + Convert.ToBase64String(Png()) + "' x='60' y='20' width='20' height='60' preserveAspectRatio='none'/>",
        "bounded-filter" => """
            <defs><filter id="f" filterUnits="userSpaceOnUse" primitiveUnits="userSpaceOnUse" x="0" y="0" width="100" height="100">
              <feGaussianBlur in="SourceGraphic" stdDeviation="2" result="blur"/><feOffset in="blur" dx="3" dy="4" result="offset"/>
              <feColorMatrix in="offset" type="matrix" values="0 0 0 0 0.8 0 0 0 0 0.1 0 0 0 0 0.2 0 0 0 1 0" result="red"/>
              <feMerge><feMergeNode in="red"/><feMergeNode in="SourceGraphic"/></feMerge>
            </filter></defs><rect x="25" y="25" width="45" height="45" fill="#2070e0" filter="url(#f)"/>
            """,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
    private static byte[] Png()
    {
        using var bitmap = new SKBitmap(new SKImageInfo(4, 4, SKColorType.Bgra8888, SKAlphaType.Premul));
        bitmap.Erase(SKColors.Transparent); bitmap.SetPixel(0, 0, SKColors.Red); bitmap.SetPixel(1, 1, SKColors.Green);
        bitmap.SetPixel(2, 2, new SKColor(255, 255, 255, 128)); bitmap.SetPixel(3, 3, SKColors.Blue);
        using var image = SKImage.FromBitmap(bitmap); using var data = image.Encode(SKEncodedImageFormat.Png, 100); return data.ToArray();
    }
    private static EffectPackageContents Package(string body, byte[]? image = null)
    {
        Dictionary<string, byte[]> files = new(StringComparer.Ordinal)
        { ["assets/main.svg"] = Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 100 100'>" + body + "</svg>") };
        if (image is not null) files.Add("images/pixel.bin", image);
        foreach (string mode in new[] { "vector", "atlas" })
        {
            JsonObject descriptor = new()
            {
                ["svgResourceVersion"] = 1, ["id"] = mode, ["source"] = "assets/main.svg",
                ["profile"] = EffectPackageSvgResources.StaticProfile, ["mode"] = mode,
                ["viewport"] = new JsonObject { ["width"] = 100, ["height"] = 100 }, ["fonts"] = new JsonArray(),
                ["images"] = image is null ? new JsonArray() : new JsonArray("images/pixel.bin"),
            };
            if (mode == "atlas") descriptor["atlas"] = new JsonObject
            {
                ["variants"] = new JsonArray(new JsonObject { ["id"] = "fixed", ["width"] = 100, ["height"] = 100 }),
                ["paddingPx"] = 4, ["mipmaps"] = true,
            };
            files.Add("resources/" + mode + ".json", Encoding.UTF8.GetBytes(descriptor.ToJsonString()));
        }
        JsonArray entries = [];
        foreach (var pair in files) entries.Add(new JsonObject { ["path"] = pair.Key,
            ["role"] = pair.Key.EndsWith(".svg", StringComparison.Ordinal) ? "svg" : pair.Key.EndsWith(".json", StringComparison.Ordinal) ? "svgResource" : "image",
            ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(pair.Value)) });
        JsonObject manifest = new()
        {
            ["packageVersion"] = EffectPackageProfiles.V2.PackageVersion, ["sdkVersion"] = EffectPackageProfiles.V2.SdkVersion,
            ["shaderProfile"] = EffectPackageProfiles.V2.ShaderProfile, ["packageId"] = "svg.native.tests", ["name"] = "SVG native",
            ["version"] = "1.0.0", ["requires"] = new JsonArray(EffectPackageSvgResources.VectorCapability, EffectPackageSvgResources.AtlasCapability),
            ["lineExit"] = new JsonObject { ["durationUs"] = 0 }, ["emitters"] = new JsonArray(), ["files"] = entries,
            ["parameters"] = new JsonArray(), ["exports"] = new JsonArray(),
        };
        return EffectPackageContents.CreateSupported(Encoding.UTF8.GetBytes(manifest.ToJsonString()),
            files.Select(pair => KeyValuePair.Create(pair.Key, (ReadOnlyMemory<byte>)pair.Value)));
    }
}
