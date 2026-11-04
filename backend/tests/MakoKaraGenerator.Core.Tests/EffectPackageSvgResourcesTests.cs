using System.Text;
using System.Text.Json.Nodes;
using MakoKaraGenerator.Core.Effects;

namespace MakoKaraGenerator.Core.Tests;

public sealed class EffectPackageSvgResourcesTests
{
    private static readonly string[] PixelImage = ["assets/pixel.bin"];

    [Fact]
    public void 严格描述固定vector和atlas的独立逻辑身份且图片及源仍属于原闭包()
    {
        var files = Files(); var manifest = Manifest(files); var contents = Create(files, manifest);
        var resources = EffectPackageSvgResources.Read(contents);
        var vector = resources.ById["star-vector"]; var atlas = resources.ById["star-atlas"];
        Assert.Equal(2, resources.ById.Count); Assert.Equal(SvgResourceMode.Vector, vector.Mode);
        Assert.Equal("resources/vector.json", vector.DescriptorPath); Assert.Equal("assets/star.svg", vector.SourcePath);
        Assert.Equal(EffectPackageSvgResources.StaticProfile, vector.Profile);
        Assert.Equal(100, vector.Viewport.Width); Assert.Equal(80, vector.Viewport.Height);
        Assert.Equal(PixelImage, vector.Images); Assert.Empty(vector.Variants);
        Assert.Equal(0, vector.PaddingPx); Assert.False(vector.Mipmaps);
        Assert.Equal(SvgResourceMode.Atlas, atlas.Mode); Assert.Equal(vector.SourcePath, atlas.SourcePath);
        var variant = Assert.Single(atlas.Variants); Assert.Equal("s64", variant.Id);
        Assert.Equal(64, variant.Width); Assert.Equal(32, variant.Height); Assert.Equal(4, atlas.PaddingPx); Assert.True(atlas.Mipmaps);
        files["resources/vector.json"][0] = 0; files["assets/star.svg"][0] = 0;
        Assert.Equal("star-vector", EffectPackageSvgResources.Read(contents).ById["star-vector"].Id);
        Assert.Equal(contents.Revision, Create(Files(), Manifest(Files())).Revision);
    }

    [Fact]
    public void 静态描述验证不授予SVG执行能力且Definition返回前仍拒绝pending能力()
    {
        var contents = Create(Files());
        Assert.Equal(2, EffectPackageSvgResources.Read(contents).ById.Count);
        Assert.DoesNotContain(EffectPackageSvgResources.VectorCapability, EffectPackageProfiles.SupportedV2Capabilities);
        Assert.DoesNotContain(EffectPackageSvgResources.AtlasCapability, EffectPackageProfiles.SupportedV2Capabilities);
        var error = Assert.Throws<EffectPackageException>(() => EffectPackageDefinition.Read(contents));
        Assert.Equal("PackageCapabilityUnsupported", error.Code);
        Assert.Contains("resources.svg.", error.Message, StringComparison.Ordinal);
        Assert.Empty(EffectPackageDefinition.Read(SdkV2PackageFixture.Contents()).SvgResources.ById);
        Assert.Empty(EffectPackageDefinition.Read(EffectPackageFixture.Contents()).SvgResources.ById);
    }

    [Theory]
    [InlineData("unknown", "PackageSemanticUnknownField")]
    [InlineData("missing", "PackageSemanticMissingField")]
    [InlineData("version", "PackageSvgVersionMismatch")]
    [InlineData("fraction-version", "PackageParameterTypeMismatch")]
    [InlineData("profile", "PackageSvgProfileMismatch")]
    [InlineData("mode", "PackageSvgModeUnsupported")]
    [InlineData("id", "PackageSvgIdInvalid")]
    [InlineData("long-id", "PackageSvgIdInvalid")]
    [InlineData("font", "PackageSvgFontsUnsupported")]
    [InlineData("font-type", "PackageParameterTypeMismatch")]
    [InlineData("viewport-unknown", "PackageSemanticUnknownField")]
    [InlineData("viewport-zero", "PackageSvgViewportInvalid")]
    [InlineData("viewport-large", "PackageSvgViewportInvalid")]
    [InlineData("viewport-type", "PackageParameterTypeMismatch")]
    [InlineData("vector-atlas", "PackageSvgAtlasUnexpected")]
    [InlineData("image-duplicate", "PackageReferenceDuplicate")]
    [InlineData("source-path", "PackagePathInvalid")]
    [InlineData("source-case", "PackageSvgSourceMissing")]
    [InlineData("source-missing", "PackageSvgSourceMissing")]
    [InlineData("image-path", "PackagePathInvalid")]
    [InlineData("image-missing", "PackageSvgImageMissing")]
    public void 描述中未知缺失错型非法语义与路径均在Contents承载前失败(string failure, string code)
    {
        var files = Files(); var descriptor = JsonNode.Parse(files["resources/vector.json"])!.AsObject();
        switch (failure)
        {
            case "unknown": descriptor["unexpected"] = true; break;
            case "missing": descriptor.Remove("fonts"); break;
            case "version": descriptor["svgResourceVersion"] = 2; break;
            case "fraction-version": descriptor["svgResourceVersion"] = 1.5; break;
            case "profile": descriptor["profile"] = "mako-svg-static-2"; break;
            case "mode": descriptor["mode"] = "Vector"; break;
            case "id": descriptor["id"] = "bad:id"; break;
            case "long-id": descriptor["id"] = new string('a', 129); break;
            case "font": descriptor["fonts"]!.AsArray().Add("fonts/body.ttf"); break;
            case "font-type": descriptor["fonts"] = "none"; break;
            case "viewport-unknown": descriptor["viewport"]!["depth"] = 1; break;
            case "viewport-zero": descriptor["viewport"]!["width"] = 0; break;
            case "viewport-large": descriptor["viewport"]!["height"] = 1_000_001; break;
            case "viewport-type": descriptor["viewport"]!["width"] = "100"; break;
            case "vector-atlas": descriptor["atlas"] = Atlas(); break;
            case "image-duplicate": descriptor["images"]!.AsArray().Add("assets/pixel.bin"); break;
            case "source-path": descriptor["source"] = "../star.svg"; break;
            case "source-case": descriptor["source"] = "assets/Star.svg"; break;
            case "source-missing": descriptor["source"] = "assets/missing.svg"; break;
            case "image-path": descriptor["images"]![0] = "file:///tmp/image.png"; break;
            case "image-missing": descriptor["images"]![0] = "assets/missing.png"; break;
        }
        files["resources/vector.json"] = EffectPackageFixture.Bytes(descriptor);
        var error = Assert.Throws<EffectPackageException>(() => Create(files));
        Assert.Equal(code, error.Code);
    }

    [Theory]
    [InlineData("atlas-missing", "PackageSemanticMissingField")]
    [InlineData("unknown", "PackageSemanticUnknownField")]
    [InlineData("variants-empty", "PackageSvgBudgetExceeded")]
    [InlineData("variants-type", "PackageParameterTypeMismatch")]
    [InlineData("variants-over-budget", "PackageSvgBudgetExceeded")]
    [InlineData("variant-duplicate", "PackageSvgVariantDuplicate")]
    [InlineData("variant-unknown", "PackageSemanticUnknownField")]
    [InlineData("variant-id", "PackageSvgIdInvalid")]
    [InlineData("width-zero", "PackageSvgAtlasSizeInvalid")]
    [InlineData("width-fraction", "PackageParameterTypeMismatch")]
    [InlineData("width-overflow", "PackageSvgAtlasSizeInvalid")]
    [InlineData("page-with-padding", "PackageSvgAtlasSizeInvalid")]
    [InlineData("padding-zero", "PackageSvgAtlasPaddingInvalid")]
    [InlineData("padding-large", "PackageSvgAtlasPaddingInvalid")]
    [InlineData("mipmaps-false", "PackageSvgMipmapsRequired")]
    [InlineData("mipmaps-type", "PackageSvgMipmapsRequired")]
    public void atlas必须声明唯一有限档位和真实mip意图且padding计入页边长(string failure, string code)
    {
        var files = Files(); var descriptor = JsonNode.Parse(files["resources/atlas.json"])!.AsObject();
        var atlas = descriptor["atlas"]!;
        switch (failure)
        {
            case "atlas-missing": descriptor.Remove("atlas"); break;
            case "unknown": atlas["automaticLod"] = true; break;
            case "variants-empty": atlas["variants"] = new JsonArray(); break;
            case "variants-type": atlas["variants"] = "s64"; break;
            case "variants-over-budget":
                for (int i = 1; i <= 8; i++)
                {
                    var extra = atlas["variants"]![0]!.DeepClone(); extra["id"] = "s" + i; atlas["variants"]!.AsArray().Add(extra);
                }
                break;
            case "variant-duplicate": atlas["variants"]!.AsArray().Add(atlas["variants"]![0]!.DeepClone()); break;
            case "variant-unknown": atlas["variants"]![0]!["quality"] = "automatic"; break;
            case "variant-id": atlas["variants"]![0]!["id"] = "s:64"; break;
            case "width-zero": atlas["variants"]![0]!["width"] = 0; break;
            case "width-fraction": atlas["variants"]![0]!["width"] = 64.5; break;
            case "width-overflow": atlas["variants"]![0]!["width"] = long.MaxValue; break;
            case "page-with-padding": atlas["variants"]![0]!["width"] = 4096; break;
            case "padding-zero": atlas["paddingPx"] = 0; break;
            case "padding-large": atlas["paddingPx"] = 65; break;
            case "mipmaps-false": atlas["mipmaps"] = false; break;
            case "mipmaps-type": atlas["mipmaps"] = "true"; break;
        }
        files["resources/atlas.json"] = EffectPackageFixture.Bytes(descriptor);
        Assert.Equal(code, Assert.Throws<EffectPackageException>(() => Create(files)).Code);
    }

    [Fact]
    public void source和image只按精确role解析而不是扩展名且不能把v2资源放入v1包()
    {
        var files = Files(); var manifest = Manifest(files);
        FileEntry(manifest, "assets/star.svg")["role"] = "asset";
        Assert.Equal("PackageSvgSourceMissing", Assert.Throws<EffectPackageException>(() => Create(files, manifest)).Code);
        manifest = Manifest(files); FileEntry(manifest, "assets/pixel.bin")["role"] = "svg";
        Assert.Equal("PackageSvgImageMissing", Assert.Throws<EffectPackageException>(() => Create(files, manifest)).Code);
        manifest = Manifest(files); manifest["packageVersion"] = EffectPackageProfiles.V1.PackageVersion;
        manifest["sdkVersion"] = EffectPackageProfiles.V1.SdkVersion; manifest["shaderProfile"] = EffectPackageProfiles.V1.ShaderProfile;
        manifest.Remove("requires"); manifest.Remove("lineExit"); manifest.Remove("emitters");
        Assert.Equal("PackageSvgProfileMismatch", Assert.Throws<EffectPackageException>(() => Create(files, manifest)).Code);
    }

    [Fact]
    public void 未被controller引用的描述坏源和未覆盖源仍拒绝整个包()
    {
        var files = Files(); files["resources/unused.json"] = Encoding.UTF8.GetBytes("{}");
        Assert.Equal("PackageSemanticMissingField", Assert.Throws<EffectPackageException>(() => Create(files)).Code);
        files = Files(); files["assets/unused.svg"] = Encoding.UTF8.GetBytes("<svg/>");
        Assert.Equal("PackageSvgSourceUnreferenced", Assert.Throws<EffectPackageException>(() => Create(files)).Code);
        files = Files(); files["assets/unused.svg"] = [0xc3, 0x28];
        Assert.Equal("PackageUtf8Invalid", Assert.Throws<EffectPackageException>(() => Create(files)).Code);
        files = Files(); var descriptor = JsonNode.Parse(files["resources/vector.json"])!.AsObject(); descriptor["id"] = "star-atlas";
        files["resources/vector.json"] = EffectPackageFixture.Bytes(descriptor);
        Assert.Equal("PackageSvgResourceDuplicate", Assert.Throws<EffectPackageException>(() => Create(files)).Code);
    }

    [Theory]
    [InlineData("root")]
    [InlineData("viewport")]
    [InlineData("variant")]
    public void 描述每级重复JSON键均拒绝且坏UTF8不替换为合法文字(string level)
    {
        var files = Files(); string path = level == "variant" ? "resources/atlas.json" : "resources/vector.json";
        string json = Encoding.UTF8.GetString(files[path]);
        string from = level switch { "root" => "\"svgResourceVersion\":1", "viewport" => "\"width\":100", _ => "\"width\":64" };
        files[path] = Encoding.UTF8.GetBytes(json.Replace(from, from + "," + from, StringComparison.Ordinal));
        Assert.Equal("PackageJsonDuplicateKey", Assert.Throws<EffectPackageException>(() => Create(files)).Code);
        files = Files(); files[path] = [0xc3, 0x28];
        Assert.Equal("PackageUtf8Invalid", Assert.Throws<EffectPackageException>(() => Create(files)).Code);
    }

    [Theory]
    [InlineData(EffectPackageSvgResources.VectorCapability)]
    [InlineData(EffectPackageSvgResources.AtlasCapability)]
    public void 资源所需能力必须在包级精确声明(string capability)
    {
        var files = Files(); var manifest = Manifest(files); var requires = manifest["requires"]!.AsArray();
        requires.Remove(requires.Single(value => value!.GetValue<string>() == capability));
        Assert.Equal("PackageCapabilityMissing", Assert.Throws<EffectPackageException>(() => Create(files, manifest)).Code);
    }

    [Theory]
    [InlineData("resources")]
    [InlineData("descriptor")]
    [InlineData("total-descriptor")]
    [InlineData("source")]
    [InlineData("total-source")]
    [InlineData("viewport")]
    [InlineData("image-references")]
    [InlineData("image")]
    [InlineData("total-image")]
    [InlineData("atlas-dimension")]
    [InlineData("atlas-padding")]
    [InlineData("atlas-mips")]
    public void 显式更小预算在分配执行资源前拒绝且累计项不因复用描述漏账(string limit)
    {
        var contents = Create(Files()); var budget = new SvgResourceBudget();
        budget = limit switch
        {
            "resources" => budget with { MaximumResources = 1 },
            "descriptor" => budget with { MaximumDescriptorBytes = 1 },
            "total-descriptor" => budget with { MaximumTotalDescriptorBytes = 1 },
            "source" => budget with { MaximumSourceBytes = 1 },
            "total-source" => budget with { MaximumTotalSourceBytes = 1 },
            "viewport" => budget with { MaximumViewportDimension = 50 },
            "image-references" => budget with { MaximumImageReferences = 1 },
            "image" => budget with { MaximumImageBytes = 1 },
            "total-image" => budget with { MaximumTotalImageBytes = 1 },
            "atlas-dimension" => budget with { MaximumAtlasDimension = 64 },
            "atlas-padding" => budget with { MaximumAtlasPaddingPx = 3 },
            _ => budget with { MaximumAtlasMipBytes = 1 },
        };
        string code = limit switch
        {
            "viewport" => "PackageSvgViewportInvalid", "atlas-dimension" => "PackageSvgAtlasSizeInvalid",
            "atlas-padding" => "PackageSvgAtlasPaddingInvalid", _ => "PackageSvgBudgetExceeded",
        };
        Assert.Equal(code, Assert.Throws<EffectPackageException>(() => EffectPackageSvgResources.Read(contents, budget)).Code);
    }

    [Fact]
    public void atlas按含padding的完整非方形mip链计费而非只算基础层()
    {
        // 72x40 → 36x20 → 18x10 → 9x5 → 4x2 → 2x1 → 1x1，合计 15344 B。
        var contents = Create(Files());
        Assert.Single(EffectPackageSvgResources.Read(contents, budget: new SvgResourceBudget { MaximumAtlasMipBytes = 15_344 }).ById["star-atlas"].Variants);
        Assert.Equal("PackageSvgBudgetExceeded", Assert.Throws<EffectPackageException>(() =>
            EffectPackageSvgResources.Read(contents, budget: new SvgResourceBudget { MaximumAtlasMipBytes = 15_343 })).Code);
    }

    [Fact]
    public void 多描述累计atlas预算并按固定源图片内容去重而不漏掉另一份文件()
    {
        var files = Files(); files["resources/second-atlas.json"] = EffectPackageFixture.Bytes(Descriptor("second-atlas", true));
        var contents = Create(files);
        Assert.Equal(3, EffectPackageSvgResources.Read(contents, budget: new SvgResourceBudget { MaximumAtlasMipBytes = 30_688 }).ById.Count);
        Assert.Equal("PackageSvgBudgetExceeded", Assert.Throws<EffectPackageException>(() =>
            EffectPackageSvgResources.Read(contents, budget: new SvgResourceBudget { MaximumAtlasMipBytes = 30_687 })).Code);
        // 三个描述共用一份源及图片，不重复收取固定原字节，但三次 whitelist 引用独立计数。
        Assert.Equal(3, EffectPackageSvgResources.Read(contents, budget: new SvgResourceBudget
        {
            MaximumTotalSourceBytes = files["assets/star.svg"].Length, MaximumTotalImageBytes = 3, MaximumImageReferences = 3,
        }).ById.Count);
        files = Files(); files["assets/second.bin"] = [4, 5, 6];
        var descriptor = JsonNode.Parse(files["resources/vector.json"])!.AsObject(); descriptor["images"]!.AsArray().Add("assets/second.bin");
        files["resources/vector.json"] = EffectPackageFixture.Bytes(descriptor);
        var manifest = Manifest(files); FileEntry(manifest, "assets/second.bin")["role"] = "image"; contents = Create(files, manifest);
        Assert.Equal("PackageSvgBudgetExceeded", Assert.Throws<EffectPackageException>(() =>
            EffectPackageSvgResources.Read(contents, budget: new SvgResourceBudget { MaximumTotalImageBytes = 5 })).Code);
    }

    [Fact]
    public void 已固定源描述图片或采样profile的任何字节变化都产生新包修订()
    {
        var files = Files(); var original = Create(files);
        files["assets/pixel.bin"][0]++; Assert.NotEqual(original.Revision, Create(files).Revision);
        files = Files(); files["assets/star.svg"] = Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 1 1\"/>");
        Assert.NotEqual(original.Revision, Create(files).Revision);
        files = Files(); var descriptor = JsonNode.Parse(files["resources/vector.json"])!.AsObject(); descriptor["viewport"]!["width"] = 101;
        files["resources/vector.json"] = EffectPackageFixture.Bytes(descriptor); Assert.NotEqual(original.Revision, Create(files).Revision);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        Assert.Throws<OperationCanceledException>(() => EffectPackageSvgResources.Read(original, canceled.Token));
        Assert.Throws<ArgumentException>(() => EffectPackageSvgResources.Read(original, budget: new SvgResourceBudget { MaximumAtlasMipBytes = 0 }));
    }

    [Theory]
    [InlineData(false, 0L)]
    [InlineData(false, -1L)]
    [InlineData(true, 0L)]
    [InlineData(true, -1L)]
    public void 受控XML单份及累计预算拒绝非正上限(bool total, long maximum)
    {
        var budget = total
            ? new SvgResourceBudget { MaximumTotalSafeXmlBytes = maximum }
            : new SvgResourceBudget { MaximumSafeXmlBytes = maximum };
        Assert.Throws<ArgumentException>(() => budget.Validate());
        Assert.Throws<ArgumentException>(() => EffectPackageSvgResources.Read(Create(Files()), budget));
    }

    private static Dictionary<string, byte[]> Files()
    {
        var files = EffectPackageFixture.Files();
        files["assets/star.svg"] = Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 100 80\"><path d=\"M0 0L100 80\"/></svg>");
        files["assets/pixel.bin"] = [1, 2, 3];
        files["resources/vector.json"] = EffectPackageFixture.Bytes(Descriptor("star-vector", false));
        files["resources/atlas.json"] = EffectPackageFixture.Bytes(Descriptor("star-atlas", true));
        return files;
    }

    private static JsonObject Descriptor(string id, bool atlas)
    {
        var result = new JsonObject
        {
            ["svgResourceVersion"] = 1, ["id"] = id, ["source"] = "assets/star.svg",
            ["profile"] = EffectPackageSvgResources.StaticProfile, ["mode"] = atlas ? "atlas" : "vector",
            ["viewport"] = new JsonObject { ["width"] = 100, ["height"] = 80 }, ["fonts"] = new JsonArray(),
            ["images"] = new JsonArray("assets/pixel.bin"),
        };
        if (atlas) result["atlas"] = Atlas(); return result;
    }

    private static JsonObject Atlas() => new()
    {
        ["variants"] = new JsonArray(new JsonObject { ["id"] = "s64", ["width"] = 64, ["height"] = 32 }),
        ["paddingPx"] = 4, ["mipmaps"] = true,
    };

    private static JsonObject Manifest(Dictionary<string, byte[]> files)
    {
        var manifest = SdkV2PackageFixture.Manifest(files);
        manifest["requires"]!.AsArray().Add(EffectPackageSvgResources.VectorCapability);
        manifest["requires"]!.AsArray().Add(EffectPackageSvgResources.AtlasCapability);
        foreach (var node in manifest["files"]!.AsArray())
        {
            var entry = node!.AsObject(); string path = entry["path"]!.GetValue<string>();
            if (path.StartsWith("resources/", StringComparison.Ordinal)) entry["role"] = "svgResource";
            if (path.EndsWith(".svg", StringComparison.Ordinal)) entry["role"] = "svg";
            if (path == "assets/pixel.bin") entry["role"] = "image";
        }
        return manifest;
    }

    private static JsonObject FileEntry(JsonObject manifest, string path)
        => manifest["files"]!.AsArray().Single(entry => entry!["path"]!.GetValue<string>() == path)!.AsObject();

    private static EffectPackageContents Create(Dictionary<string, byte[]> files, JsonObject? manifest = null)
        => EffectPackageContents.CreateSupported(EffectPackageFixture.Bytes(manifest ?? Manifest(files)), EffectPackageFixture.Inputs(files));
}
