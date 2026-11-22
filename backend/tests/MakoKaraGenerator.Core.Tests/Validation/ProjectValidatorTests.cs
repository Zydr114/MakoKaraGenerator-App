using MakoKaraGenerator.Core.Diagnostics;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Core.Validation;

namespace MakoKaraGenerator.Core.Tests.Validation;

/// <summary>
/// 校验器规则与"同一规则在不同上下文下严重程度不同"的测试（roadmap CORE-05）。
/// </summary>
public sealed class ProjectValidatorTests
{
    [Fact]
    public void 完全合法的项目没有任何诊断()
    {
        ValidationReport report = ProjectValidator.Validate(BuildValidProject(), ValidationContext.Export);

        Assert.Equal(0, report.Count);
        Assert.False(report.HasErrors);
        Assert.False(report.HasWarnings);
        Assert.Equal("未发现问题。", report.ToSummary());
    }

    [Fact]
    public void 合法项目在打开与导出上下文都无错误()
    {
        Project project = BuildValidProject();

        Assert.False(ProjectValidator.Validate(project, ValidationContext.Open).HasErrors);
        Assert.False(ProjectValidator.Validate(project, ValidationContext.Import).HasErrors);
        Assert.False(ProjectValidator.Validate(project, ValidationContext.Export).HasErrors);
    }

    [Theory]
    [InlineData(" ", TimingStatus.Missing, null, false)]
    [InlineData("\t\u3000", TimingStatus.Missing, null, false)]
    [InlineData("、", TimingStatus.Missing, null, false)]
    [InlineData("！", TimingStatus.Missing, null, false)]
    [InlineData("「", TimingStatus.Missing, null, false)]
    [InlineData(", ", TimingStatus.Missing, null, false)]
    [InlineData("字", TimingStatus.Missing, DiagnosticCode.MissingTime, true)]
    [InlineData("♪", TimingStatus.Missing, DiagnosticCode.MissingTime, true)]
    [InlineData("", TimingStatus.Missing, DiagnosticCode.EmptyText, true)]
    [InlineData(" ", TimingStatus.Imported, DiagnosticCode.TimeStatusMismatch, true)]
    [InlineData("、", TimingStatus.Imported, DiagnosticCode.TimeStatusMismatch, true)]
    [InlineData(" ", TimingStatus.Confirmed, DiagnosticCode.InvalidTimeRange, true)]
    public void 导出只豁免实际无时间的非空排版元素(string text, TimingStatus status, DiagnosticCode? code, bool blocked)
    {
        var token = new LyricToken { Id = "token", Text = text, TimingStatus = status,
            Time = status == TimingStatus.Confirmed ? new TimeRange(50_000, 50_000) : null };
        var line = LyricLine.Create("line", "甲" + text, new TimeRange(0, 1_000_000), TimingStatus.Confirmed,
            [LyricToken.Create("anchor", "甲", new TimeRange(0, 500_000), TimingStatus.Confirmed), token]);
        var project = Project.Create("字幕", BuildValidProject().Composition, LyricsDocument.Create([line]));

        ValidationReport report = ProjectValidator.Validate(project, ValidationContext.Export);

        Assert.Equal(blocked, report.HasErrors);
        if (code is { } expected)
            Assert.Contains(report.Issues, issue => issue.Code == expected && issue.Severity == IssueSeverity.Error);
    }

    [Fact]
    public void schema_版本不符是阻断性错误()
    {
        Project project = BuildValidProject() with { SchemaVersion = 99 };

        ValidationReport report = ProjectValidator.Validate(project, ValidationContext.Open);

        Assert.True(report.HasErrors);
        Assert.Equal(IssueSeverity.Error, SeverityOf(report, DiagnosticCode.UnsupportedSchemaVersion));
    }

    [Fact]
    public void 歌词_schema_版本也会被检查()
    {
        Project project = BuildValidProject() with
        {
            Lyrics = BuildValidProject().Lyrics with { SchemaVersion = 42 },
        };

        ValidationReport report = ProjectValidator.Validate(project, ValidationContext.Open);

        Assert.True(report.Contains(DiagnosticCode.UnsupportedSchemaVersion));
        Assert.Contains("lyrics.schemaVersion", report.Issues.Select(static issue => issue.Location));
    }

    [Theory]
    [InlineData(ValidationContext.Open, IssueSeverity.Info)]
    [InlineData(ValidationContext.Import, IssueSeverity.Info)]
    [InlineData(ValidationContext.Export, IssueSeverity.Warning)]
    public void 空歌词的严重程度随上下文变化(ValidationContext context, IssueSeverity expected)
    {
        Project project = BuildValidProject() with { Lyrics = LyricsDocument.Create() };

        ValidationReport report = ProjectValidator.Validate(project, context);

        Assert.Equal(expected, SeverityOf(report, DiagnosticCode.EmptyLyrics));
    }

    [Theory]
    [InlineData(ValidationContext.Open, IssueSeverity.Info)]
    [InlineData(ValidationContext.Export, IssueSeverity.Error)]
    public void 输出时长未确定在导出时是错误(ValidationContext context, IssueSeverity expected)
    {
        Project project = BuildValidProject() with
        {
            Composition = BuildValidProject().Composition with { OutputDurationUs = null },
        };

        ValidationReport report = ProjectValidator.Validate(project, context);

        Assert.Equal(expected, SeverityOf(report, DiagnosticCode.MissingOutputDuration));
    }

    [Fact]
    public void 输出时长非正是阻断性错误()
    {
        Project project = BuildValidProject() with
        {
            Composition = BuildValidProject().Composition with { OutputDurationUs = -1 },
        };

        ValidationReport report = ProjectValidator.Validate(project, ValidationContext.Open);

        Assert.True(report.Contains(DiagnosticCode.InvalidOutputDuration));
        Assert.True(report.HasErrors);
    }

    [Theory]
    [InlineData(0, 1080)]
    [InlineData(1920, 0)]
    [InlineData(-1920, -1080)]
    public void 画布尺寸非法是阻断性错误(int width, int height)
    {
        Project project = BuildValidProject() with
        {
            Composition = BuildValidProject().Composition with
            {
                CanvasWidth = width,
                CanvasHeight = height,
            },
        };

        ValidationReport report = ProjectValidator.Validate(project, ValidationContext.Open);

        Assert.True(report.Contains(DiagnosticCode.InvalidCanvasSize));
        Assert.True(report.HasErrors);
    }

    [Theory]
    [InlineData(ValidationContext.Open, IssueSeverity.Warning)]
    [InlineData(ValidationContext.Export, IssueSeverity.Error)]
    public void 空文本在导出时是错误(ValidationContext context, IssueSeverity expected)
    {
        LyricLine emptyLine = new()
        {
            Id = "line-001",
            Text = string.Empty,
            Time = new TimeRange(0, 1_000_000),
            TimingStatus = TimingStatus.Confirmed,
        };

        Project project = BuildValidProject() with
        {
            Lyrics = new LyricsDocument { Lines = [emptyLine] },
        };

        ValidationReport report = ProjectValidator.Validate(project, context);

        Assert.Equal(expected, SeverityOf(report, DiagnosticCode.EmptyText));
    }

    [Theory]
    [InlineData(ValidationContext.Open, IssueSeverity.Info)]
    [InlineData(ValidationContext.Export, IssueSeverity.Error)]
    public void 时间缺失在导出时是错误(ValidationContext context, IssueSeverity expected)
    {
        LyricLine untimedLine = LyricLine.Create("line-001", "你好");

        Project project = BuildValidProject() with
        {
            Lyrics = new LyricsDocument { Lines = [untimedLine] },
        };

        ValidationReport report = ProjectValidator.Validate(project, context);

        Assert.Equal(expected, SeverityOf(report, DiagnosticCode.MissingTime));
    }

    [Fact]
    public void 标识重复是阻断性错误()
    {
        // 必须用对象初始化器而不是 Create：Create 是可信路径，会当场拦住重复标识；
        // 校验器要处理的是从文件读进来的、未经 Create 把关的数据。
        LyricLine line = new()
        {
            Id = "line-001",
            Text = "你你",
            Time = new TimeRange(0, 1_000_000),
            TimingStatus = TimingStatus.Confirmed,
            Tokens =
            [
                LyricToken.Create("token-001", "你", new TimeRange(0, 500_000), TimingStatus.Confirmed),
                LyricToken.Create("token-001", "你", new TimeRange(500_000, 1_000_000), TimingStatus.Confirmed),
            ],
        };

        Project project = BuildValidProject() with
        {
            Lyrics = new LyricsDocument { Lines = [line] },
        };

        ValidationReport report = ProjectValidator.Validate(project, ValidationContext.Open);

        Assert.True(report.Contains(DiagnosticCode.DuplicateIdentifier));
        Assert.True(report.HasErrors);
    }

    [Fact]
    public void 行之间标识重复也是错误()
    {
        LyricLine first = LyricLine.Create(
            "line-001",
            "你",
            new TimeRange(0, 500_000),
            TimingStatus.Confirmed,
            [LyricToken.Create("token-001", "你", new TimeRange(0, 500_000), TimingStatus.Confirmed)]);

        LyricLine second = LyricLine.Create(
            "line-001",
            "好",
            new TimeRange(500_000, 1_000_000),
            TimingStatus.Confirmed,
            [LyricToken.Create("token-002", "好", new TimeRange(500_000, 1_000_000), TimingStatus.Confirmed)]);

        Project project = BuildValidProject() with
        {
            Lyrics = new LyricsDocument { Lines = [first, second] },
        };

        ValidationReport report = ProjectValidator.Validate(project, ValidationContext.Open);

        Assert.True(report.Contains(DiagnosticCode.DuplicateIdentifier));
    }

    [Fact]
    public void 行标识与元素标识冲突也算重复()
    {
        // 行与元素共用同一命名空间：跨层级重名会让按标识引用对象变得歧义。
        LyricLine line = LyricLine.Create(
            "shared-id",
            "你好",
            new TimeRange(0, 1_000_000),
            TimingStatus.Confirmed,
            [
                LyricToken.Create("shared-id", "你", new TimeRange(0, 500_000), TimingStatus.Confirmed),
                LyricToken.Create("token-002", "好", new TimeRange(500_000, 1_000_000), TimingStatus.Confirmed),
            ]);

        Project project = BuildValidProject() with
        {
            Lyrics = new LyricsDocument { Lines = [line] },
        };

        ValidationReport report = ProjectValidator.Validate(project, ValidationContext.Open);

        Assert.True(report.Contains(DiagnosticCode.DuplicateIdentifier));
    }

    [Fact]
    public void 非法时间区间是阻断性错误()
    {
        LyricLine brokenLine = new()
        {
            Id = "line-001",
            Text = "你好",
            Time = new TimeRange(1_000_000, 500_000),
            TimingStatus = TimingStatus.Invalid,
        };

        Project project = BuildValidProject() with
        {
            Lyrics = new LyricsDocument { Lines = [brokenLine] },
        };

        ValidationReport report = ProjectValidator.Validate(project, ValidationContext.Open);

        Assert.Equal(IssueSeverity.Error, SeverityOf(report, DiagnosticCode.InvalidTimeRange));
    }

    [Fact]
    public void 时间与状态不相符是阻断性错误()
    {
        LyricLine mismatched = new()
        {
            Id = "line-001",
            Text = "你好",
            Time = new TimeRange(0, 1_000_000),
            TimingStatus = TimingStatus.Missing,
        };

        Project project = BuildValidProject() with
        {
            Lyrics = new LyricsDocument { Lines = [mismatched] },
        };

        ValidationReport report = ProjectValidator.Validate(project, ValidationContext.Open);

        Assert.Equal(IssueSeverity.Error, SeverityOf(report, DiagnosticCode.TimeStatusMismatch));
    }

    [Fact]
    public void 元素越出所属行只是警告()
    {
        LyricLine line = LyricLine.Create(
            "line-001",
            "你好",
            new TimeRange(1_000_000, 2_000_000),
            TimingStatus.Confirmed,
            [
                LyricToken.Create("token-001", "你", new TimeRange(1_000_000, 1_500_000), TimingStatus.Confirmed),
                LyricToken.Create("token-002", "好", new TimeRange(1_500_000, 2_500_000), TimingStatus.Confirmed),
            ]);

        Project project = BuildValidProject() with
        {
            Lyrics = new LyricsDocument { Lines = [line] },
        };

        ValidationReport report = ProjectValidator.Validate(project, ValidationContext.Open);

        Assert.Equal(IssueSeverity.Warning, SeverityOf(report, DiagnosticCode.ElementOutsideLine));
        Assert.False(report.HasErrors);
    }

    [Fact]
    public void 元素时间重叠只是警告()
    {
        LyricLine line = LyricLine.Create(
            "line-001",
            "你好",
            new TimeRange(0, 2_000_000),
            TimingStatus.Confirmed,
            [
                LyricToken.Create("token-001", "你", new TimeRange(0, 1_000_000), TimingStatus.Confirmed),
                LyricToken.Create("token-002", "好", new TimeRange(800_000, 2_000_000), TimingStatus.Confirmed),
            ]);

        Project project = BuildValidProject() with
        {
            Lyrics = new LyricsDocument { Lines = [line] },
        };

        ValidationReport report = ProjectValidator.Validate(project, ValidationContext.Open);

        Assert.Equal(IssueSeverity.Warning, SeverityOf(report, DiagnosticCode.OverlappingElements));
        Assert.False(report.HasErrors);
    }

    [Fact]
    public void 相邻元素无缝相接不算重叠()
    {
        LyricLine line = LyricLine.Create(
            "line-001",
            "你好",
            new TimeRange(0, 2_000_000),
            TimingStatus.Confirmed,
            [
                LyricToken.Create("token-001", "你", new TimeRange(0, 1_000_000), TimingStatus.Confirmed),
                LyricToken.Create("token-002", "好", new TimeRange(1_000_000, 2_000_000), TimingStatus.Confirmed),
            ]);

        ValidationReport report = ProjectValidator.Validate(
            BuildValidProject() with { Lyrics = new LyricsDocument { Lines = [line] } },
            ValidationContext.Export);

        Assert.False(report.Contains(DiagnosticCode.OverlappingElements));
    }

    [Fact]
    public void 歌词行未按时间排序只是警告()
    {
        LyricLine first = LyricLine.Create(".", "你", new TimeRange(2_000_000, 3_000_000), TimingStatus.Confirmed);
        LyricLine second = LyricLine.Create(".", "好", new TimeRange(1_000_000, 2_000_000), TimingStatus.Confirmed);

        Project project = BuildValidProject() with
        {
            Lyrics = new LyricsDocument { Lines = [first, second] },
        };

        ValidationReport report = ProjectValidator.Validate(project, ValidationContext.Open);

        Assert.Equal(IssueSeverity.Warning, SeverityOf(report, DiagnosticCode.LinesOutOfOrder));
    }

    [Fact]
    public void 元素文本拼接与行文本不一致只是警告()
    {
        LyricLine line = LyricLine.Create(
            "line-001",
            "你好",
            new TimeRange(0, 1_000_000),
            TimingStatus.Confirmed,
            [
                LyricToken.Create("token-001", "你", new TimeRange(0, 500_000), TimingStatus.Confirmed),
                LyricToken.Create("token-002", "世界", new TimeRange(500_000, 1_000_000), TimingStatus.Confirmed),
            ]);

        Project project = BuildValidProject() with
        {
            Lyrics = new LyricsDocument { Lines = [line] },
        };

        ValidationReport report = ProjectValidator.Validate(project, ValidationContext.Open);

        Assert.Equal(IssueSeverity.Warning, SeverityOf(report, DiagnosticCode.TokenTextMismatch));
    }

    [Fact]
    public void 未拆分的行只是提示()
    {
        LyricLine line = LyricLine.Create(
            "line-001",
            "你好",
            new TimeRange(0, 1_000_000),
            TimingStatus.Confirmed);

        Project project = BuildValidProject() with
        {
            Lyrics = new LyricsDocument { Lines = [line] },
        };

        ValidationReport report = ProjectValidator.Validate(project, ValidationContext.Open);

        Assert.Equal(IssueSeverity.Info, SeverityOf(report, DiagnosticCode.UntokenizedLine));
        Assert.False(report.HasErrors);
    }

    [Theory]
    [InlineData(ValidationContext.Open, IssueSeverity.Info)]
    [InlineData(ValidationContext.Export, IssueSeverity.Warning)]
    public void 未确认时间在导出时升级为警告(ValidationContext context, IssueSeverity expected)
    {
        LyricLine line = LyricLine.Create(
            "line-001",
            "你好",
            new TimeRange(0, 1_000_000),
            TimingStatus.Edited,
            [
                LyricToken.Create("token-001", "你", new TimeRange(0, 500_000), TimingStatus.Estimated),
                LyricToken.Create("token-002", "好", new TimeRange(500_000, 1_000_000), TimingStatus.Confirmed),
            ]);

        Project project = BuildValidProject() with
        {
            Lyrics = new LyricsDocument { Lines = [line] },
        };

        ValidationReport report = ProjectValidator.Validate(project, context);

        Assert.Equal(expected, SeverityOf(report, DiagnosticCode.UnconfirmedTiming));
    }

    [Fact]
    public void 缺失的时间不会被重复计入未确认统计()
    {
        // Missing 已经由 MissingTime 报告；UnconfirmedTiming 专门表示"有时间但没确认"。
        LyricLine line = LyricLine.Create(
            "line-001",
            "你好",
            new TimeRange(0, 1_000_000),
            TimingStatus.Confirmed,
            [
                LyricToken.Create("token-001", "你"),
                LyricToken.Create("token-002", "好", new TimeRange(500_000, 1_000_000), TimingStatus.Confirmed),
            ]);

        Project project = BuildValidProject() with
        {
            Lyrics = new LyricsDocument { Lines = [line] },
        };

        ValidationReport report = ProjectValidator.Validate(project, ValidationContext.Open);

        Assert.False(report.Contains(DiagnosticCode.UnconfirmedTiming));
    }

    [Fact]
    public void 报告摘要列出统计与逐条内容()
    {
        Project project = BuildValidProject() with { Lyrics = LyricsDocument.Create() };

        string summary = ProjectValidator.Validate(project, ValidationContext.Open).ToSummary();

        Assert.Contains("提示 1", summary, StringComparison.Ordinal);
        Assert.Contains("歌词文档没有任何行", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void 校验空项目引用会抛异常()
    {
        Assert.Throws<ArgumentNullException>(() => ProjectValidator.Validate(null!, ValidationContext.Open));
    }

    [Fact]
    public void 报告可以合并且保持顺序()
    {
        ValidationReport first = ProjectValidator.Validate(
            BuildValidProject() with { Lyrics = LyricsDocument.Create() },
            ValidationContext.Open);

        ValidationReport second = ProjectValidator.Validate(
            BuildValidProject() with { SchemaVersion = 99 },
            ValidationContext.Open);

        ValidationReport merged = first.Merge(second);

        Assert.Equal(first.Count + second.Count, merged.Count);
        Assert.Equal(first.Issues[0], merged.Issues[0]);
        Assert.Equal(second.Issues[0], merged.Issues[first.Count]);
        Assert.Equal(first, first.Merge(ValidationReport.Empty));
        Assert.Equal(first, ValidationReport.Empty.Merge(first));
    }

    private static IssueSeverity SeverityOf(ValidationReport report, DiagnosticCode code) =>
        report.Issues.Single(issue => issue.Code == code).Severity;

    /// <summary>构造一份在各个上下文中都不产生诊断的项目。</summary>
    private static Project BuildValidProject() => Project.Create(
        "合法项目",
        Composition.Create(1920, 1080, FrameRate.Fps30, RgbaColor.Black, 5_000_000),
        LyricsDocument.Create(
        [
            LyricLine.Create(
                "line-001",
                "夜空中最亮",
                new TimeRange(1_200_000, 3_400_000),
                TimingStatus.Confirmed,
                [
                    LyricToken.Create("token-001", "夜", new TimeRange(1_200_000, 1_700_000), TimingStatus.Confirmed),
                    LyricToken.Create("token-002", "空中", new TimeRange(1_700_000, 2_300_000), TimingStatus.Confirmed),
                    LyricToken.Create("token-003", "最亮", new TimeRange(2_300_000, 3_400_000), TimingStatus.Confirmed),
                ]),
        ],
        "zh-CN"));
}
