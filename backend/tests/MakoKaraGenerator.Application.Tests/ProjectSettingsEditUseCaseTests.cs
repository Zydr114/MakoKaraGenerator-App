using System.Collections.Immutable;
using MakoKaraGenerator.Application.Editing;
using MakoKaraGenerator.Core.Commands;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.TestSupport;

namespace MakoKaraGenerator.Application.Tests;

public sealed class ProjectSettingsEditUseCaseTests
{
    [Fact]
    public void 四个字段组独立提交且一次撤销恢复完整旧工程()
    {
        Project source = Sample();
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(source);
        string[] names = ["composition.canvas", "composition.frame-rate", "composition.background", "composition.duration"];
        var operations = Enumerable.Range(0, 4).Select(kind => Capture(workspace, kind)).ToArray();
        foreach ((Func<ProjectSettingsEditResult> operation, int index) in operations.Select((value, index) => (value, index)))
        {
            Project before = workspace.Project;
            ProjectSettingsEditResult result = operation();
            Assert.True(result.Accepted); Assert.True(result.Changed); Assert.Null(result.Error);
            Assert.Equal(names[index], workspace.UndoName);
            Assert.Same(source.Lyrics, result.Project.Lyrics);
            Assert.Same(source.Background, result.Project.Background);
            Assert.Same(source.AudioPath, result.Project.AudioPath);
            Assert.Same(before, workspace.Undo());
            Assert.Same(result.Project, workspace.Redo());
        }
        Assert.Equal(4, workspace.History.UndoCount);
        Assert.Equal(641, workspace.Project.Composition.CanvasWidth);
        Assert.Equal(361, workspace.Project.Composition.CanvasHeight);
        Assert.Equal(FrameRate.NtscVideo, workspace.Project.Composition.FrameRate);
        Assert.Equal(RgbaColor.Parse("#12345678"), workspace.Project.Composition.BackgroundColor);
        Assert.Equal(9_007_199_254_740_993L, workspace.Project.Composition.OutputDurationUs);
        for (int index = 0; index < 4; index++) workspace.Undo();
        Assert.Same(source, workspace.Project);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public void 所有设置入口拒绝旧工作区代次即使工程引用相同(int kind)
    {
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(Sample());
        Func<ProjectSettingsEditResult> operation = Capture(workspace, kind);
        workspace.StartNew(workspace.Project);
        AssertRejected(workspace, operation, ProjectSettingsEditErrorCode.WorkspaceStale);
    }

    [Fact]
    public void 同内容的另一工作区不能冒用捕获的设置目标()
    {
        Project source = Sample();
        ProjectWorkspace expected = ProjectWorkspace.Adopt(source), current = ProjectWorkspace.Adopt(source);
        AssertRejected(current, () => ProjectSettingsEditUseCase.SetCanvasSize(current,
            new(expected, expected.Generation, source.Composition.CanvasWidth, source.Composition.CanvasHeight, 641, 361)),
            ProjectSettingsEditErrorCode.WorkspaceStale);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public void 原字段组被改后所有入口拒绝覆盖(int kind)
    {
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(Sample());
        Func<ProjectSettingsEditResult> operation = Capture(workspace, kind);
        workspace.Execute(ProjectCommand.Create("other", project => kind switch
        {
            0 => CompositionEditor.SetCanvasSize(project, 800, project.Composition.CanvasHeight),
            1 => CompositionEditor.SetFrameRate(project, FrameRate.Fps24),
            2 => CompositionEditor.SetBackgroundColor(project, RgbaColor.White),
            _ => CompositionEditor.SetOutputDuration(project, 2_000_000),
        }));
        AssertRejected(workspace, operation, ProjectSettingsEditErrorCode.TargetStale);
    }

    [Fact]
    public void 宽高必须同时验证且无关设置变化保留最新值()
    {
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(Sample());
        var composition = workspace.Project.Composition;
        var request = new ProjectCanvasSizeEditRequest(workspace, workspace.Generation,
            composition.CanvasWidth, composition.CanvasHeight, 641, 361);
        workspace.Execute(ProjectCommand.Create("duration", project => CompositionEditor.SetOutputDuration(project, 3_000_007)));
        Assert.True(ProjectSettingsEditUseCase.SetCanvasSize(workspace, request).Accepted);
        Assert.Equal(3_000_007, workspace.Project.Composition.OutputDurationUs);
        var stale = request with { ExpectedWidth = 641, Width = 801 };
        AssertRejected(workspace, () => ProjectSettingsEditUseCase.SetCanvasSize(workspace, stale), ProjectSettingsEditErrorCode.TargetStale);
    }

    [Theory]
    [InlineData(0, 360)] [InlineData(-1, 360)] [InlineData(640, 0)] [InlineData(640, -1)]
    public void 非正画布拒绝整个字段组并保留重做(int width, int height)
    {
        ProjectWorkspace workspace = WithRedo(Sample());
        var composition = workspace.Project.Composition;
        AssertRejected(workspace, () => ProjectSettingsEditUseCase.SetCanvasSize(workspace,
            new(workspace, workspace.Generation, composition.CanvasWidth, composition.CanvasHeight, width, height)),
            ProjectSettingsEditErrorCode.InvalidCanvasSize);
        Assert.Equal(1, workspace.History.RedoCount);
    }

    [Fact]
    public void 默认零帧率拒绝但已有Core非法数据仍可在别组编辑()
    {
        Project source = Sample() with { Composition = new() { CanvasWidth = 0, CanvasHeight = -1, OutputDurationUs = -3 } };
        ProjectWorkspace workspace = WithRedo(source);
        AssertRejected(workspace, () => ProjectSettingsEditUseCase.SetFrameRate(workspace,
            new(workspace, workspace.Generation, source.Composition.FrameRate, default)), ProjectSettingsEditErrorCode.InvalidFrameRate);
        var result = ProjectSettingsEditUseCase.SetBackgroundColor(workspace,
            new(workspace, workspace.Generation, source.Composition.BackgroundColor, RgbaColor.White));
        Assert.True(result.Accepted);
        Assert.Equal(0, result.Project.Composition.CanvasWidth);
        Assert.Equal(-1, result.Project.Composition.CanvasHeight);
        Assert.Equal(-3, result.Project.Composition.OutputDurationUs);
        Assert.Equal(source with { Composition = source.Composition with { BackgroundColor = RgbaColor.White } }, result.Project);
    }

    [Theory]
    [InlineData(0L)] [InlineData(-1L)] [InlineData(long.MinValue)]
    public void 非正输出时长拒绝而未确定空值可提交(long duration)
    {
        Project source = Sample();
        ProjectWorkspace workspace = WithRedo(source);
        AssertRejected(workspace, () => ProjectSettingsEditUseCase.SetOutputDuration(workspace,
            new(workspace, workspace.Generation, source.Composition.OutputDurationUs, duration)), ProjectSettingsEditErrorCode.InvalidOutputDuration);
        var cleared = ProjectSettingsEditUseCase.SetOutputDuration(workspace,
            new(workspace, workspace.Generation, source.Composition.OutputDurationUs, null));
        Assert.True(cleared.Accepted); Assert.True(cleared.Changed);
        Assert.Null(cleared.Project.Composition.OutputDurationUs);
        Assert.Same(source, workspace.Undo());
    }

    [Fact]
    public void 六个无变化入口均保留工程引用干净重做及零通知()
    {
        Project source = Sample();
        ProjectWorkspace workspace = WithRedo(source);
        int notifications = 0;
        workspace.History.Changed += (_, _) => notifications++;
        var composition = source.Composition;
        ProjectSettingsEditResult[] results =
        [
            ProjectSettingsEditUseCase.SetCanvasSize(workspace, new(workspace, workspace.Generation,
                composition.CanvasWidth, composition.CanvasHeight, composition.CanvasWidth, composition.CanvasHeight)),
            ProjectSettingsEditUseCase.SetFrameRate(workspace, new(workspace, workspace.Generation, composition.FrameRate, composition.FrameRate)),
            ProjectSettingsEditUseCase.SetBackgroundColor(workspace, new(workspace, workspace.Generation, composition.BackgroundColor, composition.BackgroundColor)),
            ProjectSettingsEditUseCase.SetOutputDuration(workspace, new(workspace, workspace.Generation, composition.OutputDurationUs, composition.OutputDurationUs)),
            ProjectSettingsEditUseCase.UseLyricsEnd(workspace, new(workspace, workspace.Generation, composition.OutputDurationUs)),
            ProjectSettingsEditUseCase.UsePreparedMediaDuration(workspace, new(workspace, workspace.Generation, composition.OutputDurationUs), composition.OutputDurationUs!.Value),
        ];
        Assert.All(results, result => { Assert.True(result.Accepted); Assert.False(result.Changed); Assert.Same(source, result.Project); });
        Assert.Same(source, workspace.Project);
        Assert.False(workspace.IsDirty);
        Assert.Equal(0, workspace.History.UndoCount);
        Assert.Equal(1, workspace.History.RedoCount);
        Assert.Equal(0, notifications);
    }

    [Fact]
    public void 歌词末尾在提交时读取最新来源并沿Core时间策略过滤()
    {
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(Sample());
        var baseline = new ProjectOutputDurationEditBaseline(workspace, workspace.Generation, workspace.Project.Composition.OutputDurationUs);
        var independent = LyricBranch.Create("independent", "独立", lines: [LyricLine.Create("independent-line", "新", new(0, 3_000_007), TimingStatus.Edited)]);
        var untimed = LyricBranch.Create("untimed", "无时间", lines: [LyricLine.Create("untimed-line", "忽略", new(0, 9_000_000), TimingStatus.Edited)],
            timingMode: LyricBranchTimingMode.None);
        var follow = LyricBranch.Create("follow", "跟随", lines: [LyricLine.Create("follow-line", "忽略", new(0, 8_000_000), TimingStatus.Edited) with { PrimaryLineId = "line" }],
            timingMode: LyricBranchTimingMode.FollowPrimary);
        ImmutableArray<LyricBranch> additionalBranches = [independent, untimed, follow];
        workspace.Execute(ProjectCommand.Create("lyrics", project => project with { Lyrics = project.Lyrics with
            { Branches = project.Lyrics.Branches.AddRange(additionalBranches) } }));
        Project latest = workspace.Project;
        var result = ProjectSettingsEditUseCase.UseLyricsEnd(workspace, baseline);
        Assert.True(result.Accepted); Assert.True(result.Changed);
        Assert.Equal(3_000_007, result.Project.Composition.OutputDurationUs);
        Assert.Same(latest.Lyrics, result.Project.Lyrics);
        Assert.Same(latest, workspace.Undo());
    }

    [Fact]
    public void 零长非法范围不派生零时长而最小合法末点一微秒可提交()
    {
        var line = LyricLine.Create("line", "零", new TimeRange(0, 0), TimingStatus.Invalid);
        Project source = Project.Create("零范围", lyrics: LyricsDocument.Create([line]));
        ProjectWorkspace workspace = WithRedo(source);
        var baseline = new ProjectOutputDurationEditBaseline(workspace, workspace.Generation, null);
        var missing = ProjectSettingsEditUseCase.UseLyricsEnd(workspace, baseline);
        Assert.True(missing.Accepted); Assert.False(missing.Changed); Assert.Same(source, workspace.Project);
        Assert.Equal(1, workspace.History.RedoCount);
        workspace.Execute(ProjectCommand.Create("range", project => project with { Lyrics = project.Lyrics with
            { Lines = [line with { Time = new TimeRange(0, 1), TimingStatus = TimingStatus.Edited }] } }));
        var minimum = ProjectSettingsEditUseCase.UseLyricsEnd(workspace, baseline);
        Assert.True(minimum.Accepted); Assert.True(minimum.Changed);
        Assert.Equal(1, minimum.Project.Composition.OutputDurationUs);
    }

    [Theory]
    [InlineData(0L)] [InlineData(-1L)]
    public void 不可用的已准备媒体标量拒绝且不猜测时长(long duration)
    {
        ProjectWorkspace workspace = WithRedo(Sample());
        AssertRejected(workspace, () => ProjectSettingsEditUseCase.UsePreparedMediaDuration(workspace,
            new(workspace, workspace.Generation, workspace.Project.Composition.OutputDurationUs), duration),
            ProjectSettingsEditErrorCode.MediaDurationUnavailable);
    }

    [Fact]
    public void 已准备媒体长度保留整数微秒且不更换媒体归属()
    {
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(Sample());
        Project source = workspace.Project;
        var result = ProjectSettingsEditUseCase.UsePreparedMediaDuration(workspace,
            new(workspace, workspace.Generation, source.Composition.OutputDurationUs), 9_007_199_254_740_993);
        Assert.True(result.Accepted);
        Assert.Equal(9_007_199_254_740_993L, result.Project.Composition.OutputDurationUs);
        Assert.Same(source.AudioPath, result.Project.AudioPath);
        Assert.Equal("composition.duration", workspace.UndoName);
        Assert.Same(source, workspace.Undo());
    }

    [Fact]
    public void 展示候选拒绝不提交历史且无变化不触发旧非法展示校验()
    {
        Project source = ExclusiveDisplayFixture.Create();
        source = source with { LyricPresentation = source.LyricPresentation with
            { Nodes = source.LyricPresentation.Nodes.Add(source.LyricPresentation.Nodes[0]) } };
        ProjectWorkspace workspace = WithRedo(source);
        var current = source.Composition;
        AssertRejected(workspace, () => ProjectSettingsEditUseCase.SetBackgroundColor(workspace,
            new(workspace, workspace.Generation, current.BackgroundColor, RgbaColor.White)), ProjectSettingsEditErrorCode.DomainValidationFailed);
        var result = ProjectSettingsEditUseCase.SetBackgroundColor(workspace,
            new(workspace, workspace.Generation, current.BackgroundColor, current.BackgroundColor));
        Assert.True(result.Accepted); Assert.False(result.Changed);
        Assert.Same(source, workspace.Project);
        Assert.Equal(1, workspace.History.RedoCount);
    }

    private static Project Sample() => Project.Create("设置", new() { CanvasWidth = 640, CanvasHeight = 360, OutputDurationUs = 1_000_000 },
        LyricsDocument.Create([LyricLine.Create("line", "旧", new(0, 1_000_000), TimingStatus.Edited)]), audioPath: "prepared.wav");

    private static ProjectWorkspace WithRedo(Project source)
    {
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(source);
        workspace.Execute(ProjectCommand.Create("probe", project => project with { Name = "changed" }));
        workspace.Undo();
        return workspace;
    }

    private static Func<ProjectSettingsEditResult> Capture(ProjectWorkspace workspace, int kind)
    {
        var composition = workspace.Project.Composition;
        var canvas = new ProjectCanvasSizeEditRequest(workspace, workspace.Generation, composition.CanvasWidth, composition.CanvasHeight, 641, 361);
        var frame = new ProjectFrameRateEditRequest(workspace, workspace.Generation, composition.FrameRate, FrameRate.NtscVideo);
        var color = new ProjectBackgroundColorEditRequest(workspace, workspace.Generation, composition.BackgroundColor, RgbaColor.Parse("#12345678"));
        var duration = new ProjectOutputDurationEditRequest(workspace, workspace.Generation, composition.OutputDurationUs, 9_007_199_254_740_993);
        var derived = new ProjectOutputDurationEditBaseline(workspace, workspace.Generation, composition.OutputDurationUs);
        return kind switch
        {
            0 => () => ProjectSettingsEditUseCase.SetCanvasSize(workspace, canvas),
            1 => () => ProjectSettingsEditUseCase.SetFrameRate(workspace, frame),
            2 => () => ProjectSettingsEditUseCase.SetBackgroundColor(workspace, color),
            3 => () => ProjectSettingsEditUseCase.SetOutputDuration(workspace, duration),
            4 => () => ProjectSettingsEditUseCase.UseLyricsEnd(workspace, derived),
            _ => () => ProjectSettingsEditUseCase.UsePreparedMediaDuration(workspace, derived, 4_000_000),
        };
    }

    private static void AssertRejected(ProjectWorkspace workspace, Func<ProjectSettingsEditResult> operation, ProjectSettingsEditErrorCode code)
    {
        Project source = workspace.Project;
        int undo = workspace.History.UndoCount, redo = workspace.History.RedoCount, notifications = 0;
        bool dirty = workspace.IsDirty;
        workspace.History.Changed += OnChanged;
        try
        {
            ProjectSettingsEditResult result = operation();
            Assert.False(result.Accepted); Assert.False(result.Changed);
            Assert.Equal(code, result.Error?.Code);
            Assert.Same(source, result.Project); Assert.Same(source, workspace.Project);
            Assert.Equal(undo, workspace.History.UndoCount); Assert.Equal(redo, workspace.History.RedoCount);
            Assert.Equal(dirty, workspace.IsDirty); Assert.Equal(0, notifications);
        }
        finally { workspace.History.Changed -= OnChanged; }
        void OnChanged(object? sender, EventArgs args) => notifications++;
    }
}
