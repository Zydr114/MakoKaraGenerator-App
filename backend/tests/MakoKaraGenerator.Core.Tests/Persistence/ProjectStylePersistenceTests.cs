using MakoKaraGenerator.Core.Diagnostics;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using System.Text.Json.Nodes;

namespace MakoKaraGenerator.Core.Tests.Persistence;

/// <summary>
/// 歌词样式与样式覆盖的持久化测试（roadmap STYLE-01 / STYLE-02、CORE-06）。
/// </summary>
/// <remarks>
/// 重点在两件事：① 覆盖的"未设置 / 设置为空 / 设置为值"三态必须原样往返
/// （否则"恢复继承"会丢默认值）；② v1 老文件必须被迁移器认下来，而不是报版本不支持。
/// </remarks>
public sealed class ProjectStylePersistenceTests : IDisposable
{
    private readonly TemporaryDirectory _directory = new();

    /// <inheritdoc/>
    public void Dispose() => _directory.Dispose();

    [Fact]
    public void 注音布局样式与覆盖三态持久化往返()
    {
        var project = Project.Create("注音", lyricStyle: LyricStyle.Default with { Ruby = new() { RelativeScale = .65, GapPx = 4 } },
            lyrics: LyricsDocument.Create([LyricLine.Create("l", "甲乙丙", tokens:
            [
                LyricToken.Create("a", "甲"),
                LyricToken.Create("b", "乙", styleOverride: new() { Ruby = new() { RelativeScale = OptionalValue.Set<double?>(null) } }),
                LyricToken.Create("c", "丙", styleOverride: new() { Ruby = new() { RelativeScale = OptionalValue.Set<double?>(.8), GapPx = OptionalValue.Set<double?>(6) } }),
            ])]));
        Project loaded = RoundTrip(project);
        Assert.Equal(.65, loaded.LyricStyle.Ruby.RelativeScale);
        Assert.Equal(4, loaded.LyricStyle.Ruby.GapPx);
        Assert.Null(loaded.Lyrics.Lines[0].Tokens[0].StyleOverride);
        var empty = loaded.Lyrics.Lines[0].Tokens[1].StyleOverride!.Ruby!;
        Assert.True(empty.RelativeScale.IsSet); Assert.Null(empty.RelativeScale.Value); Assert.False(empty.GapPx.IsSet);
        var explicitValues = loaded.Lyrics.Lines[0].Tokens[2].StyleOverride!.Ruby!;
        Assert.Equal(.8, explicitValues.RelativeScale.Value); Assert.Equal(6, explicitValues.GapPx.Value);
        Assert.Contains("\"relativeScale\": null", ProjectFileFixture.ReadLyrics(_directory.Path), StringComparison.Ordinal);
    }

    [Fact]
    public void 样式基线与预设标识往返无损()
    {
        LyricStyle style = LyricStyle.Create(
            64f,
            fontFamilyName: "Noto Sans CJK SC",
            unsungColor: RgbaColor.Parse("#445566"),
            sungColor: RgbaColor.Parse("#AABBCC"),
            verticalCenterRatio: 0.42d);
        style = style with
        {
            UnsungFill = style.UnsungFill with
            {
                Gradient = GradientSpec.Create([
                    new GradientStop(0d, RgbaColor.Parse("#112233")),
                    new GradientStop(1d, RgbaColor.Parse("#44556680")),
                ]),
            },
            SungFill = style.SungFill with
            {
                Gradient = GradientSpec.Create([
                    new GradientStop(0d, RgbaColor.Parse("#778899")),
                    new GradientStop(1d, RgbaColor.Parse("#AABBCC")),
                ]),
            },
            Stroke = StrokeSpec.Create(RgbaColor.Parse("#FFFFFF"), 3f),
            Shadow = new ShadowSpec
            {
                Color = RgbaColor.Parse("#00000080"),
                AngleDegrees = Math.Atan2(5d, 4d) * 180d / Math.PI,
                DistancePx = (float)Math.Sqrt(41d),
                BlurRadiusPx = 6f,
            },
            Opacity = 0.8f,
            Scale = 1.25f,
        };

        Project original = Project.Create(
            "样式往返",
            Composition.Create(1280, 720, FrameRate.Fps30),
            LyricsDocument.Create([LyricLine.Create("l1", "夜", null)]),
            lyricStyle: style,
            lyricStylePresetId: "karaoke-warm");

        Project reloaded = RoundTrip(original);

        Assert.Equal(style, reloaded.LyricStyle);
        Assert.Equal("karaoke-warm", reloaded.LyricStylePresetId);
    }

    [Fact]
    public void 完整描边按行与元素保存且缺失空值和对象三态往返无损()
    {
        StrokeSpec stroke = StrokeSpec.Create(new RgbaColor(255, 80, 20, 150), 6.25f, StrokeJoin.Miter);
        Project project = Project.Create("描边三态", lyrics: LyricsDocument.Create([
            LyricLine.Create("l1", "甲乙丙", tokens:
            [
                LyricToken.Create("a", "甲"),
                LyricToken.Create("b", "乙", styleOverride: new LyricStyleOverride
                { Stroke = OptionalValue.Set<StrokeSpec?>(null) }),
                LyricToken.Create("c", "丙", styleOverride: new LyricStyleOverride { Stroke = stroke }),
            ], styleOverride: new LyricStyleOverride { Stroke = stroke }),
        ]));
        Project reloaded = RoundTrip(project);
        Assert.Equal(stroke, reloaded.Lyrics.Lines[0].StyleOverride!.Stroke.Value);
        Assert.Null(reloaded.Lyrics.Lines[0].Tokens[0].StyleOverride);
        Assert.True(reloaded.Lyrics.Lines[0].Tokens[1].StyleOverride!.Stroke.IsSet);
        Assert.Null(reloaded.Lyrics.Lines[0].Tokens[1].StyleOverride!.Stroke.Value);
        Assert.Equal(stroke, reloaded.Lyrics.Lines[0].Tokens[2].StyleOverride!.Stroke.Value);
        string json = ProjectFileFixture.ReadLyrics(_directory.Path);
        Assert.Contains("\"strokes\": []", json, StringComparison.Ordinal);
        Assert.Contains("\"widthPx\": 6.25", json, StringComparison.Ordinal);

        JsonObject root = JsonNode.Parse(json)!.AsObject();
        root["branches"]![0]!["lines"]![0]!["tokens"]![2]!["styleOverride"]!["unsung"]!["strokes"]![0]!["join"] = "Unknown";
        ProjectFileFixture.PublishLyrics(_directory.Path, root.ToJsonString());
        ProjectLoadResult malformed = ProjectStore.Load(_directory.Path);
        Assert.True(malformed.Report.HasErrors);
        Assert.Contains(malformed.Report.Issues, issue => issue.Code == DiagnosticCode.InvalidStyleValue
            && issue.Location!.EndsWith(".unsung.strokes.0.join", StringComparison.Ordinal));
    }

    [Fact]
    public void 完整渐变描述往返无损()
    {
        GradientSpec gradient = GradientSpec.Create(
            [
                new GradientStop(0d, RgbaColor.Parse("#1020FF")),
                new GradientStop(0.35d, RgbaColor.Parse("#20FF80")),
                new GradientStop(1d, RgbaColor.Parse("#FF2040")),
            ],
            angleDegrees: 135d,
            kind: GradientKind.Radial,
            coordinateSpace: GradientCoordinateSpace.Canvas,
            colorSpace: GradientColorSpace.Hsv,
            huePath: GradientHuePath.Longer,
            centerX: 0.3d,
            centerY: 0.6d,
            radiusX: 0.4d,
            radiusY: 0.25d);
        LyricStyle style = new()
        {
            FontSizePx = 64f,
            FallbackFamilyNames = ["Noto Color Emoji"],
            UnsungGradientSpec = gradient,
            SungGradientSpec = gradient with { AngleDegrees = 270d },
        };

        Project reloaded = RoundTrip(Project.Create("完整渐变", lyricStyle: style));

        Assert.Equal(style, reloaded.LyricStyle);
    }

    [Fact]
    public void 完整渐变可按行和元素应用并在保存后保留三态()
    {
        GradientSpec gradient = GradientSpec.Create(
            [new GradientStop(0d, RgbaColor.Parse("#FF0000")),
                new GradientStop(0.5d, RgbaColor.Parse("#00FF00")),
                new GradientStop(1d, RgbaColor.Parse("#0000FF"))], angleDegrees: 75d);
        LyricToken token = LyricToken.Create("t1", "夜");
        Project project = Project.Create("渐变覆盖", lyrics: LyricsDocument.Create([
            LyricLine.Create("l1", "夜", tokens: [token])]));
        project = StyleBatchEditor.Apply(project, StyleTargetLevel.Line, [new StyleTarget(0)],
            LyricStyle.Default with { UnsungGradientSpec = gradient },
            [LyricStyleProperty.UnsungFill]);
        project = StyleBatchEditor.Apply(project, StyleTargetLevel.Token, [new StyleTarget(0, 0)],
            LyricStyle.Default, [LyricStyleProperty.UnsungFill]);

        Project reloaded = RoundTrip(project);

        Assert.Equal(gradient, reloaded.Lyrics.Lines[0].StyleOverride!.UnsungGradientSpec.Value);
        Assert.True(reloaded.Lyrics.Lines[0].Tokens[0].StyleOverride!.UnsungGradientSpec.IsSet);
        Assert.Null(reloaded.Lyrics.Lines[0].Tokens[0].StyleOverride!.UnsungGradientSpec.Value);
        Assert.Null(LyricStyleResolver.Resolve(reloaded.LyricStyle,
            lineOverride: reloaded.Lyrics.Lines[0].StyleOverride,
            tokenOverride: reloaded.Lyrics.Lines[0].Tokens[0].StyleOverride).Style.UnsungGradientSpec);
        string lyricsJson = ProjectFileFixture.ReadLyrics(_directory.Path);
        Assert.Contains("\"unsung\"", lyricsJson, StringComparison.Ordinal);
        Assert.Contains("\"gradient\"", lyricsJson, StringComparison.Ordinal);
        Assert.Contains("\"angleDegrees\": 75", lyricsJson, StringComparison.Ordinal);
    }

    [Fact]
    public void 新版描边与方向阴影描述往返无损()
    {
        LyricStyle style = LyricStyle.Default with
        {
            Stroke = StrokeSpec.Create(new RgbaColor(255, 200, 80, 220), 7f, StrokeJoin.Bevel),
            Shadow = ShadowSpec.Create(
                new RgbaColor(4, 8, 16, 200),
                opacity: 0.65f,
                angleDegrees: 135d,
                distancePx: 11f,
                blurRadiusPx: 9f),
        };

        Project reloaded = RoundTrip(Project.Create("新版装饰", lyricStyle: style));

        Assert.Equal(style, reloaded.LyricStyle);
        string projectJson = File.ReadAllText(Path.Combine(_directory.Path, ProjectJson.ProjectFileName));
        Assert.Contains("\"strokes\"", projectJson, StringComparison.Ordinal);
        Assert.Contains("\"shadow\"", projectJson, StringComparison.Ordinal);
    }

    [Fact]
    public void 字体参数与行元素方向阴影覆盖跨两份文件往返()
    {
        ShadowSpec shadow = ShadowSpec.Create(new RgbaColor(20, 40, 60, 180),
            opacity: 0.7f, angleDegrees: 120d, distancePx: 14f, blurRadiusPx: 7f);
        LyricToken token = LyricToken.Create("t1", "夜", styleOverride: new LyricStyleOverride
        {
            ShadowSpec = OptionalValue.Set<ShadowSpec?>(null),
        });
        LyricLine line = LyricLine.Create("l1", "夜", tokens: [token], styleOverride: new LyricStyleOverride
        {
            FontWeight = 300,
            IsBold = false,
            IsItalic = false,
            LetterSpacingPx = -2f,
            ShadowSpec = shadow,
        });
        LyricStyle baseline = LyricStyle.Default with
        {
            FontWeight = 700,
            IsBold = true,
            IsItalic = true,
            LetterSpacingPx = 6f,
        };
        Project reloaded = RoundTrip(Project.Create("字体与阴影", lyrics: LyricsDocument.Create([line]),
            lyricStyle: baseline));

        Assert.Equal(baseline, reloaded.LyricStyle);
        LyricStyleOverride row = Assert.IsType<LyricStyleOverride>(reloaded.Lyrics.Lines[0].StyleOverride);
        Assert.Equal(300, row.FontWeight);
        Assert.False(row.IsBold);
        Assert.False(row.IsItalic);
        Assert.Equal(-2f, row.LetterSpacingPx);
        Assert.Equal(shadow, row.ShadowSpec.Value);
        LyricStyleOverride chip = Assert.IsType<LyricStyleOverride>(reloaded.Lyrics.Lines[0].Tokens[0].StyleOverride);
        Assert.True(chip.ShadowSpec.IsSet);
        Assert.Null(chip.ShadowSpec.Value);
        Assert.Equal(300, LyricStyleResolver.Resolve(reloaded.LyricStyle, lineOverride: row).Style.FontWeight);
        Assert.Null(LyricStyleResolver.Resolve(reloaded.LyricStyle, lineOverride: row,
            tokenOverride: chip).Style.Shadow);
    }

    [Fact]
    public void 上一版schema工程被拒绝而不解释旧字体()
    {
        ProjectStore.Save(Project.Create("旧字体",
            lyrics: LyricsDocument.Create([LyricLine.Create("l1", "夜")])), _directory.Path);
        string projectPath = Path.Combine(_directory.Path, ProjectJson.ProjectFileName);
        string lyricsPath = ProjectFileFixture.LyricsPath(_directory.Path);
        JsonObject project = JsonNode.Parse(File.ReadAllText(projectPath))!.AsObject();
        project["schemaVersion"] = Project.CurrentSchemaVersion - 1;
        JsonObject style = project["lyricStyle"]!.AsObject();
        foreach (string key in new[] { "fontWeight", "isBold", "isItalic", "letterSpacingPx" })
            style.Remove(key);
        File.WriteAllText(projectPath, project.ToJsonString());
        JsonObject lyrics = JsonNode.Parse(File.ReadAllText(lyricsPath))!.AsObject();
        lyrics["schemaVersion"] = LyricsDocument.CurrentSchemaVersion - 1;
        ProjectFileFixture.PublishLyrics(_directory.Path, lyrics.ToJsonString());

        ProjectLoadResult result = ProjectStore.Load(_directory.Path);

        Assert.Null(result.Project);
        Assert.True(result.Report.Contains(DiagnosticCode.UnsupportedSchemaVersion));
    }

    [Fact]
    public void 字体族为空的基线样式往返无损()
    {
        // null 字体族 = 用渲染后端默认字体，是合法取值，不能被写成别的东西。
        Project original = Project.Create(
            "默认字体",
            lyricStyle: new LyricStyle { FontFamilyName = null, FontSizePx = 48f });

        Project reloaded = RoundTrip(original);

        Assert.Null(reloaded.LyricStyle.FontFamilyName);
        Assert.Equal(48f, reloaded.LyricStyle.FontSizePx);
    }

    [Fact]
    public void 行级覆盖的三态往返无损()
    {
        // 关键回归：`FontFamilyName = null` 表示"覆盖成默认字体"，而不是"没有覆盖"。
        LyricStyleOverride lineOverride = new()
        {
            FontFamilyName = null,
            FontSizePx = 60f,
            SungColor = RgbaColor.Parse("#FF8800"),
        };

        Project original = Project.Create(
            "行覆盖",
            lyrics: LyricsDocument.Create([LyricLine.Create("l1", "夜", null, TimingStatus.Missing, null, lineOverride)]));

        Project reloaded = RoundTrip(original);
        LyricStyleOverride? reloadedOverride = reloaded.Lyrics.Lines[0].StyleOverride;

        Assert.NotNull(reloadedOverride);
        Assert.True(reloadedOverride.FontFamilyName.IsSet);
        Assert.Null(reloadedOverride.FontFamilyName.Value);
        Assert.Equal(60f, reloadedOverride.FontSizePx);
        Assert.Equal(RgbaColor.Parse("#FF8800"), reloadedOverride.SungColor);

        // 未覆盖的属性必须仍然是"未设置"，否则继承会断掉。
        Assert.Null(reloadedOverride.VerticalCenterRatio);
    }

    [Fact]
    public void 未覆盖的字体族往返后仍然是未设置()
    {
        LyricStyleOverride lineOverride = new() { FontSizePx = 44f };

        Project original = Project.Create(
            "只改字号",
            lyrics: LyricsDocument.Create([LyricLine.Create("l1", "夜", null, TimingStatus.Missing, null, lineOverride)]));

        LyricStyleOverride? reloaded = RoundTrip(original).Lyrics.Lines[0].StyleOverride;

        Assert.NotNull(reloaded);
        Assert.False(reloaded.FontFamilyName.IsSet);
        Assert.Equal(44f, reloaded.FontSizePx);
    }

    [Fact]
    public void 元素级颜色覆盖往返无损()
    {
        LyricToken token = LyricToken.Create(
            "t1",
            "夜",
            new TimeRange(0, 500_000),
            TimingStatus.Edited,
            new LyricStyleOverride { SungColor = RgbaColor.Parse("#00FF00") });

        Project original = Project.Create(
            "元素覆盖",
            lyrics: LyricsDocument.Create(
            [
                LyricLine.Create(
                    "l1",
                    "夜",
                    new TimeRange(0, 500_000),
                    TimingStatus.Edited,
                    [token]),
            ]));

        LyricStyleOverride? reloaded = RoundTrip(original).Lyrics.Lines[0].Tokens[0].StyleOverride;

        Assert.NotNull(reloaded);
        Assert.Equal(RgbaColor.Parse("#00FF00"), reloaded.SungColor);
        Assert.False(reloaded.FontFamilyName.IsSet);
    }

    [Fact]
    public void 空覆盖不会被写进文件()
    {
        Project original = Project.Create(
            "空覆盖",
            lyrics: LyricsDocument.Create([LyricLine.Create("l1", "夜", null, TimingStatus.Missing, null, LyricStyleOverride.Empty)]));

        ProjectStore.Save(original, _directory.Path);
        string json = ProjectFileFixture.ReadLyrics(_directory.Path);

        Assert.DoesNotContain("styleOverride", json, StringComparison.Ordinal);
        Assert.DoesNotContain("clearFontFamilyName", json, StringComparison.Ordinal);
    }

    [Fact]
    public void 覆盖成默认字体在文件里写成显式清除()
    {
        Project original = Project.Create(
            "清除字体",
            lyrics: LyricsDocument.Create(
            [
                LyricLine.Create(
                    "l1",
                    "夜",
                    null,
                    TimingStatus.Missing,
                    null,
                    new LyricStyleOverride { FontFamilyName = null }),
            ]));

        ProjectStore.Save(original, _directory.Path);
        string json = ProjectFileFixture.ReadLyrics(_directory.Path);

        // "键缺失"表示不覆盖，因此"覆盖成 null"必须有一个显式表示。
        Assert.Contains("\"typography\"", json, StringComparison.Ordinal);
        Assert.Contains("\"fontFamilyName\": null", json, StringComparison.Ordinal);
    }

    [Fact]
    public void 旧版本项目没有程序版本时被拒绝()
    {
        WriteVersionOneProject();

        ProjectLoadResult result = ProjectStore.Load(_directory.Path);

        Assert.Null(result.Project);
        Assert.True(result.Report.Contains(DiagnosticCode.ApplicationVersionMismatch));
    }

    [Fact]
    public void 迁移不改写磁盘上的原文件()
    {
        WriteVersionOneProject();
        string before = File.ReadAllText(Path.Combine(_directory.Path, ProjectJson.ProjectFileName));

        _ = ProjectStore.Load(_directory.Path);

        Assert.Equal(before, File.ReadAllText(Path.Combine(_directory.Path, ProjectJson.ProjectFileName)));
    }

    [Fact]
    public void 更高版本的文件被拒绝并说明原因()
    {
        ProjectStore.Save(Project.Create("测试"), _directory.Path);
        string path = Path.Combine(_directory.Path, ProjectJson.ProjectFileName);
        File.WriteAllText(path, File.ReadAllText(path).Replace($"\"schemaVersion\": {Project.CurrentSchemaVersion}", "\"schemaVersion\": 99", StringComparison.Ordinal));

        ProjectLoadResult result = ProjectStore.Load(_directory.Path);

        Assert.Null(result.Project);
        Assert.True(result.Report.HasErrors);
        Assert.Contains(
            result.Report.Issues,
            static issue => issue.Message.Contains("更新的版本", StringComparison.Ordinal));
    }

    [Fact]
    public void 不受支持的schema被报成可读错误()
    {
        // v0 不存在于任何迁移链里：必须报"读不了"，而不是当成功再抛异常。
        ProjectStore.Save(Project.Create("测试"), _directory.Path);
        string path = Path.Combine(_directory.Path, ProjectJson.ProjectFileName);
        File.WriteAllText(path, File.ReadAllText(path).Replace($"\"schemaVersion\": {Project.CurrentSchemaVersion}", "\"schemaVersion\": 0", StringComparison.Ordinal));

        ProjectLoadResult result = ProjectStore.Load(_directory.Path);

        Assert.Null(result.Project);
        Assert.Contains(
            result.Report.Issues,
            static issue => issue.Code == DiagnosticCode.UnsupportedSchemaVersion);
    }

    [Fact]
    public void 非法样式值被报错并退回默认值()
    {
        ProjectStore.Save(Project.Create("测试"), _directory.Path);
        string path = Path.Combine(_directory.Path, ProjectJson.ProjectFileName);
        File.WriteAllText(
            path,
            File.ReadAllText(path).Replace("\"fontSizePx\": 72", "\"fontSizePx\": 0", StringComparison.Ordinal));

        ProjectLoadResult result = ProjectStore.Load(_directory.Path);

        Assert.NotNull(result.Project);
        Assert.Equal(LyricStyle.Default.FontSizePx, result.Project.LyricStyle.FontSizePx);
        Assert.Contains(
            result.Report.Issues,
            static issue => issue.Code == DiagnosticCode.InvalidStyleValue);
    }

    [Fact]
    public void 显式lyricStyle缺少typography时记录诊断并回落默认排版()
    {
        ProjectStore.Save(Project.Create("缺排版"), _directory.Path);
        string path = Path.Combine(_directory.Path, ProjectJson.ProjectFileName);
        JsonObject root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        root["lyricStyle"]!.AsObject().Remove("typography");
        File.WriteAllText(path, root.ToJsonString(ProjectJson.Options));

        ProjectLoadResult result = ProjectStore.Load(_directory.Path);

        Assert.NotNull(result.Project);
        Assert.Equal(LyricStyle.Default.Typography, result.Project.LyricStyle.Typography);
        Assert.Contains(result.Report.Issues, static issue =>
            issue.Code == DiagnosticCode.InvalidStyleValue
            && issue.Severity == IssueSeverity.Error
            && issue.Location == "lyricStyle.typography");
    }

    [Fact]
    public void 展示节点缺少placement或槽位动画缺少变换时记录诊断并回落非空默认对象()
    {
        ProjectStore.Save(Project.Create("缺展示字段"), _directory.Path);
        string path = Path.Combine(_directory.Path, ProjectJson.ProjectFileName);
        JsonObject root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        root["lyricPresentation"] = new JsonObject
        {
            ["nodes"] = new JsonArray
            {
                new JsonObject
                {
                    ["id"] = "container/slot-test",
                    ["referenceWidthRatio"] = 1.0,
                    ["referenceHeightRatio"] = 1.0,
                    ["layout"] = new JsonObject
                    {
                        ["kind"] = "SlotSequence",
                        ["slotIds"] = new JsonArray(),
                        ["slotAnimation"] = new JsonObject
                        {
                            ["revealMode"] = "Progress",
                            ["revealProgress"] = 0.5,
                            ["maxVisibleLines"] = 2,
                            ["conflictPolicy"] = "QueueUntilSlotFree",
                            ["retentionPolicy"] = "UntilLineEnd",
                            ["enterDurationUs"] = 300_000,
                            ["exitDurationUs"] = 250_000,
                            ["easing"] = "EaseOut",
                            ["exitEasing"] = "EaseIn",
                        },
                    },
                },
            },
            ["programs"] = new JsonArray(),
            ["lineOverrides"] = new JsonArray(),
        };
        File.WriteAllText(path, root.ToJsonString(ProjectJson.Options));

        ProjectLoadResult result = ProjectStore.Load(_directory.Path);

        Assert.NotNull(result.Project);
        SceneContainerDefinition node = Assert.Single(result.Project.LyricPresentation.Nodes);
        Assert.NotNull(node.Placement);
        Assert.Equal(ScenePlacement.Default, node.Placement);
        SlotAnimationSpec animation = Assert.IsType<SlotAnimationSpec>(node.Layout!.SlotAnimation);
        Assert.NotNull(animation.EnterFrom);
        Assert.NotNull(animation.Hold);
        Assert.NotNull(animation.ExitTo);
        Assert.Contains(result.Report.Issues, static issue =>
            issue.Code == DiagnosticCode.InvalidLinePlacement
            && issue.Location == "project.lyricPresentation.nodes[0].placement");
        Assert.Contains(result.Report.Issues, static issue =>
            issue.Code == DiagnosticCode.InvalidDisplayProgram
            && issue.Location == "project.lyricPresentation.nodes[0].layout.slotAnimation.enterFrom");
        Assert.Contains(result.Report.Issues, static issue =>
            issue.Code == DiagnosticCode.InvalidDisplayProgram
            && issue.Location == "project.lyricPresentation.nodes[0].layout.slotAnimation.hold");
        Assert.Contains(result.Report.Issues, static issue =>
            issue.Code == DiagnosticCode.InvalidDisplayProgram
            && issue.Location == "project.lyricPresentation.nodes[0].layout.slotAnimation.exitTo");
    }

    private Project RoundTrip(Project project)
    {
        ProjectStore.Save(project, _directory.Path);

        ProjectLoadResult result = ProjectStore.Load(_directory.Path);

        Assert.NotNull(result.Project);
        Assert.False(result.Report.HasErrors, result.Report.ToSummary());

        return result.Project;
    }

    /// <summary>写一份 schema v1 的项目（没有样式字段，带注释与尾随逗号，验证手工编辑容忍度仍在）。</summary>
    private void WriteVersionOneProject()
    {
        File.WriteAllText(
            Path.Combine(_directory.Path, ProjectJson.ProjectFileName),
            """
            {
              // v1 文件：没有样式字段
              "schemaVersion": 1,
              "name": "老项目",
              "composition": {
                "canvasWidth": 1920,
                "canvasHeight": 1080,
                "frameRate": { "numerator": 30, "denominator": 1 },
                "backgroundColor": "#000000",
                "outputDurationUs": 5000000,
              },
            }
            """);

        ProjectFileFixture.PublishLyrics(
            _directory.Path,
            """
            {
              "schemaVersion": 1,
              "language": "zh-CN",
              "lines": [
                {
                  "id": "l1",
                  "text": "夜空中最亮的星",
                  "startUs": 1000000,
                  "endUs": 4000000,
                  "timingState": "Imported",
                },
              ],
            }
            """);
    }
}
