using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Core.Tests.Persistence;

public sealed class StyleTemplateLibraryTests
{
    [Fact]
    public void 行和元素模板往返且旧样式文件不读取()
    {
        using TemporaryDirectory directory = new();
        string templatesPath = Path.Combine(directory.Path, StyleTemplateLibrary.FileName);
        string oldStylesPath = Path.Combine(directory.Path, "styles.json");
        File.WriteAllText(oldStylesPath, "bad old styles");
        StyleTemplate[] templates =
        [
            StyleTemplate.Create("line-1", "蓝行", StyleTemplateKind.Line, LyricStyle.Default,
                [LyricStyleProperty.FontSizePx, LyricStyleProperty.SungColor]),
            StyleTemplate.Create("token-1", "亮字", StyleTemplateKind.Token, LyricStyle.Default,
                [LyricStyleProperty.SungColor]),
        ];

        StyleTemplateLibrary.Save(templatesPath, templates);
        StyleTemplateLibraryLoadResult result = StyleTemplateLibrary.Load(templatesPath);

        Assert.Empty(result.Issues);
        Assert.Equal(templates.Select(static template => template.Id),
            result.Templates.Select(static template => template.Id));
        Assert.Equal(StyleTemplateKind.Line, result.Templates[0].Kind);
        Assert.Equal(StyleTemplateKind.Token, result.Templates[1].Kind);
        Assert.Equal("bad old styles", File.ReadAllText(oldStylesPath));
    }

    [Fact]
    public void 模板库往返保留完整渐变坐标色彩空间和色相路径()
    {
        using TemporaryDirectory directory = new();
        string path = StyleTemplateLibrary.ResolvePath(directory.Path);
        static GradientSpec Gradient(GradientCoordinateSpace coordinateSpace, GradientColorSpace colorSpace, GradientHuePath huePath) =>
            GradientSpec.Create(
                [new GradientStop(0d, RgbaColor.Black), new GradientStop(1d, RgbaColor.White)],
                coordinateSpace: coordinateSpace, colorSpace: colorSpace, huePath: huePath);

        GradientSpec oklchShorter = Gradient(GradientCoordinateSpace.Line, GradientColorSpace.Oklch, GradientHuePath.Shorter);
        GradientSpec oklchLonger = Gradient(GradientCoordinateSpace.Line, GradientColorSpace.Oklch, GradientHuePath.Longer);
        GradientSpec glyph = Gradient(GradientCoordinateSpace.Glyph, GradientColorSpace.LinearRgb, GradientHuePath.Increasing);
        GradientSpec progress = Gradient(GradientCoordinateSpace.Progress, GradientColorSpace.Hsv, GradientHuePath.Decreasing);
        LyricStyle style = LyricStyle.Default with
        {
            UnsungFill = new() { Material = new MaterialSpec.Gradient(oklchShorter) },
            SungFill = new() { Material = new MaterialSpec.Gradient(oklchLonger) },
            Stroke = new() { Material = new MaterialSpec.Gradient(glyph), WidthPx = 2f },
        };
        StyleTemplate template = StyleTemplate.Create("gradient-variants", "渐变变体", StyleTemplateKind.Line, style,
            [LyricStyleProperty.UnsungGradientSpec, LyricStyleProperty.SungGradientSpec]);
        StyleTemplate progressTemplate = StyleTemplate.Create("gradient-progress", "进度渐变", StyleTemplateKind.Line,
            LyricStyle.Default with { UnsungFill = new() { Material = new MaterialSpec.Gradient(progress) } },
            [LyricStyleProperty.UnsungGradientSpec]);

        StyleTemplateLibrary.Save(path, [template, progressTemplate]);
        StyleTemplateLibraryLoadResult loaded = StyleTemplateLibrary.Load(path);

        Assert.Empty(loaded.Issues);
        Assert.Equal(template.Style, loaded.Templates.Single(item => item.Id == template.Id).Style);
        Assert.Equal(progressTemplate.Style, loaded.Templates.Single(item => item.Id == progressTemplate.Id).Style);
        Assert.Equal(StyleTemplateLibrary.Encode([template, progressTemplate]), StyleTemplateLibrary.Encode(loaded.Templates));
    }

    [Fact]
    public void 元素模板拒绝整行排版属性()
    {
        Assert.Throws<ArgumentException>(() => StyleTemplate.Create(
            "token-1", "错误", StyleTemplateKind.Token, LyricStyle.Default,
            [LyricStyleProperty.FontSizePx]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void 先检查版本而不解析不兼容样式(bool future)
    {
        int version = StyleTemplateLibrary.CurrentSchemaVersion + (future ? 1 : -1);
        string json = $"{{\"schemaVersion\":{version},\"templates\":[{{\"style\":{{\"fontSizePx\":72}}}}]}}";

        StyleTemplateLibraryLoadResult result = StyleTemplateLibrary.Deserialize(json);

        Assert.Empty(result.Templates);
        Assert.Equal(version, result.SchemaVersion);
        Assert.True(result.VersionMismatch);
        Assert.DoesNotContain("fontSizePx", Assert.Single(result.Issues), StringComparison.Ordinal);
    }

    [Fact]
    public void 当前版本的旧平面键属于损坏而非版本不兼容()
    {
        string json = $"{{\"schemaVersion\":{StyleTemplateLibrary.CurrentSchemaVersion},\"templates\":[{{\"style\":{{\"fontSizePx\":72}}}}]}}";

        StyleTemplateLibraryLoadResult result = StyleTemplateLibrary.Deserialize(json);

        Assert.False(result.VersionMismatch);
        Assert.NotEmpty(result.Issues);
    }

    [Fact]
    public void 多模板包库存顺序不影响规范入口且磁盘往返保持完整校验()
    {
        using TemporaryDirectory directory = new();
        var material = MixedEffectPackageFixture.Contents(pure: true);
        var controller = EffectPackageFixture.Contents();
        var newerController = EffectPackageFixture.Contents(preview: 2);
        var otherFiles = EffectPackageFixture.Files();
        var otherManifest = EffectPackageFixture.Manifest(otherFiles);
        otherManifest["packageId"] = "a.scale";
        var otherController = EffectPackageContents.Create(EffectPackageFixture.Bytes(otherManifest),
            EffectPackageFixture.Inputs(otherFiles), EffectPackageFixture.Profile);
        var style = LyricStyle.Default with { UnsungFill = new()
        {
            Material = new MaterialSpec.PackageMaterial(material.PackageId, material.Revision, "ink"),
        } };
        TemplateEffectRecipe[] recipes =
        [
            new("newer", "新修订", TemplateEffectTarget.TokenFamily, newerController.PackageId, newerController.Revision, "scale"),
            new("other", "另一包", TemplateEffectTarget.TokenFamily, otherController.PackageId, otherController.Revision, "scale"),
            new("graph", "行图", TemplateEffectTarget.Line, material.PackageId, material.Revision, "tint"),
            new("older", "原修订", TemplateEffectTarget.TokenFamily, controller.PackageId, controller.Revision, "scale"),
        ];
        EffectPackageContents[] inventory = [controller, material, otherController, newerController];
        var mixed = StyleTemplate.Create("mixed", "完整模板", StyleTemplateKind.Line, style,
            [LyricStyleProperty.SungColor, LyricStyleProperty.UnsungColor], recipes, inventory);
        var materialOnly = StyleTemplate.Create("material", "源材质", StyleTemplateKind.Token, style,
            [LyricStyleProperty.UnsungColor], packages: [material]);
        var controllerOnly = StyleTemplate.Create("controller", "控制器", StyleTemplateKind.Token,
            LyricStyle.Default, [], [recipes[0]], [newerController]);
        StyleTemplate[] templates = [materialOnly, mixed, controllerOnly];
        byte[] expected = StyleTemplateLibrary.Encode(templates);
        Assert.Equal(expected, StyleTemplateLibrary.Encode(
            [materialOnly, mixed with { Packages = [.. inventory.Reverse()] }, controllerOnly]));
        string path = StyleTemplateLibrary.ResolvePath(directory.Path);

        StyleTemplateLibrary.Save(path, templates);
        StyleTemplateLibraryLoadResult loaded = StyleTemplateLibrary.Load(path);

        Assert.Empty(loaded.Issues);
        Assert.False(loaded.VersionMismatch);
        Assert.Equal(expected, StyleTemplateLibrary.Encode(loaded.Templates));
        Assert.Equal(templates.Select(template => template.Id), loaded.Templates.Select(template => template.Id));
        var restored = loaded.Templates.Single(template => template.Id == mixed.Id);
        Assert.Equal(mixed.Properties.ToArray(), restored.Properties.ToArray());
        Assert.Equal(recipes, restored.EffectRecipes.ToArray());
        var sorted = inventory.OrderBy(package => package.PackageId, StringComparer.Ordinal)
            .ThenBy(package => package.Revision, StringComparer.Ordinal).ToArray();
        Assert.Equal(sorted.Select(package => (package.PackageId, package.Revision)),
            restored.Packages.Select(package => (package.PackageId, package.Revision)));
        foreach (var package in inventory)
        {
            var copy = restored.Packages.Single(item => item.PackageId == package.PackageId && item.Revision == package.Revision);
            Assert.Equal(package.Manifest.ToArray(), copy.Manifest.ToArray());
            foreach (var file in package.Files)
                Assert.Equal(file.Content.ToArray(), copy.Files.Single(item => item.Path == file.Path).Content.ToArray());
        }
        string content = Path.Combine(Path.GetDirectoryName(path)!, StyleTemplateLibrary.PackagePath(controller.Revision), "README.md");
        File.WriteAllText(content, "damaged content");
        var damaged = StyleTemplateLibrary.Load(path);
        Assert.Empty(damaged.Templates);
        Assert.NotEmpty(damaged.Issues);
    }

    [Fact]
    public void 包库存规范排序仍保留配方实际执行顺序()
    {
        var first = EffectPackageFixture.Contents();
        var second = EffectPackageFixture.Contents(preview: 2);
        TemplateEffectRecipe[] recipes =
        [
            new("second", "后修订先执行", TemplateEffectTarget.TokenFamily, second.PackageId, second.Revision, "scale"),
            new("first", "原修订后执行", TemplateEffectTarget.TokenFamily, first.PackageId, first.Revision, "scale"),
        ];
        var template = StyleTemplate.Create("ordered", "配方顺序", StyleTemplateKind.Token,
            LyricStyle.Default, [], recipes, [first, second]);
        byte[] original = StyleTemplateLibrary.Encode([template]);
        byte[] reordered = StyleTemplateLibrary.Encode([template with { EffectRecipes = [.. recipes.Reverse()] }]);

        Assert.False(original.AsSpan().SequenceEqual(reordered));
        var packages = new Dictionary<string, EffectPackageContents>
        {
            [StyleTemplateLibrary.PackagePath(first.Revision)] = first,
            [StyleTemplateLibrary.PackagePath(second.Revision)] = second,
        };
        var decoded = StyleTemplateLibrary.Deserialize(System.Text.Encoding.UTF8.GetString(reordered), path => packages[path]);
        Assert.Empty(decoded.Issues);
        Assert.Equal(recipes.Reverse(), Assert.Single(decoded.Templates).EffectRecipes);
        Assert.Equal(reordered, StyleTemplateLibrary.Encode(decoded.Templates));
    }

}
