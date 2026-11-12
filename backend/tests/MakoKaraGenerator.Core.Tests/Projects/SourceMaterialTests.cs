using System.Text.Json.Nodes;
using MakoKaraGenerator.Core.Diagnostics;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.Core.Validation;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Core.Tests.Projects;

public sealed class SourceMaterialTests
{
    private static readonly GradientSpec Gradient = GradientSpec.Create([new(0, new(255, 0, 0, 80)), new(1, new(0, 0, 255, 160))]);
    private static MaterialSpec.PackageMaterial Material(EffectPackageContents package, string parameters = "{\"amount\":0.12345678901234567}")
        => new(package.PackageId, package.Revision, "ink", EffectParameterOverrides.Parse(parameters));
    private static Project Source(EffectPackageContents package) => Project.Create("源材质", lyrics: LyricsDocument.Create([
        LyricLine.Create("line", "甲乙", tokens: [LyricToken.Create("a", "甲"), LyricToken.Create("b", "乙")]),
    ])) with { Effects = new([package], []), LyricStyle = LyricStyle.Default with { UnsungFill = new() { Material = Material(package) } } };

    [Fact]
    public void 静态颜色只编辑真实首色标关闭渐变不恢复隐藏颜色()
    {
        var fill = new FillSpec { Color = new RgbaColor(0, 255, 0), Gradient = Gradient };
        Assert.Equal(Gradient.Stops[0].Color, fill.Color);
        var changed = fill with { Color = RgbaColor.White };
        Assert.Equal(RgbaColor.White, changed.Gradient!.Stops[0].Color);
        Assert.Equal(Gradient.Stops[1], changed.Gradient.Stops[1]);
        Assert.Equal(new MaterialSpec.Solid(RgbaColor.White), (changed with { Gradient = null }).Material);
        var stroke = new StrokeSpec { Color = null, Gradient = Gradient, WidthPx = 3 };
        Assert.Equal(new MaterialSpec.Solid(Gradient.Stops[0].Color), (stroke with { Gradient = null }).Material);
    }

    [Theory]
    [InlineData(LyricStyleProperty.UnsungFill)]
    public void 任一材质编辑地址整体复制包引用且恢复整项继承(LyricStyleProperty property)
    {
        var package = MixedEffectPackageFixture.Contents(); var project = Source(package);
        var applied = StyleBatchEditor.Apply(project, StyleTargetLevel.Token, [new(0, 0)], project.LyricStyle, [property]);
        var overlay = applied.Lyrics.Lines[0].Tokens[0].StyleOverride!;
        Assert.Equal(project.LyricStyle.UnsungFill.Material, overlay.UnsungFill.Value!.Material.Value);
        Assert.Single(TokenStyleSupport.SetProperties(overlay));
        Assert.True(TokenStyleSupport.HasDrawableOverride(overlay));
        var resolved = LyricStyleResolver.Resolve(lineOverride: new() { UnsungColor = RgbaColor.Black }, tokenOverride: overlay);
        Assert.Equal(project.LyricStyle.UnsungFill.Material, resolved.Style.UnsungFill.Material);
        Assert.Equal(LyricStyleSource.Token, resolved.Sources[LyricStyleProperty.UnsungFill]);
        var restored = StyleBatchEditor.Apply(applied, StyleTargetLevel.Token, [new(0, 0)], project.LyricStyle, [property], restoreInheritance: true);
        Assert.Null(restored.Lyrics.Lines[0].Tokens[0].StyleOverride);
    }

    [Fact]
    public void 包材质不伪造纯色且传统颜色渐变写入拒绝覆盖它()
    {
        var package = MixedEffectPackageFixture.Contents(); var fill = Source(package).LyricStyle.UnsungFill;
        Assert.Throws<EffectPackageException>(() => fill.Color);
        Assert.Throws<EffectPackageException>(() => fill with { Color = RgbaColor.White });
        Assert.Throws<EffectPackageException>(() => fill with { Gradient = null });
        Assert.Throws<ArgumentException>(() => new FillSpecOverride { Gradient = OptionalValue.Set<GradientSpec?>(null) });
        Assert.Equal(RgbaColor.White, (fill with { Material = new MaterialSpec.Solid(RgbaColor.White) }).Color);
    }

    [Fact]
    public void 源材质与描边几何独立往返且不保存第二份颜色()
    {
        using TemporaryDirectory directory = new(); var package = MixedEffectPackageFixture.Contents(); var project = Source(package);
        var stroke = new StrokeSpec { Material = Material(package), WidthPx = 4, Join = StrokeJoin.Bevel };
        project = project with { LyricStyle = project.LyricStyle with { SungFill = new() { Material = new MaterialSpec.Gradient(Gradient) }, Stroke = stroke } };
        project = StyleBatchEditor.Apply(project, StyleTargetLevel.Token, [new(0, 0)], project.LyricStyle, [LyricStyleProperty.UnsungFill, LyricStyleProperty.UnsungStrokes]);
        var token = project.Lyrics.Lines[0].Tokens[0]; var line = project.Lyrics.Lines[0]; var branch = project.Lyrics.PrimaryBranch!;
        token = token with { StyleOverride = token.StyleOverride! with { UnsungStrokes = StrokeLayers.FromStroke(stroke with { WidthPx = 8 }) } };
        project = project with { Lyrics = project.Lyrics with { Branches = project.Lyrics.Branches.Replace(branch, branch with
            { Lines = branch.Lines.Replace(line, line with { Tokens = line.Tokens.Replace(line.Tokens[0], token) }) }) } };
        ProjectStore.Save(project, directory.Path); var loaded = ProjectStore.Load(directory.Path); Assert.NotNull(loaded.Project);
        Assert.Equal(project.LyricStyle, loaded.Project.LyricStyle);
        var resolved = LyricStyleResolver.Resolve(loaded.Project.LyricStyle, tokenOverride: loaded.Project.Lyrics.Lines[0].Tokens[0].StyleOverride).Style;
        Assert.Equal(stroke.Material, resolved.Stroke!.Material); Assert.Equal(8, resolved.Stroke.WidthPx);
        Assert.Equal(EffectParameterOverrides.Parse("{\"amount\":0.12345678901234567}"), ((MaterialSpec.PackageMaterial)resolved.UnsungFill.Material).Parameters);
        var root = JsonNode.Parse(File.ReadAllText(Path.Combine(directory.Path, "project.json")))!;
        Assert.Equal("packageMaterial", root["lyricStyle"]!["unsung"]!["fill"]!["kind"]!.GetValue<string>());
        Assert.Null(root["lyricStyle"]!["unsung"]!["fill"]!["color"]);
        Assert.Equal("gradient", root["lyricStyle"]!["sung"]!["fill"]!["kind"]!.GetValue<string>());
        Assert.Null(root["lyricStyle"]!["unsung"]!["strokes"]![0]!["color"]);
        Assert.Equal(4, root["lyricStyle"]!["unsung"]!["strokes"]![0]!["widthPx"]!.GetValue<int>());
        Assert.Equal(Project.CurrentSchemaVersion, root["schemaVersion"]!.GetValue<int>());
        Assert.Equal(LyricsDocument.CurrentSchemaVersion, JsonNode.Parse(ProjectFileFixture.ReadLyrics(directory.Path))!["schemaVersion"]!.GetValue<int>());
    }

    [Theory]
    [InlineData("legacy")]
    [InlineData("mixed")]
    [InlineData("null")]
    [InlineData("missing")]
    [InlineData("shortColor")]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    [InlineData("incompleteGradient")]
    [InlineData("nullGradientKind")]
    [InlineData("numericGradientKind")]
    [InlineData("unsortedGradient")]
    public void 非法材质判别对象取消读取不产生纯色替代(string fault)
    {
        using TemporaryDirectory directory = new(); ProjectStore.Save(Project.Create("严格"), directory.Path);
        string path = Path.Combine(directory.Path, "project.json"); var root = JsonNode.Parse(File.ReadAllText(path))!;
        root["lyricStyle"]!["unsung"]!["fill"] = fault switch
        {
            "legacy" => JsonNode.Parse("{\"color\":\"#FFFFFFFF\",\"gradient\":null}"),
            "mixed" => JsonNode.Parse("{\"kind\":\"solid\",\"color\":\"#FFFFFFFF\",\"packageId\":\"x\"}"),
            "null" => null, "missing" => new JsonObject(),
            "shortColor" => JsonNode.Parse("{\"kind\":\"solid\",\"color\":\"#FFFFFF\"}"),
            "unknown" => JsonNode.Parse("{\"kind\":\"glass\"}"),
            "incompleteGradient" => JsonNode.Parse("{\"kind\":\"gradient\",\"gradient\":{\"kind\":\"Linear\"}}"),
            _ => JsonNode.Parse("{\"kind\":\"solid\",\"color\":\"#FFFFFFFF\"}"),
        };
        if (fault is "nullGradientKind" or "numericGradientKind" or "unsortedGradient")
        {
            var fallback = MixedEffectPackageFixture.Gradient(); var gradient = fallback["gradient"]!;
            if (fault == "nullGradientKind") gradient["kind"] = null;
            if (fault == "numericGradientKind") gradient["kind"] = "0";
            if (fault == "unsortedGradient") gradient["stops"]![1]!["offset"] = -.1;
            root["lyricStyle"]!["unsung"]!["fill"] = fallback;
        }
        string bytes = root.ToJsonString();
        if (fault == "duplicate") bytes = bytes.Replace("\"kind\":\"solid\"", "\"kind\":\"solid\",\"kind\":\"solid\"", StringComparison.Ordinal);
        File.WriteAllText(path, bytes); var loaded = ProjectStore.Load(directory.Path);
        Assert.Null(loaded.Project); Assert.True(loaded.Report.HasErrors); Assert.Equal(bytes, File.ReadAllText(path));
    }

    [Fact]
    public void 缺失材质修订拒绝保存并保护旧入口()
    {
        using TemporaryDirectory directory = new(); ProjectStore.Save(Project.Create("旧"), directory.Path);
        string path = Path.Combine(directory.Path, "project.json"); var bytes = File.ReadAllBytes(path);
        var project = Source(MixedEffectPackageFixture.Contents()) with { Effects = ProjectEffects.Empty };
        Assert.Equal("SourceMaterialPackageMissing", Assert.Throws<EffectPackageException>(() => ProjectStore.Save(project, directory.Path)).Code);
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Fact]
    public void 非法参数草稿保留真实引用并报告语义错误而不丢失资源()
    {
        using TemporaryDirectory directory = new(); var package = MixedEffectPackageFixture.Contents(); var project = Source(package);
        project = project with { LyricStyle = project.LyricStyle with { UnsungFill = new() { Material = Material(package, "{\"amount\":\"bad\"}") } } };
        ProjectStore.Save(project, directory.Path); var loaded = ProjectStore.Load(directory.Path); Assert.NotNull(loaded.Project);
        Assert.Equal(project.LyricStyle, loaded.Project.LyricStyle);
        Assert.Contains(loaded.Report.Issues, issue => issue.Code == DiagnosticCode.InvalidEffectInstance && issue.Location!.StartsWith("lyricStyle.unsung.fill", StringComparison.Ordinal));
        Assert.True(ProjectValidator.Validate(loaded.Project, ValidationContext.Export).HasErrors);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void 模板拒绝错误导出或区域工程则保留诊断供修复(bool wrongRegion)
    {
        var package = MixedEffectPackageFixture.Contents(manifestChange: manifest =>
        {
            if (wrongRegion) manifest["exports"]!.AsArray().Single(e => e!["kind"]!.GetValue<string>() == "material")!["regions"] = new JsonArray("stroke");
        });
        var material = wrongRegion ? Material(package) : new MaterialSpec.PackageMaterial(package.PackageId, package.Revision, "tint");
        var style = LyricStyle.Default with { UnsungFill = new() { Material = material } };
        Assert.Throws<EffectPackageException>(() => StyleTemplate.Create("bad", "坏引用", StyleTemplateKind.Line, style, [LyricStyleProperty.FontSizePx], packages: [package]));
        var project = Source(package) with { LyricStyle = style };
        Assert.Contains(ProjectValidator.Validate(project, ValidationContext.Open).Issues, issue => issue.Code == DiagnosticCode.InvalidEffectInstance
            && issue.Location!.StartsWith("lyricStyle.unsung.fill", StringComparison.Ordinal));
    }

    [Fact]
    public void 源材质参数身份和包注销同时考虑不可见元素与标记()
    {
        var package = MixedEffectPackageFixture.Contents(); var project = LeadInFixture.Create(); var branch = project.Lyrics.PrimaryBranch!;
        var line = branch.Lines[0]; var token = line.Tokens[0]; var material = Material(package);
        var overlay = new LyricStyleOverride { UnsungFill = new FillSpecOverride { Material = material } };
        project = project with { Effects = new([package], []), Lyrics = project.Lyrics with { Branches = project.Lyrics.Branches.Replace(branch,
            branch with { StyleOverride = overlay, Lines = branch.Lines.Replace(line, line with { StyleOverride = overlay,
                Tokens = line.Tokens.Replace(token, token with { StyleOverride = overlay }) }) }) } };
        var sequence = project.LyricPresentation.Nodes.First(n => n.Content is CountdownSequenceContent);
        var mark = project.LyricPresentation.Nodes.First(n => n.Content is TextMarkContent);
        project = project with { LyricPresentation = project.LyricPresentation with { Nodes = project.LyricPresentation.Nodes
            .Replace(sequence, sequence with { Content = ((CountdownSequenceContent)sequence.Content!) with { Appearance = SceneTextAppearance.Default with { PendingFill = new() { Material = material } } } })
            .Replace(mark, mark with { Content = ((TextMarkContent)mark.Content!) with { AppearanceOverride = SceneTextAppearanceOverride.FromStyleOverride(overlay) } }) } };
        Assert.Equal(5, SourceMaterialCatalog.Collect(project).Length); SourceMaterialCatalog.RequireResources(project);
        Assert.All(SourceMaterialCatalog.Collect(project), r => SourceMaterialCatalog.Validate(r, EffectPackageDefinition.Read(package)));
        Assert.NotNull(ProjectEffectInputs.Fingerprint(project));
        Assert.Equal("EffectPackageInUse", Assert.Throws<EffectPackageException>(() => ProjectEffectsEditor.Unregister(project, package.PackageId, package.Revision)).Code);
        Assert.NotEqual(ProjectEffectInputs.Fingerprint(project), ProjectEffectInputs.Fingerprint(Source(package)));
    }

    [Fact]
    public void 仅材质模板带完整闭包且库写入新目录保留旧版()
    {
        using TemporaryDirectory directory = new(); string old = Path.Combine(directory.Path, "templates-v4", "index.json");
        Directory.CreateDirectory(Path.GetDirectoryName(old)!); File.WriteAllText(old, "旧库");
        var package = MixedEffectPackageFixture.Contents(); var style = Source(package).LyricStyle;
        Assert.Throws<ArgumentException>(() => StyleTemplate.Create("own", "材质", StyleTemplateKind.Token, style, [LyricStyleProperty.UnsungFill]));
        var template = StyleTemplate.Create("own", "材质", StyleTemplateKind.Token, style, [LyricStyleProperty.UnsungFill], packages: [package]);
        string path = StyleTemplateLibrary.ResolvePath(directory.Path); Assert.Contains(StyleTemplateLibrary.DirectoryName, path, StringComparison.Ordinal);
        Assert.True(StyleTemplateLibrary.Load(path).LegacyLibraryRetained);
        StyleTemplateLibrary.Save(path, [template]); var loaded = StyleTemplateLibrary.Load(path); Assert.Empty(loaded.Issues);
        Assert.Equal(style, Assert.Single(loaded.Templates).Style); Assert.Equal(package.Revision, loaded.Templates[0].Packages[0].Revision);
        Assert.Equal("旧库", File.ReadAllText(old));
        Assert.True(StyleTemplateApplicator.TryPreview(Project.Create("套用", lyrics: LyricsDocument.Create([LyricLine.Create("l", "甲", tokens: [LyricToken.Create("t", "甲")])])),
            loaded.Templates[0], "l", "t", false, out var applied, out var reason), reason);
        Assert.Equal(package.Revision, Assert.Single(applied.Result.Effects.Packages).Revision);
        Assert.Equal(expected: style.UnsungFill.Material, actual: applied.Result.Lyrics.Lines[0].Tokens[0].StyleOverride!.UnsungFill.Value!.Material.Value);
    }
}
