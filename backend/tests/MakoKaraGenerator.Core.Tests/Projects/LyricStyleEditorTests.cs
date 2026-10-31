using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;

namespace MakoKaraGenerator.Core.Tests.Projects;

/// <summary>工程样式编辑规则的测试（roadmap STYLE-02）。</summary>
public sealed class LyricStyleEditorTests
{
    [Fact]
    public void 只改一项时只写这一项的覆盖()
    {
        // 关键回归：把当前生效的整份样式都写成覆盖，等于让这一行脱离继承——
        // 以后改工程基线它就再也不跟着变了。
        Project project = BuildProject();

        Project edited = LyricStyleEditor.UpdateLineOverrideProperty(
            project,
            0,
            static overlay => overlay with { FontSizePx = 96f });

        LyricStyleOverride? lineOverride = edited.Lyrics.Lines[0].StyleOverride;

        Assert.NotNull(lineOverride);
        Assert.Equal(96f, lineOverride.FontSizePx);
        Assert.False(lineOverride.FontFamilyName.IsSet);
        Assert.Null(lineOverride.SungColor);
        Assert.Null(lineOverride.VerticalCenterRatio);
    }

    [Fact]
    public void 行覆盖之上改另一项不会丢掉已有覆盖()
    {
        Project project = LyricStyleEditor.UpdateLineOverrideProperty(
            BuildProject(),
            0,
            static overlay => overlay with { FontSizePx = 96f });

        Project edited = LyricStyleEditor.UpdateLineOverrideProperty(
            project,
            0,
            static overlay => overlay with { SungColor = RgbaColor.Parse("#FF0000") });

        LyricStyleOverride? lineOverride = edited.Lyrics.Lines[0].StyleOverride;

        Assert.NotNull(lineOverride);
        Assert.Equal(96f, lineOverride.FontSizePx);
        Assert.Equal(RgbaColor.Parse("#FF0000"), lineOverride.SungColor);
    }

    [Fact]
    public void 清除覆盖后恢复完全继承()
    {
        Project project = LyricStyleEditor.UpdateLineOverrideProperty(
            BuildProject(),
            0,
            static overlay => overlay with { FontSizePx = 96f });

        Project cleared = LyricStyleEditor.ClearLineOverride(project, 0);

        Assert.Null(cleared.Lyrics.Lines[0].StyleOverride);
    }

    [Fact]
    public void 空覆盖会被收敛成没有覆盖()
    {
        Project project = LyricStyleEditor.SetLineOverride(BuildProject(), 0, LyricStyleOverride.Empty);

        // 文件里不该留下一个空对象。
        Assert.Null(project.Lyrics.Lines[0].StyleOverride);
    }

    [Fact]
    public void 手工改基线会清掉预设标识()
    {
        Project project = LyricStyleEditor.ApplyPreset(BuildProject(), LyricStylePresetLibrary.Default.Presets[1]);

        Assert.Equal("karaoke-large", project.LyricStylePresetId);

        Project edited = LyricStyleEditor.SetBaseline(project, project.LyricStyle with { FontSizePx = 100f });

        Assert.Null(edited.LyricStylePresetId);
        Assert.Equal(100f, edited.LyricStyle.FontSizePx);
    }

    [Fact]
    public void 应用预设会写入样式本身而不是只记名字()
    {
        LyricStylePreset preset = LyricStylePresetLibrary.Default.Presets[2];

        Project project = LyricStyleEditor.ApplyPreset(BuildProject(), preset);

        Assert.Equal(preset.Style, project.LyricStyle);
        Assert.Equal(preset.Id, project.LyricStylePresetId);
    }

    [Fact]
    public void 应用与当前一致的预设不会换实例()
    {
        // 界面的"有没有未保存改动"按实例比较。启动时把预设下拉初始化成默认预设
        // 曾经就会顺手"应用"一次，结果工程实例被换掉，刚打开就显示成已修改。
        Project project = BuildProject() with
        {
            LyricStyle = LyricStylePresetLibrary.Default.Presets[0].Style,
            LyricStylePresetId = LyricStylePresetLibrary.Default.Presets[0].Id,
        };

        Project applied = LyricStyleEditor.ApplyPreset(project, LyricStylePresetLibrary.Default.Presets[0]);

        Assert.Same(project, applied);
    }

    [Fact]
    public void 设置与当前一致的基线且预设标识已空时不会换实例()
    {
        Project project = BuildProject();

        Assert.Same(project, LyricStyleEditor.SetBaseline(project, project.LyricStyle));
    }

    [Fact]
    public void 基线没变但预设标识还在时要清掉标识()
    {
        Project project = BuildProject() with { LyricStylePresetId = "karaoke-large" };

        Project edited = LyricStyleEditor.SetBaseline(project, project.LyricStyle);

        Assert.NotSame(project, edited);
        Assert.Null(edited.LyricStylePresetId);
    }

    [Fact]
    public void 行下标越界时原样返回()
    {
        Project project = BuildProject();

        Assert.Same(project, LyricStyleEditor.ClearLineOverride(project, 99));
        Assert.Same(project, LyricStyleEditor.UpdateLineOverride(project, -1, static _ => null));
    }

    [Fact]
    public void 编辑不会修改传入的工程()
    {
        Project project = BuildProject();

        _ = LyricStyleEditor.UpdateLineOverrideProperty(
            project,
            0,
            static overlay => overlay with { FontSizePx = 96f });

        Assert.Null(project.Lyrics.Lines[0].StyleOverride);
    }

    private static Project BuildProject() => Project.Create(
        "样式编辑",
        lyrics: LyricsDocument.Create(
        [
            LyricLine.Create(
                "l1",
                "夜",
                new TimeRange(0, 1_000_000),
                TimingStatus.Edited,
                [LyricToken.Create("t1", "夜", new TimeRange(0, 1_000_000), TimingStatus.Edited)]),
        ]));
}
