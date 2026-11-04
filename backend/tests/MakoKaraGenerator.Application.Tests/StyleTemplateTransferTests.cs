using System.IO.Compression;
using System.Text.Json.Nodes;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Application.Tests;

public sealed class StyleTemplateTransferTests
{
    [Fact]
    public async Task 纯求值v2模板通过分享自核验后保留实际SDK身份和包闭包()
    {
        using TemporaryDirectory temp = new(); var files = EffectPackageFixture.Files(); var manifest = EffectPackageFixture.Manifest(files);
        manifest["packageVersion"] = EffectPackageProfiles.V2.PackageVersion; manifest["sdkVersion"] = "2.0.7";
        manifest["shaderProfile"] = EffectPackageProfiles.V2.ShaderProfile; manifest["requires"] = new JsonArray("token.affine");
        manifest["lineExit"] = new JsonObject { ["durationUs"] = 0 }; manifest["emitters"] = new JsonArray();
        var package = EffectPackageContents.CreateSupported(EffectPackageFixture.Bytes(manifest), EffectPackageFixture.Inputs(files));
        var configuration = ScriptConfiguration.Create("pure", "纯求值", package, "scale", target: ScriptRelativeTarget.TokenFamily);
        var template = StyleTemplate.Create("pure-template", "纯求值模板", StyleTemplateKind.Token, LyricStyle.Default, [],
            [TemplateEffectRecipe.FromConfiguration("pure-recipe", configuration)], [package]);
        string archive = temp.FilePath("pure.mktpl");
        await StyleTemplateTransfer.WriteArchiveAsync(archive, [template]);
        var restored = Assert.Single(await StyleTemplateTransfer.ReadArchiveAsync(archive));
        Assert.Equal(template, restored); Assert.Equal("2.0.7", Assert.Single(restored.Packages).Profile.SdkVersion);
        Assert.Empty(Directory.GetFiles(temp.Path, "*.tmp"));
    }

    [Fact]
    public async Task 仅包材质模板分享后脱离原库且套用工程独立保存完整修订()
    {
        using TemporaryDirectory temp = new(); var package = MixedEffectPackageFixture.Contents();
        var material = new MaterialSpec.PackageMaterial(package.PackageId, package.Revision, "ink",
            EffectParameterOverrides.Parse("{\"amount\":0.12345678901234567}"));
        var template = StyleTemplate.Create("material", "材质分享", StyleTemplateKind.Line,
            LyricStyle.Default with { UnsungFill = new() { Material = material } }, [LyricStyleProperty.UnsungColor], packages: [package]);
        string index = StyleTemplateLibrary.ResolvePath(temp.Path); StyleTemplateLibrary.Save(index, [template]);
        string archive = temp.FilePath("source-material.mktpl");
        await StyleTemplateTransfer.WriteArchiveAsync(archive, StyleTemplateLibrary.Load(index).Templates);
        Directory.Delete(Path.GetDirectoryName(index)!, true);
        var restored = Assert.Single(await StyleTemplateTransfer.ReadArchiveAsync(archive));
        Assert.Equal(material, restored.Style.UnsungFill.Material); Assert.Equal(package.Revision, Assert.Single(restored.Packages).Revision);
        var project = Project.Create("独立材质", lyrics: Core.Lyrics.LyricsDocument.Create([Core.Lyrics.LyricLine.Create("line", "歌")]));
        Assert.True(StyleTemplateApplicator.TryPreview(project, restored, "line", null, false, out var applied, out var reason), reason);
        string directory = temp.FilePath("project"); ProjectStore.Save(applied.Result, directory);
        File.Delete(archive); var reopened = ProjectStore.Load(directory); Assert.NotNull(reopened.Project);
        Assert.Equal(material, reopened.Project.Lyrics.Lines[0].StyleOverride!.UnsungFill.Value!.Material.Value);
        Assert.Equal(package.Revision, Assert.Single(reopened.Project.Effects.Packages).Revision);
        SourceMaterialCatalog.RequireResources(reopened.Project);
    }

    private static StyleTemplate Template()
    {
        var package = EffectPackageFixture.Contents(); return StyleTemplate.Create("t", "分享", StyleTemplateKind.Line, LyricStyle.Default, [],
            [new("r", "缩放", TemplateEffectTarget.TokenFamily, package.PackageId, package.Revision, "scale")], [package]);
    }
    [Fact]
    public async Task 模板分享包离开原库保持完整资源与字节稳定且静态模板可共享()
    {
        using TemporaryDirectory temp = new(); var template = Template(); string index = StyleTemplateLibrary.ResolvePath(temp.Path);
        StyleTemplateLibrary.Save(index, [template]); var loaded = StyleTemplateLibrary.Load(index).Templates;
        string file = temp.FilePath("share.mktpl"), other = temp.FilePath("other.mktpl");
        await StyleTemplateTransfer.WriteArchiveAsync(file, loaded); await StyleTemplateTransfer.WriteArchiveAsync(other, loaded);
        Assert.Equal(File.ReadAllBytes(file), File.ReadAllBytes(other)); Directory.Delete(Path.GetDirectoryName(index)!, true);
        var restored = await StyleTemplateTransfer.ReadArchiveAsync(file); Assert.Equal(StyleTemplateLibrary.Encode(loaded), StyleTemplateLibrary.Encode(restored));
        var staticTemplate = StyleTemplate.Create("static", "静态", StyleTemplateKind.Line, LyricStyle.Default, [LyricStyleProperty.SungColor]);
        await StyleTemplateTransfer.WriteArchiveAsync(other, [staticTemplate]); Assert.Empty(Assert.Single(await StyleTemplateTransfer.ReadArchiveAsync(other)).EffectRecipes);
        Assert.Empty(Directory.GetFiles(temp.Path, "*.tmp"));
    }
    [Theory]
    [InlineData("extra")] [InlineData("missing")] [InlineData("link")] [InlineData("duplicate")] [InlineData("../outside")] [InlineData("Packages/")]
    public async Task 非闭合分享拒绝额外缺失链接重复与大小写路径(string issue)
    {
        using TemporaryDirectory temp = new(); string file = temp.FilePath("share.mktpl"); var template = Template(); await StyleTemplateTransfer.WriteArchiveAsync(file, [template]);
        using (var zip = ZipFile.Open(file, ZipArchiveMode.Update))
        {
            if (issue == "missing") zip.GetEntry(StyleTemplateLibrary.PackagePath(template.Packages[0].Revision) + "/src/PackEffect.cs")!.Delete();
            else
            {
                var entry = zip.CreateEntry(issue == "duplicate" ? "index.json" : issue);
                if (issue == "link") entry.ExternalAttributes = 0xA000 << 16;
            }
        }
        await Assert.ThrowsAnyAsync<Exception>(() => StyleTemplateTransfer.ReadArchiveAsync(file)); Assert.False(File.Exists(temp.FilePath("outside")));
    }
    [Theory]
    [InlineData("file")] [InlineData("total")] [InlineData("count")]
    public async Task 归档展开受显式预算且失败保持旧目标(string kind)
    {
        using TemporaryDirectory temp = new(); string file = temp.FilePath("share.mktpl"), old = temp.FilePath("old.mktpl"); var template = Template();
        await StyleTemplateTransfer.WriteArchiveAsync(file, [template]); File.WriteAllText(old, "old");
        var budget = kind switch { "file" => new EffectPackageContentBudget { MaximumFileBytes = 1 },
            "total" => new EffectPackageContentBudget { MaximumBytes = 1 }, _ => new EffectPackageContentBudget { MaximumFiles = 1 } };
        await Assert.ThrowsAsync<EffectPackageException>(() => StyleTemplateTransfer.ReadArchiveAsync(file, budget));
        await Assert.ThrowsAsync<EffectPackageException>(() => StyleTemplateTransfer.WriteArchiveAsync(old, [template], budget));
        Assert.Equal("old", File.ReadAllText(old)); Assert.Empty(Directory.GetFiles(temp.Path, "*.tmp"));
    }
    [Fact]
    public async Task 取消分享及无法发布保持旧产物并清理本次临时文件()
    {
        using TemporaryDirectory temp = new(); string file = temp.FilePath("old.mktpl"); File.WriteAllText(file, "old");
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => StyleTemplateTransfer.WriteArchiveAsync(file, [Template()], cancellationToken: cancel.Token));
        Assert.Equal("old", File.ReadAllText(file)); string directory = temp.FilePath("blocked.mktpl"); Directory.CreateDirectory(directory);
        await Assert.ThrowsAnyAsync<IOException>(() => StyleTemplateTransfer.WriteArchiveAsync(directory, [Template()]));
        Assert.Empty(Directory.GetFiles(temp.Path, "*.tmp"));
    }
}
