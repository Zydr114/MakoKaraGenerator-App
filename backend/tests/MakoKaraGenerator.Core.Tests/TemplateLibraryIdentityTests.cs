using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Core.Tests;

public sealed class TemplateLibraryIdentityTests
{
    [Fact]
    public void 只读身份允许真实内置但原持久入口仍拒绝且同一writer保持字节()
    {
        Assert.Equal(2, StyleTemplateLibrary.BuiltIns.Count);
        byte[] builtIn = StyleTemplateLibrary.EncodeForIdentity(StyleTemplateLibrary.BuiltIns);
        Assert.Equal(builtIn, StyleTemplateLibrary.EncodeForIdentity(StyleTemplateLibrary.BuiltIns));
        Assert.Throws<ArgumentException>(() => StyleTemplateLibrary.Encode(StyleTemplateLibrary.BuiltIns));
        var own = StyleTemplateLibrary.BuiltIns[0] with { Id = "个人/固定身份", Name = "渐变\"名称" };
        Assert.Equal(StyleTemplateLibrary.Encode([own]), StyleTemplateLibrary.EncodeForIdentity([own]));
        Assert.Throws<ArgumentException>(() => StyleTemplateLibrary.EncodeForIdentity([own, own]));
    }

    [Fact]
    public void 完整未选包材质参数精度参与身份且不访问颜色facade()
    {
        var package = MixedEffectPackageFixture.MaterialContents();
        var parameters = EffectParameterOverrides.Parse("{\"amount\":0.12345678901234567890123456789}");
        var material = new MaterialSpec.PackageMaterial(package.PackageId, package.Revision, "ink", parameters);
        var template = StyleTemplate.Create("advanced", "完整", StyleTemplateKind.Line,
            LyricStyle.Default with { UnsungFill = new() { Material = material } }, [LyricStyleProperty.FontSizePx], packages: [package]);
        byte[] original = StyleTemplateLibrary.EncodeForIdentity([template]);
        Assert.Equal(StyleTemplateLibrary.Encode([template]), original);
        var modified = template with { Style = template.Style with { UnsungFill = new() { Material = new MaterialSpec.PackageMaterial(package.PackageId, package.Revision, "ink", EffectParameterOverrides.Parse("{\"amount\":0.12345678901234567890123456788}")) } } };
        Assert.NotEqual(original, StyleTemplateLibrary.EncodeForIdentity([modified]));
        Assert.Equal("{\"amount\":0.12345678901234567890123456789}", material.Parameters.Json);
    }
}
