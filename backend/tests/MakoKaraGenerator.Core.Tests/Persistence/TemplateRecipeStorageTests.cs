using System.Text.Json.Nodes;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Core.Tests.Persistence;

public sealed class TemplateRecipeStorageTests
{
    private static StyleTemplate Template()
    {
        var package = EffectPackageFixture.Contents(); return StyleTemplate.Create("own", "模板", StyleTemplateKind.Token, LyricStyle.Default, [],
            [new("r", "强调", TemplateEffectTarget.TokenFamily, package.PackageId, package.Revision, "scale", EffectParameterOverrides.Parse("{\"peak\":1.2345678901234567}"))], [package]);
    }
    [Fact]
    public void 库及保存对全部包共享文件预算并且可以显式调高()
    {
        using TemporaryDirectory temp = new(); var first = Template(); var package = EffectPackageFixture.Contents(2);
        var second = StyleTemplate.Create("second", "第二", StyleTemplateKind.Token, LyricStyle.Default, [],
            [new("r", "强调", TemplateEffectTarget.TokenFamily, package.PackageId, package.Revision, "scale")], [package]);
        string path = StyleTemplateLibrary.ResolvePath(temp.Path); StyleTemplateLibrary.Save(path, [first, second]); var bytes = File.ReadAllBytes(path);
        var budget = new EffectPackageContentBudget { MaximumFiles = 8 };
        Assert.NotEmpty(StyleTemplateLibrary.Load(path, budget).Issues);
        Assert.Throws<EffectPackageException>(() => StyleTemplateLibrary.Save(path, [first, second], budget)); Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Empty(StyleTemplateLibrary.Load(path, budget with { MaximumFiles = 9 }).Issues);
    }

    [Fact]
    public void 新目录不读旧库且独立闭包精度往返和删除只改索引()
    {
        using TemporaryDirectory temp = new(); temp.WriteFile("templates.json", "bad legacy"); string path = StyleTemplateLibrary.ResolvePath(temp.Path);
        Assert.True(StyleTemplateLibrary.Load(path).LegacyLibraryRetained); var template = Template(); StyleTemplateLibrary.Save(path, [template]);
        var loaded = StyleTemplateLibrary.Load(path); Assert.Empty(loaded.Issues); var restored = Assert.Single(loaded.Templates);
        Assert.Equal(template.EffectRecipes.ToArray(), restored.EffectRecipes.ToArray()); Assert.Equal(template.Packages[0].Revision, restored.Packages[0].Revision);
        Assert.Equal(StyleTemplateLibrary.Encode([template]), StyleTemplateLibrary.Encode([restored]));
        var packagePath = Path.Combine(Path.GetDirectoryName(path)!, StyleTemplateLibrary.PackagePath(template.Packages[0].Revision));
        StyleTemplateLibrary.Save(path, []); Assert.True(Directory.Exists(packagePath)); Assert.Empty(StyleTemplateLibrary.Load(path).Templates);
        Assert.Equal("bad legacy", temp.ReadFile("templates.json"));
    }
    [Theory]
    [InlineData("version")] [InlineData("unknown")] [InlineData("missingRecipe")] [InlineData("resource")] [InlineData("path")] [InlineData("parameter")] [InlineData("binding")] [InlineData("style")]
    public void 破损入口或资源拒绝整库及后续写入保持原件(string problem)
    {
        using TemporaryDirectory temp = new(); var template = Template(); string path = StyleTemplateLibrary.ResolvePath(temp.Path); StyleTemplateLibrary.Save(path, [template]);
        var root = JsonNode.Parse(File.ReadAllText(path))!;
        switch(problem)
        {
            case "version": root["schemaVersion"] = 999; break;
            case "unknown": root["templates"]![0]!["future"] = 1; break;
            case "missingRecipe": root["templates"]![0]!.AsObject().Remove("effectRecipes"); break;
            case "resource": File.WriteAllText(Path.Combine(Path.GetDirectoryName(path)!, StyleTemplateLibrary.PackagePath(template.Packages[0].Revision), "src/PackEffect.cs"), "corrupted"); break;
            case "path": root["packages"]![0]!["path"] = "../external"; break;
            case "parameter": root["templates"]![0]!["effectRecipes"]![0]!["parameters"]!["peak"] = 99; break;
            case "style": root["templates"]![0]!["style"] = null; break;
            case "binding": root["templates"]![0]!["effectRecipes"]![0]!["target"] = "line"; break;
        }
        File.WriteAllText(path, root.ToJsonString()); var original = File.ReadAllBytes(path); var loaded = StyleTemplateLibrary.Load(path);
        Assert.Empty(loaded.Templates); Assert.NotEmpty(loaded.Issues); Assert.Equal(problem == "version", loaded.VersionMismatch);
        Assert.Throws<IOException>(() => StyleTemplateLibrary.Save(path, [])); Assert.Equal(original, File.ReadAllBytes(path));
    }
    [Fact]
    public void 链接入口及包拒绝而非读取外部文件()
    {
        using TemporaryDirectory temp = new(); string path = StyleTemplateLibrary.ResolvePath(temp.Path); var template = Template(); StyleTemplateLibrary.Save(path, [template]);
        string saved = temp.FilePath("external.json"); File.Move(path, saved);
        bool fileLinked = true;
        try { File.CreateSymbolicLink(path, saved); }
        catch (IOException error) when (OperatingSystem.IsWindows() && error.HResult == unchecked((int)0x80070522)) { fileLinked = false; }
        if (fileLinked)
        {
            Assert.NotEmpty(StyleTemplateLibrary.Load(path).Issues); Assert.ThrowsAny<IOException>(() => StyleTemplateLibrary.Save(path, []));
            File.Delete(path);
        }
        File.Move(saved, path);
        string package = Path.Combine(Path.GetDirectoryName(path)!, StyleTemplateLibrary.PackagePath(template.Packages[0].Revision));
        string externalDir = temp.FilePath("external");
        Directory.Move(package, externalDir);
        try { Directory.CreateSymbolicLink(package, externalDir); }
        catch (IOException error) when (OperatingSystem.IsWindows() && error.HResult == unchecked((int)0x80070522))
        {
            using var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{package}\" \"{externalDir}\"") { CreateNoWindow = true, UseShellExecute = false })!;
            mklink.WaitForExit(); Assert.Equal(0, mklink.ExitCode);
        }
        try
        {
            Assert.NotEmpty(StyleTemplateLibrary.Load(path).Issues);
        }
        finally
        {
            if (Directory.Exists(package))
            {
                Directory.Delete(package, false);
            }
        }
    }
}
