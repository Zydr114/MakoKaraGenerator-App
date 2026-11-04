using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Rendering.Skia.Effects.Svg;
using MakoKaraGenerator.Scripting;
using SkiaSharp;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed class SvgClipProfileTests
{
    [Theory]
    [InlineData("", "clip-rule='nonzero'", "clip-rule='evenodd'", true)]
    [InlineData("", "clip-rule='evenodd'", "clip-rule='nonzero'", false)]
    [InlineData("", "clip-rule='evenodd'", "", true)]
    [InlineData("", "clip-rule='nonzero'", "", false)]
    [InlineData("<style>clipPath { clip-rule: nonzero; }</style>", "", "style='clip-rule: evenodd'", true)]
    [InlineData("", "", "clip-rule='evenodd'", true)]
    public void 裁剪规则按叶子级联覆盖及父级继承保留真实孔洞(string css, string parentRule, string childRule, bool hole)
    {
        var contents = Package(css + "<defs><clipPath id='clip' clipPathUnits='userSpaceOnUse' " + parentRule
            + "><path d='M0 0H100V100H0Z M25 25H75V75H25Z' " + childRule
            + "/></clipPath></defs><rect width='100' height='100' fill='red' clip-path='url(#clip)'/>");
        using var owner = PackageSvgResources.LoadReference(contents, EffectPackageSvgResources.Read(contents));
        var vector = owner.GetVector(new ResourceIdentity(contents.PackageId, contents.Revision, "graphic"));
        using var pixels = new SKBitmap(new SKImageInfo(100, 100, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(pixels))
        {
            canvas.Clear(SKColors.Transparent); canvas.Scale(100, 100); canvas.DrawPicture(vector.Picture);
        }
        Assert.Equal(SKColors.Red, pixels.GetPixel(10, 50));
        Assert.Equal(hole ? default : SKColors.Red, pixels.GetPixel(50, 50));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void 显式空裁剪清空像素而普通形状可选占位不改变像素(bool emptyClip)
    {
        var contents = Package((emptyClip ? "<defs><clipPath id='empty' clipPathUnits='userSpaceOnUse'/></defs>" : "")
            + "<rect width='100' height='100' fill='red'" + (emptyClip ? " clip-path='url(#empty)'" : "") + "/>");
        using var owner = PackageSvgResources.LoadReference(contents, EffectPackageSvgResources.Read(contents));
        var vector = owner.GetVector(new ResourceIdentity(contents.PackageId, contents.Revision, "graphic"));
        using var pixels = new SKBitmap(new SKImageInfo(100, 100, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(pixels))
        {
            canvas.Clear(SKColors.Transparent); canvas.Scale(100, 100); canvas.DrawPicture(vector.Picture);
        }
        for (int y = 0; y < 100; y++) for (int x = 0; x < 100; x++)
            Assert.Equal(emptyClip ? default : SKColors.Red, pixels.GetPixel(x, y));
    }

    [Theory]
    [InlineData("translate(1 2)")]
    [InlineData("translate(1\t2)")]
    [InlineData("translate(1\n2)")]
    [InlineData("translate(1 , 2 )")]
    [InlineData("rotate(12 40 40)")]
    [InlineData("matrix(1 0 0 1 2 3)")]
    [InlineData("translate(1 2) scale(2 3), rotate(5)")]
    public void 变换参数接受合法空白与逗号且前瞻不吞分隔符(string transform)
    {
        var contents = Package("<rect width='10' height='10' transform='" + transform + "'/>");
        Assert.Single(Compile(contents).Documents);
    }

    [Theory]
    [InlineData("translate(1-2)")]
    [InlineData("translate(1.2.3)")]
    [InlineData("translate(,1)")]
    [InlineData("translate(1,)")]
    [InlineData("translate(1,,2)")]
    [InlineData("translate(1 2)scale(2)")]
    [InlineData("translate(1 2),")]
    [InlineData("translate(1 2 3)")]
    public void 变换参数仍拒绝缺分隔符重复逗号与错误参数数量(string transform)
    {
        var contents = Package("<rect width='10' height='10' transform='" + transform + "'/>");
        Assert.Equal("PackageSvgSyntaxInvalid", Assert.Throws<EffectPackageException>(() => Compile(contents)).Code);
    }

    [Theory]
    [InlineData("<path d='M0 0 L90 0 L90 90 Z' clip-rule='evenodd'/>")]
    [InlineData("<rect x='10' y='10' width='60' height='70' rx='3' transform='rotate(12 40 40)'/>")]
    [InlineData("<circle cx='50' cy='50' r='35' transform='translate(2 3)'/>")]
    [InlineData("<ellipse cx='50' cy='50' rx='30' ry='20'/>")]
    [InlineData("<line x1='10' y1='10' x2='70' y2='70'/>")]
    [InlineData("<polyline points='10 10 90 10 90 90'/>")]
    [InlineData("<polygon points='10 10 90 10 50 90'/>")]
    public void 首期裁剪接受直接形状及其有限局部变换(string shapes)
    {
        var contents = Package("<defs><clipPath id='clip' clipPathUnits='userSpaceOnUse' transform='translate(1 2)'>"
            + shapes + "</clipPath></defs><rect width='100' height='100' clip-path='url(#clip)'/>");
        var document = Assert.Single(Compile(contents).Documents);
        Assert.Contains("clip-path=\"url(#clip)\"", document.SafeXml, StringComparison.Ordinal);
        Assert.True(document.Inspection.RawElements >= 5);
    }

    [Theory]
    [InlineData("", "<g transform='translate(2 3)'><rect width='50' height='50'/></g>")]
    [InlineData("", "<symbol id='inside' viewBox='0 0 10 10'><rect width='10' height='10'/></symbol>")]
    [InlineData("<rect id='shape' width='30' height='30'/>", "<use href='#shape'/>")]
    [InlineData("<rect id='shape' width='30' height='30'/>", "<use href='#shape' x='4' y='5' transform='rotate(20)'/>")]
    [InlineData("<g id='group'><circle cx='20' cy='20' r='15'/></g>", "<use href='#group'/>")]
    [InlineData("<symbol id='symbol' viewBox='0 0 10 10'><rect width='10' height='10'/></symbol>", "<use href='#symbol' width='70' height='70'/>")]
    [InlineData("<path id='shape' d='M0 0 L50 0 L50 50 Z'/><use id='alias' href='#shape' transform='scale(2)'/>", "<use href='#alias' x='4' y='5'/>")]
    public void 首期裁剪容器及任意有界use在未引用defs中也具名拒绝(string definitions, string clipContent)
    {
        foreach (bool referenced in new[] { false, true })
        {
            string body = "<defs>" + definitions + "<clipPath id='clip' clipPathUnits='userSpaceOnUse'>"
                + clipContent + "</clipPath></defs><rect width='100' height='100'"
                + (referenced ? " clip-path='url(#clip)'" : "") + "/>";
            var exception = Assert.Throws<EffectPackageException>(() => Compile(Package(body)));
            Assert.Equal("PackageSvgClipContentUnsupported", exception.Code);
            Assert.Equal("assets/main.svg", exception.Path);
        }
    }

    [Fact]
    public void 普通绘制中的组symbol与嵌套use仍接受()
    {
        var contents = Package("<defs><symbol id='tile' viewBox='0 0 10 10'><g transform='translate(1 1)'>"
            + "<rect width='8' height='8'/></g></symbol><use id='alias' href='#tile' width='20' height='20'/></defs>"
            + "<g transform='rotate(5)'><use href='#alias' x='10' y='20'/></g>");
        var document = Assert.Single(Compile(contents).Documents);
        Assert.Contains("<symbol", document.SafeXml, StringComparison.Ordinal);
        Assert.Contains("href=\"#alias\"", document.SafeXml, StringComparison.Ordinal);
    }

    private static SvgStaticPackage Compile(EffectPackageContents contents)
        => SvgStaticCompiler.CompilePackage(contents, EffectPackageSvgResources.Read(contents), SvgResourceBudget.Default, default);
    private static EffectPackageContents Package(string body)
    {
        Dictionary<string, byte[]> files = new(StringComparer.Ordinal)
        {
            ["assets/main.svg"] = Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 100 100'>" + body + "</svg>"),
            ["resources/main.json"] = Encoding.UTF8.GetBytes(new JsonObject
            {
                ["svgResourceVersion"] = 1, ["id"] = "graphic", ["source"] = "assets/main.svg",
                ["profile"] = EffectPackageSvgResources.StaticProfile, ["mode"] = "vector",
                ["viewport"] = new JsonObject { ["width"] = 100, ["height"] = 100 },
                ["fonts"] = new JsonArray(), ["images"] = new JsonArray(),
            }.ToJsonString()),
        };
        JsonArray entries = [];
        foreach (var pair in files) entries.Add(new JsonObject { ["path"] = pair.Key,
            ["role"] = pair.Key.EndsWith(".svg", StringComparison.Ordinal) ? "svg" : "svgResource",
            ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(pair.Value)) });
        JsonObject manifest = new()
        {
            ["packageVersion"] = EffectPackageProfiles.V2.PackageVersion, ["sdkVersion"] = EffectPackageProfiles.V2.SdkVersion,
            ["shaderProfile"] = EffectPackageProfiles.V2.ShaderProfile, ["packageId"] = "svg.clip.tests", ["name"] = "SVG clip profile",
            ["version"] = "1.0.0", ["requires"] = new JsonArray(EffectPackageSvgResources.VectorCapability),
            ["lineExit"] = new JsonObject { ["durationUs"] = 0 }, ["emitters"] = new JsonArray(),
            ["files"] = entries, ["parameters"] = new JsonArray(), ["exports"] = new JsonArray(),
        };
        return EffectPackageContents.CreateSupported(Encoding.UTF8.GetBytes(manifest.ToJsonString()),
            files.Select(pair => KeyValuePair.Create(pair.Key, (ReadOnlyMemory<byte>)pair.Value)));
    }
}
