using MakoKaraGenerator.Core.Diagnostics;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Core.Validation;

namespace MakoKaraGenerator.Core.Tests.Projects;

/// <summary>水平对齐方式的模型、持久化与校验测试（P0「对齐方式和基本样式」）。</summary>
public sealed class HorizontalAlignmentTests : IDisposable
{
    private readonly TemporaryDirectory _directory = new();

    /// <inheritdoc/>
    public void Dispose() => _directory.Dispose();

    [Fact]
    public void 默认居中()
    {
        // 加入对齐字段之前的行为就是居中：默认值必须与之一致，否则老工程打开会"变样"。
        Assert.Equal(LyricHorizontalAlignment.Center, new LyricStyle().HorizontalAlignment);
        Assert.Equal(LyricHorizontalAlignment.Center, LyricStyle.Default.HorizontalAlignment);
    }

    [Fact]
    public void 存盘读回一致()
    {
        Project project = BuildProject() with
        {
            LyricStyle = new LyricStyle { HorizontalAlignment = LyricHorizontalAlignment.Right },
        };

        ProjectStore.Save(project, _directory.Path);

        ProjectLoadResult result = ProjectStore.Load(_directory.Path);

        Assert.NotNull(result.Project);
        Assert.Equal(LyricHorizontalAlignment.Right, result.Project.LyricStyle.HorizontalAlignment);
    }

    [Fact]
    public void 行覆盖的元素级设置会被校验器报出来()
    {
        // 对齐属于整行排版：元素级覆盖必须被拒绝（ADR-0032 的契约）。
        Project project = BuildProject() with
        {
            Lyrics = LyricsDocument.Create(
            [
                LyricLine.Create(
                    "l1",
                    "夜空",
                    new TimeRange(0, 1_000_000),
                    TimingStatus.Edited,
                    [
                        LyricToken.Create("t1", "夜空", new TimeRange(0, 1_000_000), TimingStatus.Edited) with
                        {
                            StyleOverride = new LyricStyleOverride
                            {
                                HorizontalAlignment = LyricHorizontalAlignment.Left,
                            },
                        },
                    ]),
            ]),
        };

        ValidationReport report = ProjectValidator.Validate(project, ValidationContext.Open);

        Assert.Contains(
            report.Issues,
            issue => issue.Code == DiagnosticCode.UnsupportedTokenStyleOverride);
    }

    [Fact]
    public void 覆盖类型能识别对齐这一项()
    {
        Assert.True(new LyricStyleOverride { HorizontalAlignment = LyricHorizontalAlignment.Left }.IsEmpty == false);
        Assert.True(new LyricStyleOverride().IsEmpty);
    }

    [Fact]
    public void 文件里写着不认识的对齐方式时回退居中并报错()
    {
        ProjectStore.Save(
            BuildProject() with
            {
                LyricStyle = new LyricStyle { HorizontalAlignment = LyricHorizontalAlignment.Right },
            },
            _directory.Path);

        string path = Path.Combine(_directory.Path, ProjectJson.ProjectFileName);
        string json = File.ReadAllText(path);

        Assert.Contains("Right", json, StringComparison.Ordinal);

        File.WriteAllText(path, json.Replace("\"Right\"", "\"Sideways\"", StringComparison.Ordinal));

        ProjectLoadResult result = ProjectStore.Load(_directory.Path);

        Assert.NotNull(result.Project);
        Assert.Equal(LyricHorizontalAlignment.Center, result.Project.LyricStyle.HorizontalAlignment);
        Assert.Contains(result.Report.Issues, issue => issue.Code == DiagnosticCode.InvalidStyleValue);
    }

    [Fact]
    public void 老文件按居中解释()
    {
        // v4 文件没有这个字段：迁移不改写数据，读回来就是默认的居中。
        ProjectStore.Save(BuildProject(), _directory.Path);

        string path = Path.Combine(_directory.Path, ProjectJson.ProjectFileName);
        string json = File.ReadAllText(path);

        File.WriteAllText(
            path,
            json.Replace("\"schemaVersion\": 5", "\"schemaVersion\": 4", StringComparison.Ordinal)
                .Replace("\"horizontalAlignment\": \"Center\",", string.Empty, StringComparison.Ordinal));

        ProjectLoadResult result = ProjectStore.Load(_directory.Path);

        Assert.NotNull(result.Project);
        Assert.Equal(LyricHorizontalAlignment.Center, result.Project.LyricStyle.HorizontalAlignment);
    }

    private static Project BuildProject() => Project.Create(
        "对齐",
        Composition.Create(1920, 1080, FrameRate.Fps30),
        LyricsDocument.Create(
        [
            LyricLine.Create("l1", "夜空", new TimeRange(0, 1_000_000), TimingStatus.Edited),
        ]));
}
