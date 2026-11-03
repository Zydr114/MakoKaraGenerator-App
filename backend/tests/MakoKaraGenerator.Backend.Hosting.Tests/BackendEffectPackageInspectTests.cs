using System.Text;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using MakoKaraGenerator.Application;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.TestSupport;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

public sealed class BackendEffectPackageInspectTests
{
    [Fact]
    public void 合法带BOM图文件的输入声明完整保留()
    {
        var original = EffectPackageGraphFixture.Contents();
        var manifest = JsonNode.Parse(original.Manifest.AsSpan())!.AsObject();
        var files = original.Files.ToDictionary(value => value.Path, value => value.Content.ToArray(), StringComparer.Ordinal);
        files[EffectPackageGraphFixture.GraphPath] = [0xef, 0xbb, 0xbf, .. files[EffectPackageGraphFixture.GraphPath]];
        foreach (JsonNode? file in manifest["files"]!.AsArray())
            if ((string?)file!["path"] == EffectPackageGraphFixture.GraphPath)
                file["sha256"] = Convert.ToHexStringLower(SHA256.HashData(files[EffectPackageGraphFixture.GraphPath]));
        var package = EffectPackageContents.Create(EffectPackageFixture.Bytes(manifest), EffectPackageFixture.Inputs(files), EffectPackageFixture.Profile);
        _ = EffectPackageDefinition.Read(package);
        var result = ProjectSessionRegistry.CreateEffectPackageInspection(package, new("asset", package.PackageId, package.Revision, "asset"));
        Assert.True(result.StructureValid);
        var input = Assert.Single(result.Inputs);
        Assert.Equal(EffectPackageGraphFixture.GraphPath, input.Path);
        Assert.Equal("TargetColor", input.Inputs[0].GetProperty("source").GetString());
        Assert.True(input.Inputs[0].GetProperty("capture").GetProperty("includeRuby").GetBoolean());
    }

    [Fact]
    public async Task 检查完整九类参数保精度且不编译执行源码或泄漏主机路径()
    {
        using TemporaryDirectory directory = new();
        var files = EffectPackageFixture.Files();
        files["src/PackEffect.cs"] = Encoding.UTF8.GetBytes("this is not compilable C#, and must never run");
        files["test.png"] = [1, 2, 3]; // Structure inspection does not decode images.
        var manifest = EffectPackageFixture.Manifest(files);
        foreach (JsonNode? node in (JsonArray)manifest["files"]!)
            if ((string?)node!["path"] == "test.png") node["role"] = "image";
        var parameters = (JsonArray)manifest["parameters"]!;
        foreach (string declaration in new[]
        {
            """{"id":"whole","type":"integer","label":"Integer","default":9007199254740993,"min":0,"max":9223372036854775807}""",
            """{"id":"time","type":"duration","label":"Time","default":42,"min":0,"max":9223372036854775807}""",
            """{"id":"flag","type":"boolean","label":"Flag","default":true}""",
            """{"id":"choice","type":"enum","label":"Choice","default":"b","values":[{"value":"a","label":"Alpha"},{"value":"b","label":"Beta"}]}""",
            """{"id":"text","type":"text","label":"Text","default":""}""",
            """{"id":"color","type":"color","label":"Color","default":{"r":0.1,"g":0.2,"b":0.3,"a":0.4}}""",
            """{"id":"curve","type":"curve","label":"Curve","default":{"kind":"easing","easing":"linear"}}""",
            """{"id":"asset","type":"asset","label":"Image","default":"test.png"}""",
        }) parameters.Add(JsonNode.Parse(declaration));
        var package = EffectPackageContents.Create(EffectPackageFixture.Bytes(manifest), EffectPackageFixture.Inputs(files), EffectPackageFixture.Profile);
        ResourceStore resources = new(new() { ResourceRoot = directory.FilePath("assets") });
        var asset = await Upload(directory, resources, package);
        await using ProjectSessionRegistry registry = new("host", resources, NewProjectPreparationService.CreateDefault());
        var response = Accepted(await registry.InspectEffectPackageAsync("client", new(asset.AssetId)));
        Assert.True(response.StructureValid); Assert.Empty(response.Diagnostics); Assert.Equal(9, response.Parameters.Length);
        Assert.Equal("9007199254740993", response.Parameters.Single(value => value.Id == "whole").DefaultValueJson);
        Assert.Equal("9223372036854775807", response.Parameters.Single(value => value.Id == "whole").MaximumJson);
        Assert.Equal(["Alpha", "Beta"], response.Parameters.Single(value => value.Id == "choice").Options.Select(value => value.Label));
        Assert.Equal(["tokens"], response.BindingKinds); Assert.Equal("controller", Assert.Single(response.Exports).Kind);
        Assert.Equal(package.Files.Select(value => (value.Path, value.Role, value.Sha256)), response.Files.Select(value => (value.Path, value.Role, value.Sha256)));
        Assert.DoesNotContain(directory.Path, JsonSerializer.Serialize(response));
        Assert.Equal(package.Revision, Accepted(await registry.ResolveEffectPackageReferenceAsync("client", response.Reference)).Revision);
        Assert.Equal(BackendErrorCode.RevisionConflict, (await registry.InspectEffectPackageAsync("client", new(asset.AssetId, "changed"))).Error!.Code);
        Assert.Equal(BackendErrorCode.ResourceUnauthorized, (await registry.InspectEffectPackageAsync("other", new(asset.AssetId))).Error!.Code);
        Assert.Equal(BackendErrorCode.InvalidRequest, (await registry.InspectEffectPackageAsync("client", new(asset.AssetId, Reference: response.Reference))).Error!.Code);
        Assert.Equal(BackendErrorCode.InvalidRequest, (await registry.ResolveEffectPackageReferenceAsync("client", response.Reference with { LibraryId = "personal" })).Error!.Code);
    }

    [Fact]
    public async Task 结构诊断保留原清单而不把坏参数当合法schema()
    {
        using TemporaryDirectory directory = new();
        var files = EffectPackageFixture.Files(); var manifest = EffectPackageFixture.Manifest(files);
        manifest["parameters"]![0]!["default"] = 100;
        var package = EffectPackageContents.Create(EffectPackageFixture.Bytes(manifest), EffectPackageFixture.Inputs(files), EffectPackageFixture.Profile);
        ResourceStore resources = new(new() { ResourceRoot = directory.FilePath("assets") });
        var asset = await Upload(directory, resources, package);
        await using ProjectSessionRegistry registry = new("host", resources, NewProjectPreparationService.CreateDefault());
        var inspected = Accepted(await registry.InspectEffectPackageAsync("client", new(asset.AssetId)));
        Assert.False(inspected.StructureValid); Assert.Empty(inspected.Parameters); Assert.Empty(inspected.Exports);
        Assert.Equal("PackageParameterOutOfRange", Assert.Single(inspected.Diagnostics).Code);
        Assert.Equal(100, inspected.Manifest.GetProperty("parameters")[0].GetProperty("default").GetInt32());
    }

    [Fact]
    public async Task 库内固定引用无需上传且复核库模板与包三层身份()
    {
        using TemporaryDirectory directory = new(); var package = EffectPackageFixture.Contents();
        var template = StyleTemplate.Create("advanced", "高级", StyleTemplateKind.Line, LyricStyle.Default, [],
            [new("recipe", "Scale", TemplateEffectTarget.TokenFamily, package.PackageId, package.Revision, "scale")], [package]);
        string state = directory.FilePath("state"); string index = StyleTemplateLibrary.ResolvePath(Path.Combine(state, "libraries"));
        StyleTemplateLibrary.Save(index, [template]);
        ResourceStore resources = new(new() { ResourceRoot = directory.FilePath("assets") });
        await using ProjectSessionRegistry registry = new("host", resources, NewProjectPreparationService.CreateDefault());
        registry.ConfigurePersistence(new() { StateRoot = state }); registry.ConfigureTemplateLibrary();
        var list = Accepted(await registry.ReadTemplatesAsync("personal", null));
        var reference = ProjectSessionRegistry.CreateLibraryEffectPackageReference("personal", list.Revision, template.Id,
            Assert.Single(list.Templates).TemplateRevision, package);
        var response = Accepted(await registry.InspectEffectPackageAsync("client", new(Reference: reference)));
        Assert.True(response.StructureValid); Assert.Equal(package.Revision, response.PackageRevision); Assert.Equal("library", response.Reference.Kind);
        Assert.Equal(BackendErrorCode.RevisionConflict, (await registry.ResolveEffectPackageReferenceAsync("client", reference with { LibraryRevision = new('0', 64) })).Error!.Code);
        Assert.Equal(BackendErrorCode.RevisionConflict, (await registry.ResolveEffectPackageReferenceAsync("client", reference with { TemplateRevision = new('0', 64) })).Error!.Code);
        Assert.Equal(BackendErrorCode.ResourceNotFound, (await registry.ResolveEffectPackageReferenceAsync("client", reference with { PackageRevision = "sha256:" + new string('0', 64) })).Error!.Code);
    }

    private static T Accepted<T>(BackendOperationResult<T> result) { Assert.True(result.Accepted, result.Error?.ToString()); return result.Response!; }
    private static async Task<StoredAsset> Upload(TemporaryDirectory directory, ResourceStore resources, EffectPackageContents package)
    {
        string archive = directory.FilePath("package.mkfx"); await EffectPackageTransfer.WriteArchiveAsync(archive, package);
        await using var stream = File.OpenRead(archive);
        return await resources.UploadAsync("client", "package.mkfx", "application/octet-stream", stream);
    }
}
