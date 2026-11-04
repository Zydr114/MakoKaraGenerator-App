using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Rendering.Skia.Effects.Svg;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed partial class PackageSvgValidatorTests
{
    [Fact]
    public void 真正grammar消费隐式moveto重复相对曲线arc且inspection只是静态事实()
    {
        var contents = Package(Svg("<path d='M1 1 5 5 h5 v5 C1 2 3 4 5 6 S7 8 9 10 Q2 3 4 5 T6 7 A2 3 30 0 1 10 10 z'/>"), atlas: true);
        var inspection = new PackageSvgValidator().Validate(contents, EffectPackageSvgResources.Read(contents));
        Assert.Equal(contents.PackageId, inspection.PackageId); Assert.Equal(contents.Revision, inspection.Revision);
        Assert.Equal(SvgStaticCompiler.RulesVersion, inspection.RulesVersion);
        var resource = Assert.Single(inspection.Resources);
        Assert.Equal(10, resource.RawPathCommands); Assert.Equal(10, resource.ExpandedPathCommands);
        Assert.Equal(2, resource.RawElements); Assert.True(resource.EstimatedPictureOperations >= 10);
        Assert.Equal(0, resource.ValidationDecodedImageBytes); Assert.Equal(0, resource.EstimatedNativeDecodedImageBytes);
        var plan = Assert.Single(resource.AtlasPlans);
        Assert.Equal("fixed", plan.VariantId);
        Assert.Equal(new[] { (19, 11), (9, 5), (4, 2), (2, 1), (1, 1) }, plan.Levels.Select(l => (l.Width, l.Height)));
        Assert.Equal(plan.Levels.Sum(l => l.Bytes), plan.MipBytes);
        Assert.True(inspection.EstimatedPrepareBytes > plan.MipBytes);
        Assert.Contains(resource.Dependencies, d => d.Path == "assets/main.svg" && d.Sha256 == contents.Files.Single(f => f.Role == "svg").Sha256);
    }

    [Fact]
    public void CSS真实token处理注释转义quotedURL及固定级联后移除CSS入口()
    {
        const string body = """
            <style><![CDATA[
              path { fill: blue; stroke-width: 2 }
              .ink { f\69ll: u\72l("\23 paint"); stroke: rgb(100%,0%,0%) }
              #star { opacity: .75 }
            ]]></style>
            <defs>
              <linearGradient id="paint" gradientUnits="objectBoundingBox"><stop offset="0" stop-color="#000"/><stop offset="100%" stop-color="#fff"/></linearGradient>
              <clipPath id="clip" clipPathUnits="userSpaceOnUse"><rect width="100" height="100"/></clipPath>
              <symbol id="shape" viewBox="0 0 10 10"><path id="star" class="ink" style="stroke-width:3" d="M0 0 L10 10"/></symbol>
            </defs>
            <use href="#shape" width="100" height="100" clip-path="url(#clip)"/>
            """;
        var contents = Package(Svg(body)); var compiled = Compile(contents); var document = Assert.Single(compiled.Documents);
        Assert.DoesNotContain("<style", document.SafeXml, StringComparison.Ordinal); Assert.DoesNotContain("class=", document.SafeXml, StringComparison.Ordinal);
        Assert.DoesNotContain("style=", document.SafeXml, StringComparison.Ordinal);
        Assert.Contains("fill=\"url(#paint)\"", document.SafeXml, StringComparison.Ordinal);
        Assert.Contains("stroke-width=\"3\"", document.SafeXml, StringComparison.Ordinal);
        Assert.Contains("stroke=\"rgb(255,0,0)\"", document.SafeXml, StringComparison.Ordinal);
        Assert.Contains("opacity=\"0.75\"", document.SafeXml, StringComparison.Ordinal);
        Assert.Equal(3, document.Inspection.CssRules); Assert.Equal(6, document.Inspection.CssDeclarations);
        Assert.True(document.Inspection.ExpandedPathCommands > document.Inspection.RawPathCommands);
        Assert.All(document.SourceMap, location => { Assert.Equal("assets/main.svg", location.Path); Assert.True(location.Line > 0); Assert.True(location.Column > 0); });
    }

    [Fact]
    public void namespace按URI处理且安全CDATA只在元数据和style承载文字()
    {
        const string xml = """
            <v:svg xmlns:v="http://www.w3.org/2000/svg" xmlns:x="http://www.w3.org/1999/xlink" viewBox="0 0 100 100">
              <v:title><![CDATA[<safe>&]]></v:title>
              <v:defs><v:path id="p" d="M0 0 L1 1"/></v:defs><v:use x:href="#p"/>
            </v:svg>
            """;
        var document = Assert.Single(Compile(Package(xml)).Documents);
        Assert.DoesNotContain("v:", document.SafeXml, StringComparison.Ordinal);
        Assert.DoesNotContain("x:href", document.SafeXml, StringComparison.Ordinal);
        Assert.Contains("href=\"#p\"", document.SafeXml, StringComparison.Ordinal);
        Assert.Contains("&lt;safe&gt;&amp;", document.SafeXml, StringComparison.Ordinal);
    }

    [Fact]
    public void 有限filter的默认输入显式固定且surface只是逻辑预算()
    {
        const string body = """
            <defs><filter id="f" filterUnits="userSpaceOnUse" primitiveUnits="userSpaceOnUse" x="0" y="0" width="100" height="100">
              <feGaussianBlur stdDeviation="2" result="blur"/><feOffset dx="1" dy="-2"/>
              <feColorMatrix type="matrix" values="1 0 0 0 0 0 1 0 0 0 0 0 1 0 0 0 0 0 1 0" result="color"/>
              <feMerge><feMergeNode in="SourceGraphic"/><feMergeNode in="color"/></feMerge>
            </filter></defs><rect width="80" height="80" filter="url(#f)"/>
            """;
        var document = Assert.Single(Compile(Package(Svg(body))).Documents);
        Assert.Equal(5, document.Inspection.FilterPasses); Assert.True(document.Inspection.FilterSurfaceBytes > 100 * 100 * 4);
        Assert.Contains("in=\"SourceGraphic\"", document.SafeXml, StringComparison.Ordinal);
        Assert.Contains("in=\"blur\"", document.SafeXml, StringComparison.Ordinal);
        Assert.Contains("color-interpolation-filters=\"sRGB\"", document.SafeXml, StringComparison.Ordinal);
    }

    [Fact]
    public void 未使用坏源也拒绝且外来descriptor不能授权另一个修订()
    {
        var first = Package(Svg("<rect width='1' height='1'/>"));
        var second = Package(Svg("<rect width='2' height='2'/>"), resourceId: "another");
        Assert.Equal("PackageSvgResourceOwnershipMismatch", Assert.Throws<EffectPackageException>(() =>
            new PackageSvgValidator().Validate(second, EffectPackageSvgResources.Read(first))).Code);
        var unused = Package(Svg("<defs><path d='M0 0 Lbad'/></defs>"));
        Assert.Equal("PackageSvgSyntaxInvalid", Assert.Throws<EffectPackageException>(() => Compile(unused)).Code);
    }

    [Fact]
    public void 取消在任何XML或native工作前传播()
    {
        var contents = Package(Svg("<rect width='1' height='1'/>"));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => new PackageSvgValidator().Validate(contents,
            EffectPackageSvgResources.Read(contents), cancellation.Token));
    }

    private static string Svg(string body) => "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 100 100'>" + body + "</svg>";
    private static SvgStaticPackage Compile(EffectPackageContents contents, SvgResourceBudget? budget = null)
        => SvgStaticCompiler.CompilePackage(contents, EffectPackageSvgResources.Read(contents), budget ?? SvgResourceBudget.Default, default);
    private static EffectPackageContents Package(string svg, bool atlas = false, Dictionary<string, byte[]>? images = null, string resourceId = "graphic")
    {
        Dictionary<string, byte[]> files = new(StringComparer.Ordinal) { ["assets/main.svg"] = Encoding.UTF8.GetBytes(svg) };
        if (images is not null) foreach (var pair in images) files.Add(pair.Key, pair.Value);
        JsonObject descriptor = new()
        {
            ["svgResourceVersion"] = 1, ["id"] = resourceId, ["source"] = "assets/main.svg",
            ["profile"] = EffectPackageSvgResources.StaticProfile, ["mode"] = atlas ? "atlas" : "vector",
            ["viewport"] = new JsonObject { ["width"] = 100, ["height"] = 100 }, ["fonts"] = new JsonArray(),
            ["images"] = new JsonArray([.. (images?.Keys.AsEnumerable() ?? Enumerable.Empty<string>()).Select(path => (JsonNode?)JsonValue.Create(path))]),
        };
        if (atlas) descriptor["atlas"] = new JsonObject
        {
            ["variants"] = new JsonArray(new JsonObject { ["id"] = "fixed", ["width"] = 15, ["height"] = 7 }),
            ["paddingPx"] = 2, ["mipmaps"] = true,
        };
        files.Add("resources/main.json", Encoding.UTF8.GetBytes(descriptor.ToJsonString()));
        JsonArray entries = [];
        foreach (var pair in files) entries.Add(new JsonObject
        {
            ["path"] = pair.Key, ["role"] = pair.Key.EndsWith(".svg", StringComparison.Ordinal) ? "svg"
                : pair.Key.EndsWith(".json", StringComparison.Ordinal) ? "svgResource" : "image",
            ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(pair.Value)),
        });
        JsonObject manifest = new()
        {
            ["packageVersion"] = EffectPackageProfiles.V2.PackageVersion, ["sdkVersion"] = EffectPackageProfiles.V2.SdkVersion,
            ["shaderProfile"] = EffectPackageProfiles.V2.ShaderProfile, ["packageId"] = "svg.validator.tests", ["name"] = "SVG validator",
            ["version"] = "1.0.0", ["requires"] = new JsonArray(atlas ? EffectPackageSvgResources.AtlasCapability : EffectPackageSvgResources.VectorCapability),
            ["lineExit"] = new JsonObject { ["durationUs"] = 0 }, ["emitters"] = new JsonArray(),
            ["files"] = entries, ["parameters"] = new JsonArray(), ["exports"] = new JsonArray(),
        };
        return EffectPackageContents.CreateSupported(Encoding.UTF8.GetBytes(manifest.ToJsonString()),
            files.Select(pair => KeyValuePair.Create(pair.Key, (ReadOnlyMemory<byte>)pair.Value)));
    }
}
