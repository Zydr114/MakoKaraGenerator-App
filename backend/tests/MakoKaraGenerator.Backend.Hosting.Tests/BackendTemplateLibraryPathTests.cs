using System.Text.Json.Nodes;
using MakoKaraGenerator.Application;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

public sealed class BackendTemplateLibraryPathTests
{
    [Fact]
    public async Task 旧固定目录保留字节和包且新规范库可创建重启读取()
    {
        using TemporaryDirectory temp = new();
        string legacy = Legacy(temp), current = Current(temp);
        var template = PackagedTemplate(); StyleTemplateLibrary.Save(legacy, [template]);
        var json = JsonNode.Parse(File.ReadAllText(legacy))!;
        json["schemaVersion"] = StyleTemplateLibrary.CurrentSchemaVersion - 1;
        File.WriteAllText(legacy, json.ToJsonString());
        var before = Files(Path.GetDirectoryName(legacy)!);
        string id;
        await using (var registry = Registry(temp))
        {
            var empty = Accepted(await registry.ReadTemplatesAsync("personal", null));
            Assert.Equal("missing", empty.Revision); Assert.Equal(0, empty.TotalCount);
            var created = Accepted(await registry.ExecuteTemplateLibraryCommandAsync("client", "personal", Create(empty.Revision)));
            id = created.TemplateId; Assert.True(File.Exists(current));
        }
        await using (var restarted = Registry(temp))
            Assert.Equal(id, Assert.Single(Accepted(await restarted.ReadTemplatesAsync("personal", null)).Templates).TemplateId);
        AssertFiles(before, Path.GetDirectoryName(legacy)!);
        Assert.False(Directory.Exists(Path.Combine(Path.GetDirectoryName(current)!, "packages")));
    }

    [Fact]
    public async Task 已有当前固定目录保留完整包并在原目录继续写入()
    {
        using TemporaryDirectory temp = new(); string legacy = Legacy(temp);
        var template = PackagedTemplate(); StyleTemplateLibrary.Save(legacy, [template]);
        var packages = Files(Path.Combine(Path.GetDirectoryName(legacy)!, "packages"));
        await using (var registry = Registry(temp))
        {
            var loaded = Accepted(await registry.ReadTemplatesAsync("personal", null));
            Assert.Equal(template.Id, Assert.Single(loaded.Templates).TemplateId);
            Accepted(await registry.ExecuteTemplateLibraryCommandAsync("client", "personal", Create(loaded.Revision)));
            Assert.Equal(2, StyleTemplateLibrary.Load(legacy).Templates.Count);
            Assert.False(File.Exists(Current(temp)));
        }
        await using (var restarted = Registry(temp))
            Assert.Equal(2, Accepted(await restarted.ReadTemplatesAsync("personal", null)).TotalCount);
        AssertFiles(packages, Path.Combine(Path.GetDirectoryName(legacy)!, "packages"));
    }

    [Theory]
    [InlineData("unknown")] [InlineData("future")] [InlineData("zero")] [InlineData("negative")]
    [InlineData("fraction")] [InlineData("string")] [InlineData("null")] [InlineData("boolean")]
    [InlineData("broken")] [InlineData("duplicate")] [InlineData("oversize")]
    [InlineData("directory")] [InlineData("current-closure")]
    public async Task 固定入口无法明确确认为旧版本时拒绝且内置仍可读取(string problem)
    {
        using TemporaryDirectory temp = new(); string legacy = Legacy(temp);
        SeedInvalid(legacy, problem);
        await using var registry = Registry(temp); // Configuration itself must not perform personal I/O.
        Assert.Equal(2, Accepted(await registry.ReadTemplatesAsync("builtin", null)).TotalCount);
        Assert.Equal(2, registry.ReadTemplateLibraries().Libraries.Length);
        Invalid(await registry.ReadTemplatesAsync("personal", null));
        Invalid(await registry.ExecuteTemplateLibraryCommandAsync("client", "personal", Create("missing")));
        Assert.False(File.Exists(Current(temp)));
    }

    [Theory]
    [InlineData("broken")] [InlineData("future")] [InlineData("current-closure")]
    public async Task 新规范入口损坏时严格拒绝而不退回空库(string problem)
    {
        using TemporaryDirectory temp = new(); SeedInvalid(Current(temp), problem);
        await using var registry = Registry(temp);
        Invalid(await registry.ReadTemplatesAsync("personal", null));
        Invalid(await registry.ExecuteTemplateLibraryCommandAsync("client", "personal", Create("missing")));
        Assert.Equal(2, Accepted(await registry.ReadTemplatesAsync("builtin", null)).TotalCount);
        Assert.False(File.Exists(Legacy(temp)));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task 两个可能为当前数据的入口并存时个人库明确拒绝(bool brokenNew)
    {
        using TemporaryDirectory temp = new(); StyleTemplateLibrary.Save(Legacy(temp), [PackagedTemplate()]);
        if (brokenNew) SeedInvalid(Current(temp), "broken"); else StyleTemplateLibrary.Save(Current(temp), []);
        var legacy = Files(Path.GetDirectoryName(Legacy(temp))!); var current = Files(Path.GetDirectoryName(Current(temp))!);
        await using var registry = Registry(temp);
        Invalid(await registry.ReadTemplatesAsync("personal", null));
        Invalid(await registry.ExecuteTemplateLibraryCommandAsync("client", "personal", Create("missing")));
        Assert.Equal(2, Accepted(await registry.ReadTemplatesAsync("builtin", null)).TotalCount);
        AssertFiles(legacy, Path.GetDirectoryName(Legacy(temp))!); AssertFiles(current, Path.GetDirectoryName(Current(temp))!);
    }

    [Fact]
    public async Task 首次选定后入口固定损坏不能切换到另一库()
    {
        using TemporaryDirectory temp = new();
        await using var registry = Registry(temp);
        Assert.Equal(0, Accepted(await registry.ReadTemplatesAsync("personal", null)).TotalCount);
        SeedInvalid(Current(temp), "broken"); StyleTemplateLibrary.Save(Legacy(temp), [PackagedTemplate()]);
        Invalid(await registry.ReadTemplatesAsync("personal", null));
        File.Delete(Current(temp));
        Assert.Equal(0, Accepted(await registry.ReadTemplatesAsync("personal", null)).TotalCount);
        Accepted(await registry.ExecuteTemplateLibraryCommandAsync("client", "personal", Create("missing")));
        Assert.Single(StyleTemplateLibrary.Load(Current(temp)).Templates);
        Assert.Equal("existing", Assert.Single(StyleTemplateLibrary.Load(Legacy(temp)).Templates).Id);
    }

    [Fact]
    public async Task 选址失败可在原进程修复后重试()
    {
        using TemporaryDirectory temp = new(); SeedInvalid(Legacy(temp), "broken");
        await using var registry = Registry(temp);
        Invalid(await registry.ReadTemplatesAsync("personal", null));
        File.WriteAllText(Legacy(temp), $"{{\"schemaVersion\":{StyleTemplateLibrary.CurrentSchemaVersion - 1}}}");
        Assert.Equal(0, Accepted(await registry.ReadTemplatesAsync("personal", null)).TotalCount);
        Accepted(await registry.ExecuteTemplateLibraryCommandAsync("client", "personal", Create("missing")));
        Assert.True(File.Exists(Current(temp)));
    }

    [Fact]
    public async Task 当前固定库延续原有注释和尾逗号JSON读取语义()
    {
        using TemporaryDirectory temp = new(); string legacy = Legacy(temp);
        StyleTemplateLibrary.Save(legacy, [PackagedTemplate()]);
        string json = File.ReadAllText(legacy); int end = json.LastIndexOf('}');
        File.WriteAllText(legacy, "/* retained index */" + json.Insert(end, ","));
        await using var registry = Registry(temp);
        Assert.Equal("existing", Assert.Single(Accepted(await registry.ReadTemplatesAsync("personal", null)).Templates).TemplateId);
        Assert.False(File.Exists(Current(temp)));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task 固定入口及祖先链接不可被当作可忽略旧库(bool directoryLink)
    {
        using TemporaryDirectory temp = new(); string outside = temp.FilePath("outside"); Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "index.json"), $"{{\"schemaVersion\":{StyleTemplateLibrary.CurrentSchemaVersion - 1}}}");
        string legacy = Legacy(temp); Directory.CreateDirectory(Path.GetDirectoryName(Path.GetDirectoryName(legacy))!);
        if (directoryLink) Directory.CreateSymbolicLink(Path.GetDirectoryName(legacy)!, outside);
        else { Directory.CreateDirectory(Path.GetDirectoryName(legacy)!); File.CreateSymbolicLink(legacy, Path.Combine(outside, "index.json")); }
        await using var registry = Registry(temp);
        Assert.Equal(2, Accepted(await registry.ReadTemplatesAsync("builtin", null)).TotalCount);
        Invalid(await registry.ReadTemplatesAsync("personal", null)); Assert.False(File.Exists(Current(temp)));
    }

    private static void SeedInvalid(string path, string problem)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (problem == "directory") { Directory.CreateDirectory(path); return; }
        if (problem == "current-closure")
        {
            var template = PackagedTemplate(); StyleTemplateLibrary.Save(path, [template]);
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(path)!, StyleTemplateLibrary.PackagePath(template.Packages[0].Revision), "src/PackEffect.cs"), "corrupt");
            return;
        }
        string json = problem switch
        {
            "future" => $"{{\"schemaVersion\":{StyleTemplateLibrary.CurrentSchemaVersion + 1}}}",
            "zero" => "{\"schemaVersion\":0}", "negative" => "{\"schemaVersion\":-1}",
            "fraction" => "{\"schemaVersion\":1.5}", "string" => "{\"schemaVersion\":\"6\"}",
            "null" => "{\"schemaVersion\":null}", "boolean" => "{\"schemaVersion\":true}", "unknown" => "{}",
            "duplicate" => $"{{\"schemaVersion\":{StyleTemplateLibrary.CurrentSchemaVersion},\"schemaVersion\":1}}",
            _ => "{broken",
        };
        File.WriteAllText(path, json);
        if (problem == "oversize") { using var stream = File.OpenWrite(path); stream.SetLength(StyleTemplateLibrary.MaximumIndexBytes + 1L); }
    }

    private static StyleTemplate PackagedTemplate()
    {
        var package = EffectPackageFixture.Contents();
        return StyleTemplate.Create("existing", "带包模板", StyleTemplateKind.Token, LyricStyle.Default, [],
            [new("recipe", "强调", TemplateEffectTarget.TokenFamily, package.PackageId, package.Revision, "scale")], [package]);
    }
    private static string Legacy(TemporaryDirectory temp) => temp.FilePath("state/libraries/templates/index.json");
    private static string Current(TemporaryDirectory temp) => StyleTemplateLibrary.ResolvePath(temp.FilePath("state/libraries"));
    private static ProjectSessionRegistry Registry(TemporaryDirectory temp)
    {
        ResourceStore resources = new(new() { ResourceRoot = temp.FilePath("resources") });
        ProjectSessionRegistry registry = new("host", resources, NewProjectPreparationService.CreateDefault());
        registry.ConfigurePersistence(new() { StateRoot = temp.FilePath("state") }); registry.ConfigureTemplateLibrary(); return registry;
    }
    private static TemplateLibraryCommandRequest Create(string revision) => new(revision,
        new(Guid.NewGuid().ToString("N"), "host", DateTimeOffset.UtcNow.AddMinutes(1)),
        new CreateTemplateCommandDto(new("新模板", TemplateKind.Line, ProjectSession.ExportTemplateLibraryStyle(LyricStyle.Default), [TemplateProperty.FontSizePx])));
    private static T Accepted<T>(BackendOperationResult<T> result) { Assert.True(result.Accepted, result.Error?.ToString()); return result.Response!; }
    private static void Invalid<T>(BackendOperationResult<T> result) { Assert.False(result.Accepted); Assert.Equal("LibraryInvalid", result.Error?.FailureCode); }
    private static Dictionary<string, byte[]> Files(string directory) => Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
        .ToDictionary(file => Path.GetRelativePath(directory, file), File.ReadAllBytes, StringComparer.Ordinal);
    private static void AssertFiles(Dictionary<string, byte[]> before, string directory)
    {
        var after = Files(directory); Assert.Equal(before.Keys.Order(), after.Keys.Order());
        foreach (var item in before) Assert.Equal(item.Value, after[item.Key]);
    }
}
