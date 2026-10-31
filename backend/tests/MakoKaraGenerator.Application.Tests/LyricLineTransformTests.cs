using MakoKaraGenerator.Application.Editing;
using MakoKaraGenerator.Core.Commands;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;

namespace MakoKaraGenerator.Application.Tests;

public sealed class LyricLineTransformTests
{
    private static readonly string[] ExpectedShiftTargets = ["line", "a", "b"];
    private static readonly string[] ExpectedReorderedTokenIds = ["b", "a", "untimed"];

    [Fact]
    public void 平移预览无副作用提交保留精确微秒身份并一次撤销()
    {
        Project source = Sample();
        var workspace = ProjectWorkspace.Adopt(source);
        var baseline = Capture(workspace);
        int notifications = 0;
        workspace.History.Changed += (_, _) => notifications++;

        var preview = LyricContentEditUseCase.PreviewShiftLine(workspace, baseline, 500_001);
        Assert.True(preview.Accepted, preview.Error?.Detail);
        Assert.True(preview.Changed);
        Assert.Same(source, workspace.Project);
        Assert.Equal(0, notifications);
        Assert.False(workspace.IsDirty);
        var result = LyricContentEditUseCase.ShiftLine(workspace, baseline, 500_001);

        Assert.True(result.Accepted, result.Error?.Detail);
        Assert.Equal(preview.Project, result.Project);
        LyricLine line = result.Project.Lyrics.Lines[0];
        Assert.Equal(TimeRange.Create(1_500_500, 5_500_500), line.Time);
        Assert.Equal(TimeRange.Create(1_500_500, 2_500_500), line.Tokens[0].Time);
        Assert.Equal(TimeRange.Create(2_500_500, 3_500_500), line.Tokens[2].Time);
        Assert.Equal(TimingStatus.Edited, line.TimingStatus);
        Assert.Equal(TimingStatus.Edited, line.Tokens[0].TimingStatus);
        Assert.Same(source.Lyrics.Lines[0].Tokens[1], line.Tokens[1]);
        Assert.Same(source.Lyrics.Lines[0].StyleOverride, line.StyleOverride);
        Assert.Same(source.Lyrics.Lines[0].Tokens[0].StyleOverride, line.Tokens[0].StyleOverride);
        Assert.True(source.Lyrics.Lines[0].RubyAnnotations.SequenceEqual(line.RubyAnnotations));
        Assert.Equal(ExpectedShiftTargets, result.ChangeSet.Targets.Select(target => target.TokenId ?? target.LineId));
        Assert.False(result.ChangeSet.FullRefreshRequired);
        Assert.Equal("line.shift", workspace.UndoName);
        Assert.Equal(1, notifications);
        Assert.Equal(1, workspace.History.UndoCount);
        Assert.Same(source, workspace.Undo());
        Assert.Same(result.Project, workspace.Redo());
    }

    [Theory]
    [InlineData(0)] [InlineData(1)]
    public void 无变化平移重排保留原引用脏标记重做和零通知(int operation)
    {
        var workspace = WithRedo(Sample());
        Project source = workspace.Project;
        int notifications = 0;
        workspace.History.Changed += (_, _) => notifications++;
        if (operation == 0)
        {
            var result = LyricContentEditUseCase.ShiftLine(workspace, Capture(workspace), 0);
            Assert.True(result.Accepted); Assert.False(result.Changed); Assert.Same(source, result.Project);
        }
        else
        {
            var result = LyricContentEditUseCase.ReorderToken(workspace, new(Capture(workspace), "a", 0, true));
            Assert.True(result.Accepted); Assert.False(result.Changed); Assert.Same(source, result.Project);
        }
        Assert.Same(source, workspace.Project); Assert.False(workspace.IsDirty);
        Assert.Equal(0, workspace.History.UndoCount); Assert.Equal(1, workspace.History.RedoCount);
        Assert.Equal(0, notifications);
    }

    [Theory]
    [InlineData(-1_000_500L, 0L)]
    [InlineData(1L, 5_000_499L)]
    [InlineData(long.MaxValue, 0L)]
    [InlineData(long.MinValue, 0L)]
    public void 非法负值媒体边界及整数溢出平移不改变工程或重做(long deltaUs, long durationUs)
    {
        var workspace = WithRedo(Sample());
        Project source = workspace.Project;
        int notifications = 0;
        workspace.History.Changed += (_, _) => notifications++;
        var result = LyricContentEditUseCase.ShiftLine(workspace, Capture(workspace), deltaUs, durationUs);
        Assert.False(result.Accepted); Assert.NotNull(result.Error);
        Assert.Same(source, result.Project); Assert.Same(source, workspace.Project);
        Assert.Equal(0, notifications); Assert.False(workspace.IsDirty);
        Assert.Equal(0, workspace.History.UndoCount); Assert.Equal(1, workspace.History.RedoCount);
    }

    [Fact]
    public void 超过双精度安全整数的微秒平移精确保留()
    {
        long start = 9_007_199_254_740_993;
        LyricLine line = LyricLine.Create("line", "字", TimeRange.Create(start, start + 1_000_007), TimingStatus.Edited,
            [LyricToken.Create("a", "字", TimeRange.Create(start + 1, start + 900_499), TimingStatus.Edited)]);
        var workspace = ProjectWorkspace.Adopt(Project.Create("large", lyrics: LyricsDocument.Create([line])));
        var result = LyricContentEditUseCase.ShiftLine(workspace, Capture(workspace), 1);
        Assert.True(result.Accepted, result.Error?.Detail);
        Assert.Equal(start + 1, result.Project.Lyrics.Lines[0].Time!.Value.StartUs);
        Assert.Equal(start + 2, result.Project.Lyrics.Lines[0].Tokens[0].Time!.Value.StartUs);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(6)]
    public void 完整原行任意受保护字段变化均拒绝平移和下标重排(int field)
    {
        var workspace = ProjectWorkspace.Adopt(Sample());
        var baseline = Capture(workspace);
        LyricLine old = baseline.ExpectedLine;
        LyricLine changed = field switch
        {
            0 => old with { Text = "另" },
            1 => old with { Time = TimeRange.Create(0, 6_000_499) },
            2 => old with { TimingStatus = TimingStatus.Edited },
            3 => old with { Tokens = old.Tokens.SetItem(0, old.Tokens[0] with { Time = TimeRange.Create(1_000_500, 2_000_499) }) },
            4 => old with { RubyAnnotations = old.RubyAnnotations.SetItem(0, old.RubyAnnotations[0] with { Text = "新注音" }) },
            5 => old with { StyleOverride = old.StyleOverride! with { FontSizePx = 111 } },
            _ => old with { PrimaryLineId = "changed-link" },
        };
        workspace.Execute(ProjectCommand.Create("change", project => project with
            { Lyrics = project.Lyrics with { Lines = project.Lyrics.Lines.SetItem(0, changed) } }));
        Project current = workspace.Project;
        var shift = LyricContentEditUseCase.ShiftLine(workspace, baseline, 10);
        var reorder = LyricContentEditUseCase.ReorderToken(workspace, new(baseline, "a", 2, true));
        Assert.False(shift.Accepted); Assert.Equal(LyricTimingEditErrorCode.TargetStale, shift.Error!.Code);
        Assert.False(reorder.Accepted); Assert.Equal(LyricContentEditErrorCode.TargetStale, reorder.Error!.Code);
        Assert.Same(current, workspace.Project); Assert.Equal(1, workspace.History.UndoCount);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public void 工作区代次来源行和元素失效拒绝请求(int kind)
    {
        var workspace = WithRedo(Sample());
        var baseline = Capture(workspace);
        baseline = kind switch
        {
            0 => baseline with { ExpectedWorkspace = ProjectWorkspace.Adopt(workspace.Project) },
            1 => baseline with { ExpectedWorkspaceGeneration = workspace.Generation + 1 },
            2 => baseline with { Target = baseline.Target with { BranchId = "missing" } },
            3 => baseline with { Target = baseline.Target with { LineId = "missing" } },
            _ => baseline,
        };
        var result = LyricContentEditUseCase.ReorderToken(workspace, new(baseline, kind == 4 ? "missing" : "a", 2, true));
        Assert.False(result.Accepted); Assert.False(workspace.IsDirty);
        Assert.Equal(1, workspace.History.RedoCount); Assert.Equal(0, workspace.History.UndoCount);
        Assert.Equal(kind < 2 ? LyricContentEditErrorCode.WorkspaceStale : LyricContentEditErrorCode.TargetStale, result.Error!.Code);
    }

    [Theory]
    [InlineData(-1, true)] [InlineData(3, true)] [InlineData(2, false)]
    public void 非法重排位置和未确认时间冲突拒绝且保留重做(int destination, bool reassign)
    {
        var workspace = WithRedo(Sample());
        Project source = workspace.Project;
        var result = LyricContentEditUseCase.ReorderToken(workspace, new(Capture(workspace), "a", destination, reassign));
        Assert.False(result.Accepted); Assert.NotEmpty(result.Error!.Detail!);
        Assert.Same(source, workspace.Project); Assert.False(workspace.IsDirty);
        Assert.Equal(1, workspace.History.RedoCount); Assert.Equal(0, workspace.History.UndoCount);
    }

    [Fact]
    public void 重排显式重分配时间槽保留注音样式身份和未定时元素()
    {
        Project source = Sample();
        var workspace = ProjectWorkspace.Adopt(source);
        var request = new LyricTokenReorderRequest(Capture(workspace), "b", 0, true);
        var preview = LyricContentEditUseCase.PreviewReorderToken(workspace, request);
        Assert.True(preview.Accepted, preview.Error?.Detail); Assert.Same(source, workspace.Project);
        var result = LyricContentEditUseCase.ReorderToken(workspace, request);
        Assert.True(result.Accepted, result.Error?.Detail);
        LyricLine line = result.Project.Lyrics.Lines[0];
        Assert.Equal("乙甲🙂", line.Text); Assert.Equal(ExpectedReorderedTokenIds, line.Tokens.Select(token => token.Id));
        Assert.Equal(source.Lyrics.Lines[0].Time, line.Time);
        Assert.Equal(source.Lyrics.Lines[0].TimingStatus, line.TimingStatus);
        Assert.Equal(source.Lyrics.Lines[0].Tokens[0].Time, line.Tokens[0].Time);
        Assert.Equal(source.Lyrics.Lines[0].Tokens[2].Time, line.Tokens[1].Time);
        Assert.Equal(TimingStatus.Edited, line.Tokens[1].TimingStatus);
        Assert.Same(source.Lyrics.Lines[0].Tokens[1], line.Tokens[2]);
        Assert.Same(source.Lyrics.Lines[0].Tokens[0].StyleOverride, line.Tokens[1].StyleOverride);
        Assert.True(source.Lyrics.Lines[0].RubyAnnotations.SequenceEqual(line.RubyAnnotations));
        var change = Assert.Single(result.ChangeSet.Rows);
        Assert.True(change.Kind.HasFlag(LyricLineChangeKind.Tokens));
        Assert.Equal("line.reorder", workspace.UndoName); Assert.Equal(1, workspace.History.UndoCount);
        Assert.Same(source, workspace.Undo()); Assert.Same(result.Project, workspace.Redo());
    }

    [Fact]
    public void 不定时元素移动无需重分配且已定时元素仍是原实例()
    {
        var workspace = ProjectWorkspace.Adopt(Sample());
        var old = workspace.Project.Lyrics.Lines[0];
        var result = LyricContentEditUseCase.ReorderToken(workspace, new(Capture(workspace), "untimed", 0, false));
        Assert.True(result.Accepted, result.Error?.Detail);
        Assert.Equal("🙂甲乙", result.Project.Lyrics.Lines[0].Text);
        Assert.Same(old.Tokens[0], result.Project.Lyrics.Lines[0].Tokens[1]);
        Assert.Same(old.Tokens[2], result.Project.Lyrics.Lines[0].Tokens[2]);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void 翻译来源平移重排保留真正主来源和无关最新设置(bool reorder)
    {
        Project source = Sample();
        LyricBranch translation = LyricBranch.Create("translation", "翻译", LyricBranchRole.Translation,
            [source.Lyrics.Lines[0] with { Id = "translation-line", Tokens = [.. source.Lyrics.Lines[0].Tokens.Select(token => token with { Id = "tr-" + token.Id })], RubyAnnotations = [] }]);
        source = source with { Lyrics = source.Lyrics with { Branches = source.Lyrics.Branches.Add(translation) } };
        var workspace = ProjectWorkspace.Adopt(source);
        var baseline = new LyricContentEditBaseline(workspace, workspace.Generation, new(translation.Id, "translation-line"), translation.Lines[0]);
        workspace.Execute(ProjectCommand.Create("setting", project => project with { Name = "最新名称" }));
        if (reorder)
        {
            var result = LyricContentEditUseCase.ReorderToken(workspace, new(baseline, "tr-b", 0, true));
            Assert.True(result.Accepted, result.Error?.Detail);
            Assert.Equal("乙甲🙂", result.Project.Lyrics.Branches[1].Lines[0].Text);
        }
        else
        {
            var result = LyricContentEditUseCase.ShiftLine(workspace, baseline, 7);
            Assert.True(result.Accepted, result.Error?.Detail);
            Assert.Equal(1_000_506, result.Project.Lyrics.Branches[1].Lines[0].Time!.Value.StartUs);
        }
        Assert.Equal("最新名称", workspace.Project.Name);
        Assert.Equal(source.Lyrics.PrimaryBranchId, workspace.Project.Lyrics.PrimaryBranchId);
        Assert.Same(source.Lyrics.PrimaryBranch, workspace.Project.Lyrics.PrimaryBranch);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void 平移重排真实呈现拒绝均不改工程或历史(bool reorder)
    {
        Project source = Sample();
        source = source with { LyricPresentation = source.LyricPresentation with
            { Nodes = source.LyricPresentation.Nodes.Add(source.LyricPresentation.Nodes[0]) } };
        var workspace = WithRedo(source);
        if (reorder) Assert.False(LyricContentEditUseCase.ReorderToken(workspace, new(Capture(workspace), "b", 0, true)).Accepted);
        else Assert.False(LyricContentEditUseCase.ShiftLine(workspace, Capture(workspace), 1).Accepted);
        Assert.Same(source, workspace.Project); Assert.False(workspace.IsDirty);
        Assert.Equal(1, workspace.History.RedoCount); Assert.Equal(0, workspace.History.UndoCount);
    }

    private static LyricContentEditBaseline Capture(ProjectWorkspace workspace) =>
        new(workspace, workspace.Generation, new(workspace.Project.Lyrics.PrimaryBranchId, "line"), workspace.Project.Lyrics.Lines[0]);

    private static ProjectWorkspace WithRedo(Project source)
    {
        var workspace = ProjectWorkspace.Adopt(source);
        workspace.Execute(ProjectCommand.Create("redo", project => project with { Name = "redone" }));
        workspace.Undo();
        return workspace;
    }

    private static Project Sample() => Project.Create("transforms", lyrics: LyricsDocument.Create(
        [LyricLine.Create("line", "甲🙂乙", TimeRange.Create(1_000_499, 5_000_499), TimingStatus.Estimated,
            [LyricToken.Create("a", "甲", TimeRange.Create(1_000_499, 2_000_499), TimingStatus.Estimated,
                new LyricStyleOverride { SungColor = new RgbaColor(1, 2, 3) }),
             LyricToken.Create("untimed", "🙂"),
             LyricToken.Create("b", "乙", TimeRange.Create(2_000_499, 3_000_499), TimingStatus.Edited)],
            new LyricStyleOverride { FontSizePx = 77 }) with
            { RubyAnnotations = [RubyAnnotation.Create("ruby", "a", "こう")] }]));
}
