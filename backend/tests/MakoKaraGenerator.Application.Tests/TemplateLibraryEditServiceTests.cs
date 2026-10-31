using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Application.Tests;

public sealed class TemplateLibraryEditServiceTests
{
    private static StaticTemplateDefinition Definition(string name = "静态") => new(name, StyleTemplateKind.Line,
        LyricStyle.Default, [LyricStyleProperty.FontSizePx]);

    [Fact]
    public async Task 缺库准备无副作用一次固定ID提交和全量等值替换保真实字节()
    {
        using TemporaryDirectory directory = new(); var repository = new TemplateLibraryRepository(directory.FilePath("index.json"));
        var service = new TemplateLibraryEditService(repository); var missing = service.Read(TemplateLibraryCatalog.Personal);
        Assert.Equal(LibraryIndexRevision.Missing, missing.Revision);
        var prepared = service.Prepare(missing, new TemplateLibraryEdit.Create(Definition()));
        Assert.True(prepared.Changed); Assert.False(File.Exists(repository.Path));
        string id = prepared.TemplateId; Assert.Equal(id, Assert.Single(prepared.Templates).Id);
        var saved = await service.CommitPreparedAsync(prepared); Assert.Equal(id, Assert.Single(saved.Templates).Id);
        File.AppendAllText(repository.Path, "\n  "); var raw = File.ReadAllBytes(repository.Path);
        var source = service.Read(TemplateLibraryCatalog.Personal);
        var same = service.Prepare(source, new TemplateLibraryEdit.Replace(id, Definition()));
        Assert.False(same.Changed); Assert.Same(source.Templates[0], same.Templates[0]);
        Assert.Equal(source.Revision, (await service.CommitPreparedAsync(same)).Revision);
        Assert.Equal(raw, File.ReadAllBytes(repository.Path));
        var deleted = await service.CommitPreparedAsync(service.Prepare(source, new TemplateLibraryEdit.Delete(id)));
        Assert.Empty(deleted.Templates); Assert.NotEqual(LibraryIndexRevision.Missing, deleted.Revision);
    }

    [Fact]
    public async Task 准备后外部更改包括无变化提交均CAS拒绝且快照不能跨服务()
    {
        using TemporaryDirectory directory = new(); var repository = new TemplateLibraryRepository(directory.FilePath("index.json"));
        var service = new TemplateLibraryEditService(repository);
        var first = await service.CommitPreparedAsync(service.Prepare(service.Read(TemplateLibraryCatalog.Personal), new TemplateLibraryEdit.Create(Definition())));
        var noOp = service.Prepare(first, new TemplateLibraryEdit.Rename(first.Templates[0].Id, "静态"));
        var change = service.Prepare(first, new TemplateLibraryEdit.Rename(first.Templates[0].Id, "新名称"));
        File.AppendAllText(repository.Path, "\n"); byte[] external = File.ReadAllBytes(repository.Path);
        await Assert.ThrowsAsync<LibraryRevisionConflictException>(() => service.CommitPreparedAsync(noOp));
        await Assert.ThrowsAsync<LibraryRevisionConflictException>(() => service.CommitPreparedAsync(change));
        Assert.Equal(external, File.ReadAllBytes(repository.Path));
        var other = new TemplateLibraryEditService(repository);
        Assert.Equal(TemplateLibraryEditFailure.SnapshotStale, Assert.Throws<TemplateLibraryEditException>(() => other.Prepare(first, new TemplateLibraryEdit.Delete(first.Templates[0].Id))).Failure);
        await Assert.ThrowsAsync<TemplateLibraryEditException>(() => other.CommitPreparedAsync(change));
    }

    [Fact]
    public void 损坏个人库不当空且不影响真实内置或允许写内置()
    {
        using TemporaryDirectory directory = new(); var repository = new TemplateLibraryRepository(directory.FilePath("index.json"));
        File.WriteAllText(repository.Path, "broken"); var service = new TemplateLibraryEditService(repository);
        Assert.Equal(TemplateLibraryEditFailure.LibraryInvalid, Assert.Throws<TemplateLibraryEditException>(() => service.Read(TemplateLibraryCatalog.Personal)).Failure);
        var builtIn = service.Read(TemplateLibraryCatalog.BuiltIn); Assert.Equal(2, builtIn.Templates.Length);
        Assert.NotEqual(LibraryIndexRevision.Missing, builtIn.Revision);
        Assert.Equal(TemplateLibraryEditFailure.ReadOnlyLibrary, Assert.Throws<TemplateLibraryEditException>(() => service.Prepare(builtIn, new TemplateLibraryEdit.Create(Definition()))).Failure);
        Assert.Equal("broken", File.ReadAllText(repository.Path));
    }

    [Fact]
    public void 新全定义未选字段也严格有效并沿原类型支持清单()
    {
        using TemporaryDirectory directory = new(); var service = new TemplateLibraryEditService(new(directory.FilePath("index.json")));
        var snapshot = service.Read(TemplateLibraryCatalog.Personal);
        LyricStyle[] invalid = [LyricStyle.Default with { Opacity = 2 }, LyricStyle.Default with { Stroke = new() { WidthPx = -1 } },
            LyricStyle.Default with { Shadow = new() { DistancePx = -1 } }, LyricStyle.Default with { Typography = LyricStyle.Default.Typography with { FontSizePx = 0 } }];
        foreach (var style in invalid)
            Assert.Equal(TemplateLibraryEditFailure.InvalidDefinition, Assert.Throws<TemplateLibraryEditException>(() => service.Prepare(snapshot, new TemplateLibraryEdit.Create(Definition() with { Style = style }))).Failure);
        Assert.Throws<TemplateLibraryEditException>(() => service.Prepare(snapshot, new TemplateLibraryEdit.Create(Definition() with { Kind = StyleTemplateKind.Token, Properties = [LyricStyleProperty.HorizontalAlignment] })));
        Assert.Throws<TemplateLibraryEditException>(() => service.Prepare(snapshot, new TemplateLibraryEdit.Create(Definition() with { Kind = StyleTemplateKind.Composite, Properties = [LyricStyleProperty.VerticalCenterRatio] })));
        Assert.False(File.Exists(directory.FilePath("index.json")));
    }

    [Fact]
    public async Task 渐变库定义读回后仍可继续替换提交()
    {
        using TemporaryDirectory directory = new();
        string path = directory.FilePath("index.json");
        static GradientSpec Gradient(GradientCoordinateSpace coordinateSpace, GradientColorSpace colorSpace, GradientHuePath huePath) =>
            GradientSpec.Create(
                [new GradientStop(0d, RgbaColor.Black), new GradientStop(1d, RgbaColor.White)],
                coordinateSpace: coordinateSpace, colorSpace: colorSpace, huePath: huePath);
        LyricStyle style = LyricStyle.Default with
        {
            UnsungFill = new() { Material = new MaterialSpec.Gradient(Gradient(GradientCoordinateSpace.Glyph, GradientColorSpace.Oklch, GradientHuePath.Shorter)) },
            SungFill = new() { Material = new MaterialSpec.Gradient(Gradient(GradientCoordinateSpace.Progress, GradientColorSpace.Oklch, GradientHuePath.Longer)) },
        };
        var service = new TemplateLibraryEditService(new(path));
        var created = await service.CommitPreparedAsync(service.Prepare(service.Read(TemplateLibraryCatalog.Personal),
            new TemplateLibraryEdit.Create(new("变体", StyleTemplateKind.Line, style,
                [LyricStyleProperty.UnsungGradientSpec, LyricStyleProperty.SungGradientSpec]))));
        var loaded = service.Read(TemplateLibraryCatalog.Personal);
        Assert.Equal(style, Assert.Single(loaded.Templates).Style);

        LyricStyle replacement = style with
        {
            UnsungFill = new() { Material = new MaterialSpec.Gradient(Gradient(GradientCoordinateSpace.Line, GradientColorSpace.LinearRgb, GradientHuePath.Shorter)) },
        };
        var replaced = service.Prepare(loaded, new TemplateLibraryEdit.Replace(Assert.Single(loaded.Templates).Id,
            new("替换", StyleTemplateKind.Line, replacement, [LyricStyleProperty.UnsungGradientSpec])));
        var committed = await service.CommitPreparedAsync(replaced);
        Assert.Equal(replacement, Assert.Single(committed.Templates).Style);
        Assert.Empty(service.Read(TemplateLibraryCatalog.Personal).Templates
            .Where(item => item.Style.UnsungFill.Gradient?.CoordinateSpace == GradientCoordinateSpace.Glyph));
        Assert.Equal(GradientColorSpace.Oklch, committed.Templates.Single().Style.SungFill.Gradient!.ColorSpace);
    }

    [Fact]
    public async Task 高级完整闭包允许复制改名删除但禁止整体替换且不删除历史资源()
    {
        using TemporaryDirectory directory = new(); string path = directory.FilePath("index.json");
        var package = MixedEffectPackageFixture.MaterialContents(); var parameters = EffectParameterOverrides.Parse("{\"amount\":0.12345678901234567890123456789}");
        var advanced = StyleTemplate.Create("legacy/高级", "旧模板", StyleTemplateKind.Line,
            LyricStyle.Default with { UnsungFill = new() { Material = new MaterialSpec.PackageMaterial(package.PackageId, package.Revision, "ink", parameters) } },
            [LyricStyleProperty.FontSizePx], packages: [package]);
        StyleTemplateLibrary.Save(path, [advanced]); var service = new TemplateLibraryEditService(new(path)); var source = service.Read(TemplateLibraryCatalog.Personal);
        Assert.False(TemplateLibraryEditService.IsStatic(source.Templates[0]));
        Assert.Equal(TemplateLibraryEditFailure.AdvancedDefinitionUnsupported, Assert.Throws<TemplateLibraryEditException>(() => service.Prepare(source, new TemplateLibraryEdit.Replace(advanced.Id, Definition()))).Failure);
        var copy = service.Prepare(source, new TemplateLibraryEdit.Copy(advanced.Id, "副本"), source);
        var copied = copy.Templates.Single(item => item.Id == copy.TemplateId);
        Assert.Same(source.Templates[0].Style, copied.Style); Assert.Same(source.Templates[0].Packages[0], copied.Packages[0]);
        var saved = await service.CommitPreparedAsync(copy);
        var renamed = await service.CommitPreparedAsync(service.Prepare(saved, new TemplateLibraryEdit.Rename(copy.TemplateId, "重命名")));
        var actual = renamed.Templates.Single(item => item.Id == copy.TemplateId);
        Assert.Equal(parameters.Json, Assert.IsType<MaterialSpec.PackageMaterial>(actual.Style.UnsungFill.Material).Parameters.Json);
        Assert.Equal(package.Revision, actual.Packages[0].Revision);
        var after = await service.CommitPreparedAsync(service.Prepare(renamed, new TemplateLibraryEdit.Delete(copy.TemplateId)));
        Assert.Single(after.Templates);
        Assert.True(Directory.Exists(Path.Combine(directory.Path, StyleTemplateLibrary.PackagePath(package.Revision))));
    }
}
