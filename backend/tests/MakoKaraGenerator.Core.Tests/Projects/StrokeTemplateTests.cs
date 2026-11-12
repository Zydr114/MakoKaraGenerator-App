using MakoKaraGenerator.Core.Diagnostics;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Validation;

namespace MakoKaraGenerator.Core.Tests.Projects;

public sealed class StrokeTemplateTests
{
    private static readonly StrokeSpec Outline = StrokeSpec.Create(new RgbaColor(220, 40, 30, 180), 7f, StrokeJoin.Bevel);

    [Fact]
    public void 描边模板支持行元素和全部目标且重复套用保留工程实例()
    {
        Project project = CreateProject();
        // 旧套用适配器仍验证原行／元素写入；组合预设由新的固定应用事务验证。
        foreach (StyleTemplateKind kind in new[] { StyleTemplateKind.Line, StyleTemplateKind.Token })
        {
            StyleTemplate template = Template(kind, Outline);
            Assert.True(StyleTemplateApplicator.TryPreview(project, template, null, null, true,
                out StyleTemplateApplyPreview preview, out string reason), reason);
            Assert.Equal(kind == StyleTemplateKind.Line ? 2 : 3, preview.ChangedCount);
            Assert.All(preview.Result.Lyrics.Lines, line =>
            {
                if (kind == StyleTemplateKind.Line) Assert.Equal(Outline, line.StyleOverride!.Stroke.Value);
                else Assert.All(line.Tokens, token => Assert.Equal(Outline, token.StyleOverride!.Stroke.Value));
            });
            Assert.True(StyleTemplateApplicator.TryPreview(preview.Result, template, null, null, true,
                out StyleTemplateApplyPreview repeated, out reason), reason);
            Assert.Equal(0, repeated.ChangedCount);
            Assert.Same(preview.Result, repeated.Result);
        }
    }

    [Fact]
    public void 描边覆盖的继承启用关闭和恢复相互独立()
    {
        Project project = CreateProject() with
        {
            LyricStyle = LyricStyle.Default with { Stroke = Outline },
        };
        Assert.Equal(Outline, LyricStyleEffects.GetStroke(LyricStyleResolver.Resolve(project.LyricStyle).Style));
        LyricStyleOverride off = new() { Stroke = OptionalValue.Set<StrokeSpec?>(null) };
        LyricStyle resolved = LyricStyleResolver.Resolve(project.LyricStyle, tokenOverride: off).Style;
        Assert.Null(LyricStyleEffects.GetStroke(resolved));
        Assert.False(LyricStyleEffects.IsEnabled(resolved, LyricStyleEffectKind.Stroke));
        Assert.Equal(LyricStyleSource.Token,
            LyricStyleResolver.Resolve(project.LyricStyle, tokenOverride: off).SourceOf(LyricStyleProperty.UnsungStrokes));

        Project disabled = StyleBatchEditor.Apply(project, StyleTargetLevel.Token, [new StyleTarget(0, 0)],
            LyricStyle.Default, [LyricStyleProperty.UnsungStrokes]);
        Assert.True(disabled.Lyrics.Lines[0].Tokens[0].StyleOverride!.Stroke.IsSet);
        Project restored = StyleBatchEditor.Apply(disabled, StyleTargetLevel.Token, [new StyleTarget(0, 0)],
            LyricStyle.Default, [LyricStyleProperty.UnsungStrokes], restoreInheritance: true);
        Assert.Null(restored.Lyrics.Lines[0].Tokens[0].StyleOverride);
        Assert.Equal(Outline, LyricStyleEffects.GetStroke(LyricStyleResolver.Resolve(restored.LyricStyle).Style));
    }

    [Fact]
    public void 描边整表覆盖替换父描边且空表关闭()
    {
        LyricStyle style = LyricStyle.Default with { Stroke = Outline };
        StrokeSpec replacement = Outline with { Color = RgbaColor.Black, WidthPx = 2f };
        LyricStyle changed = LyricStyleResolver.Resolve(style,
            tokenOverride: new LyricStyleOverride { Stroke = replacement }).Style;
        Assert.Equal(replacement, LyricStyleEffects.GetStroke(changed));
        Assert.False(LyricStyleEffects.IsEnabled(LyricStyleResolver.Resolve(style,
            tokenOverride: new LyricStyleOverride { UnsungStrokes = StrokeLayers.Empty }).Style,
            LyricStyleEffectKind.Stroke));
    }

    [Fact]
    public void 非法描边覆盖和非法模板有明确诊断且不改变工程()
    {
        Project project = CreateProject();
        StrokeSpec invalid = Outline with { WidthPx = float.NaN };
        Project bad = LyricStyleEditor.SetTokenOverride(project, 0, 0,
            new LyricStyleOverride { Stroke = invalid });
        Assert.Contains(ProjectValidator.Validate(bad, ValidationContext.Open).Issues, issue =>
            issue.Code == DiagnosticCode.InvalidStyleValue && issue.Location!.EndsWith(".unsung.strokes", StringComparison.Ordinal));
        StyleTemplate valid = Template(StyleTemplateKind.Token, Outline);
        Assert.False(StyleTemplateApplicator.TryPreview(project, valid with
        {
            Style = valid.Style with { Stroke = invalid },
        }, null, null, true, out StyleTemplateApplyPreview preview, out string reason));
        Assert.Same(project, preview.Result);
        Assert.NotEmpty(reason);
    }

    [Fact]
    public void 模板库保存完整描边和显式关闭且读取不丢参数()
    {
        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.Path, "templates.json");
        StyleTemplate on = Template(StyleTemplateKind.Token, Outline);
        StyleTemplate off = Template(StyleTemplateKind.Line, null);
        StyleTemplateLibrary.Save(path, [on, off]);
        StyleTemplateLibraryLoadResult loaded = StyleTemplateLibrary.Load(path);
        Assert.Empty(loaded.Issues);
        Assert.Equal(Outline, loaded.Templates[0].Style.Stroke);
        Assert.Equal(LyricStyleProperty.UnsungStrokes, Assert.Single(loaded.Templates[0].Properties));
        Assert.Null(loaded.Templates[1].Style.Stroke);
    }

    private static Project CreateProject() => Project.Create("描边", lyrics: LyricsDocument.Create([
        LyricLine.Create("l1", "甲乙", tokens: [LyricToken.Create("a", "甲"), LyricToken.Create("b", "乙")]),
        LyricLine.Create("l2", "丙", tokens: [LyricToken.Create("c", "丙")]),
    ]));

    [Fact]
    public void 只有渐变的描边经模板保存套用和基线恢复仍启用且无伪造颜色()
    {
        var gradient = GradientSpec.Create([new(0, new(255, 0, 0, 80)), new(.4, new(0, 255, 0, 160)), new(1, new(0, 0, 255))],
            GradientKind.Radial, coordinateSpace: GradientCoordinateSpace.Line, radiusX: .7, radiusY: .4);
        var stroke = Outline with { Color = null, Gradient = gradient };
        Assert.True(stroke.IsEnabled);
        Assert.False((stroke with { WidthPx = 0 }).IsEnabled);
        Assert.True((stroke with { Gradient = null }).IsEnabled);
        Assert.Equal(new MaterialSpec.Solid(gradient.Stops[0].Color), (stroke with { Gradient = null }).Material);
        using TemporaryDirectory directory = new(); var path = Path.Combine(directory.Path, "templates.json");
        StyleTemplateLibrary.Save(path, [Template(StyleTemplateKind.Token, stroke)]);
        var loaded = StyleTemplateLibrary.Load(path); Assert.Empty(loaded.Issues);
        Assert.Equal(stroke, Assert.Single(loaded.Templates).Style.Stroke);
        var project = CreateProject();
        Assert.True(StyleTemplateApplicator.TryPreview(project, loaded.Templates[0], null, null, true, out var applied, out var reason), reason);
        Assert.All(applied.Result.Lyrics.Lines.SelectMany(line => line.Tokens), token =>
        {
            var resolved = LyricStyleResolver.Resolve(project.LyricStyle, tokenOverride: token.StyleOverride).Style;
            Assert.Equal(stroke, resolved.Stroke); Assert.True(resolved.Stroke!.IsEnabled); Assert.Equal(gradient.Stops[0].Color, resolved.Stroke.Color);
        });
        Assert.True(StyleTemplateApplicator.TryPreview(applied.Result, loaded.Templates[0], null, null, true, out var repeated, out reason), reason);
        Assert.Same(applied.Result, repeated.Result);
        ProjectStore.Save(applied.Result, directory.Path);
        var reopened = ProjectStore.Load(directory.Path);
        Assert.NotNull(reopened.Project);
        Assert.All(reopened.Project.Lyrics.Lines.SelectMany(line => line.Tokens), token =>
            Assert.Equal(stroke, LyricStyleResolver.Resolve(reopened.Project.LyricStyle, tokenOverride: token.StyleOverride).Style.Stroke));
    }

    private static StyleTemplate Template(StyleTemplateKind kind, StrokeSpec? stroke) =>
        StyleTemplate.Create("stroke-" + kind, "描边", kind,
            LyricStyle.Default with { Stroke = stroke }, [LyricStyleProperty.UnsungStrokes]);
}
