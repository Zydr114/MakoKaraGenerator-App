using MakoKaraGenerator.Core.Effects;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed partial class PackageSvgValidatorTests
{
    [Theory]
    [InlineData("<script/>", "PackageSvgElementUnsupported")]
    [InlineData("<animate/>", "PackageSvgElementUnsupported")]
    [InlineData("<text>forbidden</text>", "PackageSvgElementUnsupported")]
    [InlineData("<foreignObject/>", "PackageSvgElementUnsupported")]
    [InlineData("<mask/>", "PackageSvgElementUnsupported")]
    [InlineData("<pattern/>", "PackageSvgElementUnsupported")]
    [InlineData("<rect width='1' height='1' onLoad='run()'/>", "PackageSvgDynamicUnsupported")]
    [InlineData("<rect width='1' height='1' xml:base='file:///tmp/'/>", "PackageSvgNamespaceUnsupported")]
    [InlineData("<rect width='1' height='1' unknown='ignored'/>", "PackageSvgAttributeUnsupported")]
    [InlineData("<rect width='1' height='1'><![CDATA[ ]]></rect>", "PackageSvgTextUnsupported")]
    [InlineData("<svg viewBox='0 0 1 1'/>", "PackageSvgSyntaxInvalid")]
    [InlineData("<g xmlns:bad='urn:bad'/>", "PackageSvgNamespaceUnsupported")]
    [InlineData("<path id='same' d='M0 0'/><path id='same' d='M1 1'/>", "PackageSvgIdInvalid")]
    public void 默认拒绝动态文字未知元素属性命名空间与歧义身份(string body, string code)
        => Assert.Equal(code, Assert.Throws<EffectPackageException>(() => Compile(Package(Svg(body)))).Code);

    [Theory]
    [InlineData("<!DOCTYPE svg [<!ENTITY x 'payload'>]>")]
    [InlineData("<!DOCTYPE svg SYSTEM 'file:///tmp/never-read'>")]
    [InlineData("<?xml-stylesheet type='text/css' href='https://invalid.example/style.css'?>")]
    public void DTD实体及处理指令在任何native工作前拒绝(string prefix)
        => Assert.Equal("PackageSvgXmlUnsafe", Assert.Throws<EffectPackageException>(() => Compile(Package(prefix + Svg("")))).Code);

    [Fact]
    public void href与xlink即使值相同也拒绝歧义()
        => Assert.Equal("PackageSvgSyntaxInvalid", Assert.Throws<EffectPackageException>(() => Compile(Package(Svg(
            "<defs><path id='p' d='M0 0'/></defs><use xmlns:x='http://www.w3.org/1999/xlink' href='#p' x:href='#p'/>")))).Code);

    [Theory]
    [InlineData("M0 0 L10")]
    [InlineData("M0 0 L1 2 trailing")]
    [InlineData("L0 0")]
    [InlineData("M0 0 Z 1 2")]
    [InlineData("M0 0 A1 1 0 2 0 5 5")]
    [InlineData("M0 0 A-1 1 0 0 1 5 5")]
    [InlineData("M0 0 LNaN 1")]
    [InlineData("M0 0 L1e309 1")]
    [InlineData("M0 0 L1e-100 1")]
    [InlineData("M0,,1")]
    [InlineData("M0 0,")]
    public void path完整grammar拒绝残缺命令非法flag非有限native退化和尾串(string path)
        => Assert.Equal("PackageSvgSyntaxInvalid", Assert.Throws<EffectPackageException>(() => Compile(Package(Svg("<path d='" + path + "'/>")))).Code);

    [Theory]
    [InlineData("translate(1,)")]
    [InlineData("scale(2) junk")]
    [InlineData("matrix(1 0 0 1 0)")]
    [InlineData("rotate(1 2)")]
    [InlineData("skewX(90)")]
    [InlineData("translate(1000000) translate(1000000)")]
    public void transform完整消费且累计矩阵也必须有限(string transform)
        => Assert.Equal("PackageSvgSyntaxInvalid", Assert.Throws<EffectPackageException>(() => Compile(Package(Svg("<rect width='1' height='1' transform='" + transform + "'/>")))).Code);

    [Theory]
    [InlineData("@import 'https://invalid.example/a';")]
    [InlineData("path:hover { fill:red }")]
    [InlineData("path,rect { fill:red }")]
    [InlineData("g path { fill:red }")]
    [InlineData("path { fill:var(--paint) }")]
    [InlineData("path { fill:red !important }")]
    [InlineData("path { font-size:12 }")]
    [InlineData("path { animation:none }")]
    [InlineData("path { fill:u\\72l('https://invalid.example/p') }")]
    [InlineData("path { fill:url(\"\\66 ile:///tmp/p\") }")]
    [InlineData("path { fill:red; broken }")]
    [InlineData("path { fill:red /* unfinished")]
    public void CSS转义评论引号也不能绕过静态AST规则(string css)
        => Assert.Throws<EffectPackageException>(() => Compile(Package(Svg("<style><![CDATA[" + css + "]]></style><path d='M0 0 L1 1'/>"))));

    [Theory]
    [InlineData("<use href='other.svg#p'/>", "PackageSvgReferenceUnsafe")]
    [InlineData("<use href='#missing'/>", "PackageSvgReferenceMissing")]
    [InlineData("<defs><linearGradient id='p' gradientUnits='objectBoundingBox'><stop offset='0'/></linearGradient></defs><use href='#p'/>", "PackageSvgReferenceTypeMismatch")]
    [InlineData("<defs><g id='p'><use href='#p'/></g></defs>", "PackageSvgReferenceCycle")]
    [InlineData("<defs><linearGradient id='a' gradientUnits='objectBoundingBox' href='#b'/><linearGradient id='b' gradientUnits='objectBoundingBox' href='#a'/></defs>", "PackageSvgReferenceCycle")]
    [InlineData("<defs><linearGradient id='a' gradientUnits='objectBoundingBox'><stop offset='1'/><stop offset='0'/></linearGradient></defs>", "PackageSvgSyntaxInvalid")]
    [InlineData("<defs><filter id='f' filterUnits='userSpaceOnUse' primitiveUnits='userSpaceOnUse' x='0' y='0' width='10' height='10'><feOffset in='forward' result='later'/><feOffset result='forward'/></filter></defs>", "PackageSvgFilterInputInvalid")]
    [InlineData("<defs><filter id='f' filterUnits='userSpaceOnUse' primitiveUnits='userSpaceOnUse' x='0' y='0' width='10' height='10'><feOffset result='same'/><feOffset result='same'/></filter></defs>", "PackageSvgFilterInputInvalid")]
    public void 即使未使用的本地引用和filter也验证类型闭包循环顺序(string body, string code)
        => Assert.Equal(code, Assert.Throws<EffectPackageException>(() => Compile(Package(Svg(body)))).Code);

    [Theory]
    [InlineData("elements")]
    [InlineData("depth")]
    [InlineData("attributes")]
    [InlineData("raw-path")]
    [InlineData("expanded-path")]
    [InlineData("expanded-elements")]
    [InlineData("reference-depth")]
    [InlineData("rules")]
    [InlineData("declarations")]
    [InlineData("selectors")]
    [InlineData("filter-passes")]
    [InlineData("filter-bytes")]
    [InlineData("safe-xml")]
    [InlineData("total-safe-xml")]
    [InlineData("prepare")]
    public void 固定预算每项独立拒绝而不删除节点降级或截断(string dimension)
    {
        var budget = SvgResourceBudget.Default; string body = "<rect width='10' height='10'/>";
        switch (dimension)
        {
            case "elements": budget = budget with { MaximumElements = 1 }; break;
            case "depth": budget = budget with { MaximumXmlDepth = 1 }; break;
            case "attributes": budget = budget with { MaximumAttributesPerElement = 1 }; break;
            case "raw-path": budget = budget with { MaximumPathCommands = 1 }; body = "<path d='M0 0 L1 1'/>"; break;
            case "expanded-path": budget = budget with { MaximumExpandedPathCommands = 3 }; body = "<defs><path id='p' d='M0 0 L1 1'/></defs><use href='#p'/>"; break;
            case "expanded-elements": budget = budget with { MaximumExpandedElements = 1 }; break;
            case "reference-depth": budget = budget with { MaximumReferenceDepth = 1 }; body = "<defs><path id='p' d='M0 0'/><use id='u' href='#p'/></defs><use href='#u'/>"; break;
            case "rules": budget = budget with { MaximumCssRules = 1 }; body = "<style>path{fill:red}rect{fill:blue}</style>"; break;
            case "declarations": budget = budget with { MaximumCssDeclarations = 1 }; body = "<style>path{fill:red;stroke:blue}</style>"; break;
            case "selectors": budget = budget with { MaximumSelectorSteps = 1 }; body = "<style>path{fill:red}</style>"; break;
            case "filter-passes": budget = budget with { MaximumFilterPasses = 1 }; body = Filter("<feOffset/><feOffset/>"); break;
            case "filter-bytes": budget = budget with { MaximumFilterSurfaceBytes = 1 }; body = Filter("<feOffset/>"); break;
            case "safe-xml": budget = budget with { MaximumSafeXmlBytes = 16 }; break;
            case "total-safe-xml": budget = budget with { MaximumTotalSafeXmlBytes = 16 }; break;
            case "prepare": budget = budget with { MaximumPrepareBytes = 1 }; break;
        }
        Assert.Equal("PackageSvgBudgetExceeded", Assert.Throws<EffectPackageException>(() => Compile(Package(Svg(body)), budget)).Code);
    }

    private static string Filter(string primitives) => "<defs><filter id='f' filterUnits='userSpaceOnUse' primitiveUnits='userSpaceOnUse' x='0' y='0' width='100' height='100'>" + primitives + "</filter></defs>";
}
