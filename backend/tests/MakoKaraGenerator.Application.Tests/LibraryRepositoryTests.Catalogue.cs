using System.Diagnostics;
using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Application.Tests;

public sealed partial class LibraryRepositoryTests
{
    [Fact]
    public async Task 目录监视缺失目录中真实另一个进程新增修改删除且已选固定模板不升级()
    {
        using TemporaryDirectory temp = new();
        string path = StyleTemplateLibrary.ResolvePath(Path.Combine(temp.Path, "new-config"));
        var repo = new TemplateLibraryRepository(path); await using var catalogue = new TemplateLibraryCatalogue(repo);
        using var active = catalogue.Activate(); await catalogue.WaitForIdleAsync(); Assert.Empty(catalogue.Snapshot.Library.Templates);
        using (var child = Child.Start(temp, "add", path, "add", new { Id = "external", Name = "外部新增" }))
        { await child.Ready(); child.Go(); Assert.Equal("ok", await child.Result()); }
        await CatalogueUntil(catalogue, () => catalogue.Snapshot.CanApply && catalogue.Snapshot.Library.Templates.Count == 1);
        var fixedTemplate = Assert.Single(catalogue.Snapshot.Library.Templates);
        using (var child = Child.Start(temp, "edit", path, "edit", new { Id = "external", Name = "外部修改" }))
        { await child.Ready(); child.Go(); Assert.Equal("ok", await child.Result()); }
        await CatalogueUntil(catalogue, () => catalogue.Snapshot.CanApply && catalogue.Snapshot.Library.Templates.Single().Name == "外部修改");
        Assert.Equal("外部新增", fixedTemplate.Name);
        using (var child = Child.Start(temp, "delete", path, "delete", new { Id = "external", Name = "外部删除" }))
        { await child.Ready(); child.Go(); Assert.Equal("ok", await child.Result()); }
        await CatalogueUntil(catalogue, () => catalogue.Snapshot.CanApply && catalogue.Snapshot.Library.Templates.Count == 0);
    }

    [Fact]
    public async Task 资源损坏即使索引字节未变仍复核完整闭包损坏索引也保留最后展示()
    {
        using TemporaryDirectory temp = new(); string path = StyleTemplateLibrary.ResolvePath(temp.Path);
        var package = MixedEffectPackageFixture.MaterialContents();
        var template = StyleTemplate.Create("material", "包模板", StyleTemplateKind.Line,
            LyricStyle.Default with { UnsungFill = new() { Material = new MaterialSpec.PackageMaterial(package.PackageId, package.Revision, "ink") } },
            [LyricStyleProperty.UnsungColor], packages: [package]);
        var repo = new TemplateLibraryRepository(path); repo.Save(repo.Read().Revision, [template]);
        await using var catalogue = new TemplateLibraryCatalogue(repo); using var active = catalogue.Activate(); await catalogue.WaitForIdleAsync();
        Assert.True(catalogue.Snapshot.CanApply); var revision = catalogue.Snapshot.Library.Revision;
        string manifest = Path.Combine(Path.GetDirectoryName(path)!, StyleTemplateLibrary.PackagePath(package.Revision), "manifest.json");
        byte[] validManifest = File.ReadAllBytes(manifest); File.WriteAllText(manifest, "broken package");
        await CatalogueUntil(catalogue, () => catalogue.Snapshot.Error is not null);
        Assert.False(catalogue.Snapshot.CanApply); Assert.Single(catalogue.Snapshot.Library.Templates);
        Assert.Equal(revision, repo.ReadRevision()); File.WriteAllBytes(manifest, validManifest);
        await CatalogueUntil(catalogue, () => catalogue.Snapshot.CanApply);
        byte[] validIndex = File.ReadAllBytes(path); File.WriteAllText(path, "broken index");
        await CatalogueUntil(catalogue, () => catalogue.Snapshot.Error is not null);
        Assert.Single(catalogue.Snapshot.Library.Templates); Assert.False(catalogue.Snapshot.CanApply);
        File.WriteAllBytes(path, validIndex); await CatalogueUntil(catalogue, () => catalogue.Snapshot.CanApply);
    }

    private static async Task CatalogueUntil(TemplateLibraryCatalogue catalogue, Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition() && watch.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(10);
        Assert.True(condition(), catalogue.Snapshot.Error ?? "目录通知超时");
    }
}
