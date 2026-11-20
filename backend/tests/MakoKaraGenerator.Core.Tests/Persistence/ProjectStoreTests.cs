using MakoKaraGenerator.Core.Diagnostics;
using System.Text.Json.Nodes;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;

namespace MakoKaraGenerator.Core.Tests.Persistence;

/// <summary>
/// 项目目录的往返测试，对应阶段 1 的出口判据：
/// "用测试直接创建一份项目，写入一行歌词和三个 token，保存、加载、校验后数据不变"
/// （docs/roadmap.md §6）。
/// </summary>
public sealed class ProjectStoreTests : IDisposable
{
    private readonly TemporaryDirectory _directory = new();

    public void Dispose() => _directory.Dispose();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void 打开只移除无对应来源的未配置默认主歌词程序(bool customized)
    {
        Project template = SampleProject();
        LyricsDocument lyrics = template.Lyrics with
        {
            PrimaryBranchId = "agent-v1",
            Branches = [template.Lyrics.PrimaryBranch! with { Id = "agent-v1" }],
        };
        var presentation = template.LyricPresentation.WithDefaultPrograms(lyrics);
        if (customized)
        {
            var program = presentation.FindProgram("primary")! with { AnimationPolicy = LyricBranchAnimationPolicy.Static };
            presentation = presentation with { Programs = presentation.Programs.SetItem(0, program) };
        }
        var legacy = template with { Lyrics = lyrics, LyricPresentation = presentation };
        ProjectStore.Save(legacy, _directory.Path);
        string entry = Path.Combine(_directory.Path, "project.json");
        byte[] original = File.ReadAllBytes(entry);

        ProjectLoadResult loaded = ProjectStore.Load(_directory.Path);

        Assert.Equal(original, File.ReadAllBytes(entry));
        Assert.Equal(lyrics, loaded.Project!.Lyrics);
        Assert.Equal(presentation.Nodes, loaded.Project.LyricPresentation.Nodes.AsEnumerable());
        Assert.Equal(presentation.FindProgram("agent-v1"), loaded.Project.LyricPresentation.FindProgram("agent-v1"));
        Assert.Equal(customized, loaded.Report.HasErrors);
        Assert.Equal(customized, loaded.Project.LyricPresentation.FindProgram("primary") is not null);
    }

    [Fact]
    public void 保存加载校验之后数据不变()
    {
        Project original = SampleProject();

        ProjectStore.Save(original, _directory.Path);
        ProjectLoadResult result = ProjectStore.Load(_directory.Path);

        Assert.True(result.Loaded);
        Assert.Equal(original, result.Project);
        Assert.False(result.Report.HasErrors, result.Report.ToSummary());
        Assert.True(result.CanExport);
    }

    [Fact]
    public void 往返后没有非提示级诊断()
    {
        ProjectStore.Save(SampleProject(), _directory.Path);

        ProjectLoadResult result = ProjectStore.Load(_directory.Path);

        Assert.DoesNotContain(result.Report.Issues, static issue => issue.Severity != IssueSeverity.Info);
    }

    [Fact]
    public void 样本项目包含一行歌词与三个元素()
    {
        ProjectStore.Save(SampleProject(), _directory.Path);

        ProjectLoadResult result = ProjectStore.Load(_directory.Path);

        LyricLine line = Assert.Single(result.Project!.Lyrics.Lines);
        string[] expectedTokenTexts = ["夜", "空中", "最亮"];

        Assert.Equal("夜空中最亮", line.Text);
        Assert.Equal(expectedTokenTexts, line.Tokens.Select(static token => token.Text));
        Assert.Equal("夜空中最亮", string.Concat(line.Tokens.Select(static token => token.Text)));
    }

    [Fact]
    public void 清除中间元素时间后往返仍保留未定时空位()
    {
        Assert.True(LyricTimingEditor.TryClearTokenTime(SampleProject(), "line-001", "token-002",
            out Project edited, out string reason), reason);

        ProjectStore.Save(edited, _directory.Path);
        ProjectLoadResult result = ProjectStore.Load(_directory.Path);

        Assert.True(result.Loaded, result.Report.ToSummary());
        LyricLine line = Assert.Single(result.Project!.Lyrics.Lines);
        Assert.Equal(new TimeRange(1_200_000, 3_400_000), line.Time);
        Assert.Equal(new TimeRange(1_200_000, 1_700_000), line.Tokens[0].Time);
        Assert.Null(line.Tokens[1].Time);
        Assert.Equal(TimingStatus.Missing, line.Tokens[1].TimingStatus);
        Assert.Equal(new TimeRange(2_300_000, 3_400_000), line.Tokens[2].Time);
    }

    [Fact]
    public void 保存生成项目文件与歌词文件()
    {
        ProjectStore.Save(SampleProject(), _directory.Path);

        Assert.True(File.Exists(_directory.FilePath(ProjectJson.ProjectFileName)));
        Assert.True(File.Exists(ProjectFileFixture.LyricsPath(_directory.Path)));
    }

    [Fact]
    public void 新工程写入当前程序版本并可重开()
    {
        ProjectStore.Save(SampleProject(), _directory.Path);
        JsonNode document = JsonNode.Parse(_directory.ReadFile(ProjectJson.ProjectFileName))!;

        Assert.Equal(ProductVersion.Current, document["applicationVersion"]!.GetValue<string>());
        Assert.True(ProjectStore.Load(_directory.Path).Loaded);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("0.1.0")]
    [InlineData("999.0.0")]
    [InlineData("0.2")]
    [InlineData("0.2.0.0")]
    public void 异版或未标注版本拒绝打开且保持源文件(string? storedVersion)
    {
        ProjectStore.Save(SampleProject(), _directory.Path);
        string projectPath = _directory.FilePath(ProjectJson.ProjectFileName);
        string lyricsPath = ProjectFileFixture.LyricsPath(_directory.Path);
        JsonNode document = JsonNode.Parse(File.ReadAllText(projectPath))!;
        if (storedVersion is null)
        {
            document.AsObject().Remove("applicationVersion");
        }
        else
        {
            document["applicationVersion"] = storedVersion;
        }

        File.WriteAllText(projectPath, document.ToJsonString());
        string beforeProject = File.ReadAllText(projectPath);
        string beforeLyrics = File.ReadAllText(lyricsPath);

        ProjectLoadResult result = ProjectStore.Load(_directory.Path);

        Assert.False(result.Loaded);
        Assert.True(result.Report.Contains(DiagnosticCode.ApplicationVersionMismatch));
        Assert.Equal(beforeProject, File.ReadAllText(projectPath));
        Assert.Equal(beforeLyrics, File.ReadAllText(lyricsPath));
    }

    [Fact]
    public void 便携版补丁允许打开同格式01160工程且保持源文件()
    {
        ProjectStore.Save(SampleProject(), _directory.Path);
        string projectPath = _directory.FilePath(ProjectJson.ProjectFileName);
        string lyricsPath = ProjectFileFixture.LyricsPath(_directory.Path);
        JsonNode document = JsonNode.Parse(File.ReadAllText(projectPath))!;
        document["applicationVersion"] = "0.116.0";
        File.WriteAllText(projectPath, document.ToJsonString());
        byte[] beforeProject = File.ReadAllBytes(projectPath);
        byte[] beforeLyrics = File.ReadAllBytes(lyricsPath);

        ProjectLoadResult result = ProjectStore.Load(_directory.Path);

        Assert.Equal("0.116.1", ProductVersion.Current);
        Assert.True(result.Loaded, result.Report.ToSummary());
        Assert.False(result.Report.Contains(DiagnosticCode.ApplicationVersionMismatch));
        Assert.Equal(beforeProject, File.ReadAllBytes(projectPath));
        Assert.Equal(beforeLyrics, File.ReadAllBytes(lyricsPath));
    }

    [Fact]
    public void 目标目录不存在时会被创建()
    {
        string nested = Path.Combine(_directory.Path, "a", "b", "song.mkgproj");

        ProjectStore.Save(SampleProject(), nested);

        Assert.True(File.Exists(Path.Combine(nested, ProjectJson.ProjectFileName)));
    }

    [Fact]
    public void 中文直接写入而不是转义序列()
    {
        // 项目文件是可读的源数据格式，中文被转成 \uXXXX 会让 diff 与人工检查失去意义。
        ProjectStore.Save(SampleProject(), _directory.Path);

        string lyricsJson = ProjectFileFixture.ReadLyrics(_directory.Path);

        Assert.Contains("夜空中最亮", lyricsJson, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u", lyricsJson, StringComparison.Ordinal);
    }

    [Fact]
    public void 帧率以有理数保存()
    {
        Project project = SampleProject() with
        {
            Composition = SampleProject().Composition with { FrameRate = FrameRate.NtscVideo },
        };

        ProjectStore.Save(project, _directory.Path);

        string projectJson = _directory.ReadFile(ProjectJson.ProjectFileName);

        Assert.Contains("\"numerator\": 30000", projectJson, StringComparison.Ordinal);
        Assert.Contains("\"denominator\": 1001", projectJson, StringComparison.Ordinal);
    }

    [Fact]
    public void 保存不会留下临时文件()
    {
        ProjectStore.Save(SampleProject(), _directory.Path);

        Assert.False(File.Exists(_directory.FilePath(ProjectJson.ProjectFileName + ProjectJson.TemporaryFileSuffix)));
        Assert.Empty(Directory.GetFiles(_directory.Path, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public void 覆盖保存不会残留旧内容()
    {
        ProjectStore.Save(SampleProject(), _directory.Path);

        Project edited = Project.Create(
            "改名后的项目",
            SampleProject().Composition,
            LyricsDocument.Create());

        ProjectStore.Save(edited, _directory.Path);

        ProjectLoadResult result = ProjectStore.Load(_directory.Path);

        Assert.Equal(edited, result.Project);
        Assert.Empty(result.Project!.Lyrics.Lines);
    }

    [Fact]
    public void 输出时长未确定时可以往返()
    {
        Project original = Project.Create(
            "还没有音频的项目",
            Composition.Create(1920, 1080, FrameRate.Fps30),
            LyricsDocument.Create());

        ProjectStore.Save(original, _directory.Path);
        ProjectLoadResult result = ProjectStore.Load(_directory.Path);

        Assert.Equal(original, result.Project);
        Assert.Null(result.Project!.Composition.OutputDurationUs);
    }

    [Fact]
    public void 透明背景色往返无损()
    {
        Project original = Project.Create(
            "透明画布",
            Composition.Create(1080, 1920, FrameRate.Fps24, RgbaColor.Transparent, 1_000_000),
            LyricsDocument.Create());

        ProjectStore.Save(original, _directory.Path);
        ProjectLoadResult result = ProjectStore.Load(_directory.Path);

        Assert.Equal(RgbaColor.Transparent, result.Project!.Composition.BackgroundColor);
        Assert.Equal(original, result.Project);
    }

    [Fact]
    public void 非法时间区间被原样写回而不是被静默修正()
    {
        // docs/development.md §19 设计底线：不得静默修正用户数据。
        LyricLine brokenLine = new()
        {
            Id = "line-001",
            Text = "你好",
            Time = new TimeRange(2_000_000, 1_000_000),
            TimingStatus = TimingStatus.Invalid,
        };

        Project broken = new()
        {
            Name = "非法数据项目",
            Composition = Composition.Create(1920, 1080, FrameRate.Fps30, outputDurationUs: 1_000_000),
            Lyrics = new LyricsDocument { Lines = [brokenLine] },
        };

        ProjectStore.Save(broken, _directory.Path);
        ProjectLoadResult result = ProjectStore.Load(_directory.Path);

        Assert.Equal(broken, result.Project);
        Assert.Equal(new TimeRange(2_000_000, 1_000_000), result.Project!.Lyrics.Lines[0].Time);
        Assert.True(result.Report.Contains(DiagnosticCode.InvalidTimeRange));
    }

    [Fact]
    public void 缺少时间的行可以往返()
    {
        Project original = Project.Create(
            "只有文本没有时间的项目",
            Composition.Create(1920, 1080, FrameRate.Fps30),
            LyricsDocument.Create([LyricLine.Create("line-001", "夜空中最亮")]));

        ProjectStore.Save(original, _directory.Path);
        ProjectLoadResult result = ProjectStore.Load(_directory.Path);

        Assert.Equal(original, result.Project);
        Assert.Null(result.Project!.Lyrics.Lines[0].Time);
    }

    [Fact]
    public void 保存两次得到相同字节()
    {
        // 确定性写入：同一份数据重复保存必须得到完全相同的文件，金样测试才有意义。
        ProjectStore.Save(SampleProject(), _directory.Path);
        string firstProject = _directory.ReadFile(ProjectJson.ProjectFileName);
        string firstLyrics = ProjectFileFixture.ReadLyrics(_directory.Path);

        ProjectStore.Save(SampleProject(), _directory.Path);

        Assert.Equal(firstProject, _directory.ReadFile(ProjectJson.ProjectFileName));
        Assert.Equal(firstLyrics, ProjectFileFixture.ReadLyrics(_directory.Path));
    }

    private static Project SampleProject() => Project.Create(
        "夜空中最亮的星",
        Composition.Create(1920, 1080, FrameRate.Fps30, RgbaColor.Parse("#101018"), 5_000_000),
        LyricsDocument.Create(
        [
            LyricLine.Create(
                "line-001",
                "夜空中最亮",
                new TimeRange(1_200_000, 3_400_000),
                TimingStatus.Edited,
                [
                    LyricToken.Create("token-001", "夜", new TimeRange(1_200_000, 1_700_000), TimingStatus.Confirmed),
                    LyricToken.Create("token-002", "空中", new TimeRange(1_700_000, 2_300_000), TimingStatus.Confirmed),
                    LyricToken.Create("token-003", "最亮", new TimeRange(2_300_000, 3_400_000), TimingStatus.Estimated),
                ]),
        ],
        "zh-CN"));
}
