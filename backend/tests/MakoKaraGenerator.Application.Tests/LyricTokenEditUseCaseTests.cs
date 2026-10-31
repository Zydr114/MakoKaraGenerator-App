using System.Collections.Immutable;
using MakoKaraGenerator.Application.Editing;
using MakoKaraGenerator.Core.Commands;
using MakoKaraGenerator.Core.Diagnostics;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Core.Validation;
using MakoKaraGenerator.TestSupport;

namespace MakoKaraGenerator.Application.Tests;

public sealed class LyricTokenEditUseCaseTests
{
    internal static Project Sample(string text = "甲乙")
    {
        var range = TimeRange.Create(1_000_499, 3_000_503);
        var token = LyricToken.Create("joined", text, range, TimingStatus.Imported,
            new() { UnsungColor = RgbaColor.Black });
        var line = LyricLine.Create("one", text, TimeRange.Create(0, 4_000_007), TimingStatus.Edited,
            [token], new() { FontSizePx = 43 }) with
        { RubyAnnotations = [RubyAnnotation.Create("ruby", token.Id, "こう", "ja", .7, 3)], PrimaryLineId = "missing" };
        return Project.Create("切分", lyrics: LyricsDocument.Create([line]));
    }

    internal static LyricContentEditBaseline Baseline(ProjectWorkspace workspace, string? token = "joined", string branchId = "primary")
    {
        var branch = workspace.Project.Lyrics.Branches.First(branch => branch.Id == branchId);
        return new(workspace, workspace.Generation, new(branchId, branch.Lines[0].Id, token), branch.Lines[0]);
    }

    private static LyricBranchTokenSplitRequest BranchRequest(ProjectWorkspace workspace, TokenSplitRule rule = TokenSplitRule.PerTextElement,
        string branchId = "primary", bool estimate = false)
    {
        var branch = workspace.Project.Lyrics.Branches.First(branch => branch.Id == branchId);
        return new(new(workspace, workspace.Generation, branchId, branch), rule, estimate);
    }

    [Fact]
    public void 拆分Unicode完整元素保留左身份原元数据和端点并单次历史可恢复()
    {
        var source = Sample("e\u0301👨‍👩‍👧‍👦乙");
        var workspace = ProjectWorkspace.Adopt(source);
        int notifications = 0;
        workspace.History.Changed += (_, _) => notifications++;
        var result = LyricTokenEditUseCase.SplitToken(workspace, Baseline(workspace));
        Assert.True(result.Accepted); Assert.True(result.Changed);
        var line = result.Project.Lyrics.Lines[0];
        Assert.Equal("e\u0301", line.Tokens[0].Text);
        Assert.Equal("👨‍👩‍👧‍👦乙", line.Tokens[1].Text);
        Assert.Equal("joined", line.Tokens[0].Id);
        Assert.Equal(1_000_499, line.Tokens[0].Time!.Value.StartUs);
        Assert.Equal(3_000_503, line.Tokens[1].Time!.Value.EndUs);
        Assert.Equal(line.Tokens[0].Time!.Value.EndUs, line.Tokens[1].Time!.Value.StartUs);
        Assert.All(line.Tokens, token => Assert.Equal(TimingStatus.Estimated, token.TimingStatus));
        Assert.Same(source.Lyrics.Lines[0].Tokens[0].StyleOverride, line.Tokens[0].StyleOverride);
        Assert.Null(line.Tokens[1].StyleOverride);
        Assert.Equal(source.Lyrics.Lines[0] with { Tokens = line.Tokens }, line);
        Assert.Equal(line.Tokens[1].Id, result.SelectedTokenId);
        Assert.Equal(line.Id, result.SelectedLineId);
        Assert.Equal(1, workspace.History.UndoCount); Assert.Equal(1, notifications);
        Assert.Equal("line.split", workspace.UndoName);
        Assert.Same(source, workspace.Undo()); Assert.Same(result.Project, workspace.Redo());
    }

    [Theory]
    [InlineData(0L, 9_007_199_254_740_993L, 4_503_599_627_370_496L)]
    [InlineData(0L, 9_007_199_254_740_995L, 4_503_599_627_370_498L)]
    [InlineData(0L, long.MaxValue, 4_611_686_018_427_387_904L)]
    [InlineData(9_007_199_254_740_993L, 9_007_199_254_741_000L, 9_007_199_254_740_997L)]
    [InlineData(100L, 105L, 102L)]
    [InlineData(100L, 107L, 104L)]
    public void 拆分用整数比例保留大区间和奇数微秒偶数舍入(long start, long end, long cut)
    {
        var source = Sample();
        var line = source.Lyrics.Lines[0];
        line = line with { Time = TimeRange.Create(start, end), Tokens = [line.Tokens[0] with { Time = TimeRange.Create(start, end) }] };
        var workspace = ProjectWorkspace.Adopt(source with { Lyrics = LyricsDocument.Create([line]) });
        var result = LyricTokenEditUseCase.SplitToken(workspace, Baseline(workspace));
        Assert.True(result.Accepted); Assert.True(result.Changed);
        Assert.Equal(TimeRange.Create(start, cut), result.Project.Lyrics.Lines[0].Tokens[0].Time);
        Assert.Equal(TimeRange.Create(cut, end), result.Project.Lyrics.Lines[0].Tokens[1].Time);
    }

    [Theory]
    [InlineData(9_007_199_254_740_993L, 3_002_399_751_580_331L)]
    [InlineData(long.MaxValue, 3_074_457_345_618_258_602L)]
    public void 三文本元素拆分大微秒区间保留整数三分之一端点(long end, long cut)
    {
        var source = Sample("甲乙丙");
        var line = source.Lyrics.Lines[0];
        var range = TimeRange.Create(0, end);
        line = line with { Time = range, Tokens = [line.Tokens[0] with { Time = range }] };
        var workspace = ProjectWorkspace.Adopt(source with { Lyrics = LyricsDocument.Create([line]) });
        var result = LyricTokenEditUseCase.SplitToken(workspace, Baseline(workspace));
        Assert.True(result.Accepted, result.Error?.Detail); Assert.True(result.Changed);
        Assert.Equal(TimeRange.Create(0, cut), result.Project.Lyrics.Lines[0].Tokens[0].Time);
        Assert.Equal(TimeRange.Create(cut, end), result.Project.Lyrics.Lines[0].Tokens[1].Time);
        Assert.Equal("甲", result.Project.Lyrics.Lines[0].Tokens[0].Text);
        Assert.Equal("乙丙", result.Project.Lyrics.Lines[0].Tokens[1].Text);
        Assert.Equal(1, workspace.History.UndoCount);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)]
    public void 合并沿原并集状态保留第一覆盖并移除被删除基础元素的注音(bool secondMissing, bool differentStatus)
    {
        var source = Sample();
        var line = source.Lyrics.Lines[0];
        var first = line.Tokens[0] with { Text = "甲", Time = TimeRange.Create(1_000_499, 2_000_501) };
        var second = LyricToken.Create("second", "乙", secondMissing ? null : TimeRange.Create(2_000_501, 3_000_503),
            secondMissing ? TimingStatus.Missing : differentStatus ? TimingStatus.Edited : first.TimingStatus,
            new() { Opacity = .2f });
        line = line with { Tokens = [first, second], RubyAnnotations = line.RubyAnnotations.Add(RubyAnnotation.Create("second-ruby", second.Id, "おつ")) };
        var workspace = ProjectWorkspace.Adopt(source with { Lyrics = LyricsDocument.Create([line]) });
        var result = LyricTokenEditUseCase.MergeToken(workspace, Baseline(workspace));
        Assert.True(result.Accepted); Assert.True(result.Changed);
        var changed = result.Project.Lyrics.Lines[0]; var token = Assert.Single(changed.Tokens);
        Assert.Equal("joined", token.Id); Assert.Equal("甲乙", token.Text);
        Assert.Same(first.StyleOverride, token.StyleOverride);
        Assert.Equal(secondMissing ? null : TimeRange.Create(1_000_499, 3_000_503), token.Time);
        Assert.Equal(secondMissing ? TimingStatus.Missing : differentStatus ? TimingStatus.Estimated : TimingStatus.Imported, token.TimingStatus);
        Assert.Same(line.RubyAnnotations[0], Assert.Single(changed.RubyAnnotations));
        Assert.Equal(line with { Tokens = changed.Tokens, RubyAnnotations = changed.RubyAnnotations }, changed);
        Assert.Equal(token.Id, result.SelectedTokenId);
        Assert.Equal(1, workspace.History.UndoCount);
        Assert.DoesNotContain(ProjectValidator.Validate(result.Project, ValidationContext.Open).Issues,
            issue => issue.Code == DiagnosticCode.MissingLyricAssociation && issue.Message.Contains(second.Id, StringComparison.Ordinal));
        Assert.Equal(line, workspace.Undo().Lyrics.Lines[0]);
    }

    [Fact]
    public void 非主来源重切使用全局含注音ID并保持其他来源和原Core语义()
    {
        var source = Sample();
        var translated = source.Lyrics.Lines[0] with { Id = "translated", PrimaryLineId = "one",
            Tokens = [source.Lyrics.Lines[0].Tokens[0] with { Id = "translated-token" }],
            RubyAnnotations = [RubyAnnotation.Create("token-1000", "translated-token", "訳")] };
        var branch = LyricBranch.Create("translation", "翻译", LyricBranchRole.Translation, [translated],
            timingMode: LyricBranchTimingMode.FollowPrimary);
        source = source with { Lyrics = source.Lyrics with { Branches = source.Lyrics.Branches.Add(branch) } };
        var workspace = ProjectWorkspace.Adopt(source);
        var result = LyricTokenEditUseCase.ResplitBranch(workspace, BranchRequest(workspace, branchId: branch.Id));
        Assert.True(result.Accepted); Assert.True(result.Changed);
        Assert.Same(source.Lyrics.PrimaryBranch, result.Project.Lyrics.PrimaryBranch);
        Assert.Equal("primary", result.Project.Lyrics.PrimaryBranchId);
        var line = result.Project.Lyrics.Branches[1].Lines[0];
        Assert.Equal(translated with { Tokens = line.Tokens }, line);
        Assert.Equal("translated-token", line.Tokens[0].Id);
        Assert.Same(translated.Tokens[0].StyleOverride, line.Tokens[0].StyleOverride);
        Assert.Equal("token-1001", line.Tokens[1].Id); Assert.Null(line.Tokens[1].Time);
        Assert.Equal(1, result.ChangedLineCount); Assert.Equal(1, result.PreservedCount);
        Assert.Equal(1, result.CreatedCount); Assert.Equal(0, result.EstimatedCount);
        Assert.Equal("translation", Assert.Single(result.ChangeSet.Rows).BranchId);
        Assert.Equal(1, workspace.History.UndoCount);
    }

    [Fact]
    public void 全来源批次后行编号耗尽时前行也不提交()
    {
        var source = Sample(); var line = source.Lyrics.Lines[0];
        line = line with { RubyAnnotations = [RubyAnnotation.Create("token-2147483646", "joined", "こう")] };
        var second = line with { Id = "two", Tokens = [line.Tokens[0] with { Id = "second" }], RubyAnnotations = [] };
        var workspace = ProjectWorkspace.Adopt(source with { Lyrics = LyricsDocument.Create([line, second]) });
        AssertRejected(workspace, () => LyricTokenEditUseCase.ResplitBranch(workspace, BranchRequest(workspace)),
            LyricContentEditErrorCode.IdentifierExhausted);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void 拆合重切拒绝旧完整行或来源但允许目标不变的无关工程变化(int action)
    {
        var workspace = ProjectWorkspace.Adopt(Sample());
        var baseline = Baseline(workspace); var lineBaseline = baseline with { Target = baseline.Target with { TokenId = null } };
        var branchRequest = BranchRequest(workspace);
        workspace.Execute(ProjectCommand.Create("name", project => project with { Name = "无关" }));
        var operation = new Func<LyricTokenEditResult>(() => action switch
        {
            0 => LyricTokenEditUseCase.SplitToken(workspace, baseline),
            1 => LyricTokenEditUseCase.MergeToken(workspace, baseline),
            2 => LyricTokenEditUseCase.ResplitLine(workspace, new(lineBaseline, ["甲", "乙"], false)),
            _ => LyricTokenEditUseCase.ResplitBranch(workspace, branchRequest),
        });
        workspace.Execute(ProjectCommand.Create("ruby", project => project with { Lyrics = project.Lyrics with
            { Lines = [project.Lyrics.Lines[0] with { RubyAnnotations = [] }] } }));
        AssertRejected(workspace, operation, LyricContentEditErrorCode.TargetStale);
        workspace.Undo();
        Assert.True(operation().Accepted);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void 工作区代次或实例失效时拒绝同ID工程(bool replacement)
    {
        var workspace = ProjectWorkspace.Adopt(Sample()); var baseline = Baseline(workspace);
        if (replacement) workspace = ProjectWorkspace.Adopt(workspace.Project);
        else workspace.StartNew(workspace.Project with { Name = "新工程" });
        AssertRejected(workspace, () => LyricTokenEditUseCase.SplitToken(workspace, baseline), LyricContentEditErrorCode.WorkspaceStale);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void 无变化保留引用脏状态重做且零历史通知(int action)
    {
        var workspace = ProjectWorkspace.Adopt(Sample("甲"));
        workspace.Execute(ProjectCommand.Create("name", project => project with { Name = "临时" })); workspace.Undo();
        var source = workspace.Project; bool dirty = workspace.IsDirty; int notifications = 0;
        workspace.History.Changed += (_, _) => notifications++;
        var result = action switch
        {
            0 => LyricTokenEditUseCase.SplitToken(workspace, Baseline(workspace)),
            1 => LyricTokenEditUseCase.MergeToken(workspace, Baseline(workspace)),
            2 => LyricTokenEditUseCase.ResplitLine(workspace, new(Baseline(workspace, null), ["甲"], true)),
            _ => LyricTokenEditUseCase.ResplitBranch(workspace, BranchRequest(workspace, TokenSplitRule.None)),
        };
        Assert.True(result.Accepted); Assert.False(result.Changed); Assert.Same(source, result.Project);
        Assert.Same(source, workspace.Project); Assert.Equal(dirty, workspace.IsDirty);
        Assert.Equal(0, workspace.History.UndoCount); Assert.Equal(1, workspace.History.RedoCount); Assert.Equal(0, notifications);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void 手工重切拒绝文本不匹配和拆坏Unicode边界(bool unicode)
    {
        var workspace = ProjectWorkspace.Adopt(Sample("e\u0301乙"));
        ImmutableArray<string> segments = unicode ? ["e", "\u0301乙"] : ["甲", "乙"];
        AssertRejected(workspace, () => LyricTokenEditUseCase.ResplitLine(workspace,
            new(Baseline(workspace, null), segments, false)), LyricContentEditErrorCode.InvalidTarget);
    }

    [Fact]
    public void 呈现真实拒绝不返回假成功且重做不丢失()
    {
        var source = ExclusiveDisplayFixture.Create();
        var line = LyricLine.Create("split", "甲乙", TimeRange.Create(0, 4_000_000), TimingStatus.Edited,
            [LyricToken.Create("joined", "甲乙", TimeRange.Create(1_000_000, 3_000_000), TimingStatus.Edited)]);
        source = source with { Lyrics = source.Lyrics with { Lines = source.Lyrics.Lines.Insert(0, line) },
            LyricPresentation = source.LyricPresentation with { Programs = source.LyricPresentation.Programs.SetItem(0,
                source.LyricPresentation.Programs[0] with { RootContainerId = "missing" }) } };
        var workspace = ProjectWorkspace.Adopt(source);
        AssertRejected(workspace, () => LyricTokenEditUseCase.SplitToken(workspace, Baseline(workspace)), LyricContentEditErrorCode.DomainValidationFailed);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public void 合并非法持久化元素时间返回拒绝不抛异常也不静默修复(bool firstInvalid, bool reversed)
    {
        var source = Sample();
        var line = source.Lyrics.Lines[0];
        var invalid = reversed ? new TimeRange(2_000_501, 1_000_499) : new TimeRange(-1, 2_000_501);
        var first = line.Tokens[0] with { Text = "甲", Time = firstInvalid ? invalid : TimeRange.Create(1_000_499, 2_000_501),
            TimingStatus = firstInvalid ? TimingStatus.Invalid : TimingStatus.Imported };
        var second = LyricToken.Create("second", "乙", firstInvalid ? TimeRange.Create(2_000_501, 3_000_503) : invalid,
            firstInvalid ? TimingStatus.Imported : TimingStatus.Invalid);
        var workspace = WithRedo(source with { Lyrics = LyricsDocument.Create([line with { Tokens = [first, second] }]) });

        AssertRejected(workspace, () => LyricTokenEditUseCase.MergeToken(workspace, Baseline(workspace)),
            LyricContentEditErrorCode.DomainValidationFailed);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public void 估算非法持久化区间返回拒绝且整个来源批次不提交(bool wholeLine, bool branch)
    {
        var source = Sample();
        var line = source.Lyrics.Lines[0];
        var invalid = new TimeRange(2_000_501, 1_000_499);
        line = wholeLine
            ? line with { Time = invalid, TimingStatus = TimingStatus.Invalid, Tokens = [line.Tokens[0] with { Time = null, TimingStatus = TimingStatus.Missing }] }
            : line with { Tokens = [line.Tokens[0] with { Time = invalid, TimingStatus = TimingStatus.Invalid }] };
        var validFirst = source.Lyrics.Lines[0] with { Id = "valid-first", Tokens = [line.Tokens[0] with
            { Id = "valid-token", Time = TimeRange.Create(1_000_499, 3_000_503), TimingStatus = TimingStatus.Imported }], RubyAnnotations = [] };
        var workspace = WithRedo(source with { Lyrics = LyricsDocument.Create(branch ? [validFirst, line] : [line]) });
        AssertRejected(workspace, () => branch
            ? LyricTokenEditUseCase.ResplitBranch(workspace, BranchRequest(workspace, estimate: true))
            : LyricTokenEditUseCase.ResplitLine(workspace, new(Baseline(workspace, null), ["甲", "乙"], true)),
            LyricContentEditErrorCode.DomainValidationFailed);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public void 重切合并来源时不把非法元素区间静默变成合法并集(bool firstInvalid, bool estimate)
    {
        var source = Sample();
        var line = source.Lyrics.Lines[0];
        var invalid = new TimeRange(2_000_501, 1_000_499);
        var first = line.Tokens[0] with { Text = "甲", Time = firstInvalid ? invalid : TimeRange.Create(1_000_499, 2_000_501),
            TimingStatus = firstInvalid ? TimingStatus.Invalid : TimingStatus.Imported };
        var second = LyricToken.Create("second", "乙", firstInvalid ? TimeRange.Create(2_000_501, 3_000_503) : invalid,
            firstInvalid ? TimingStatus.Imported : TimingStatus.Invalid);
        var workspace = WithRedo(source with { Lyrics = LyricsDocument.Create([line with { Tokens = [first, second] }]) });
        AssertRejected(workspace, () => LyricTokenEditUseCase.ResplitLine(workspace,
            new(Baseline(workspace, null), ["甲乙"], estimate)), LyricContentEditErrorCode.DomainValidationFailed);
    }

    private static ProjectWorkspace WithRedo(Project source)
    {
        var workspace = ProjectWorkspace.Adopt(source);
        workspace.Execute(ProjectCommand.Create("redo", project => project with { Name = "重做" }));
        workspace.Undo();
        return workspace;
    }

    private static void AssertRejected(ProjectWorkspace workspace, Func<LyricTokenEditResult> operation, LyricContentEditErrorCode code)
    {
        var source = workspace.Project; bool dirty = workspace.IsDirty;
        int undo = workspace.History.UndoCount, redo = workspace.History.RedoCount, notifications = 0;
        workspace.History.Changed += OnChanged;
        void OnChanged(object? sender, EventArgs e) => notifications++;
        var result = operation();
        workspace.History.Changed -= OnChanged;
        Assert.False(result.Accepted); Assert.False(result.Changed); Assert.Equal(code, result.Error?.Code);
        Assert.Same(source, result.Project); Assert.Same(source, workspace.Project); Assert.Equal(dirty, workspace.IsDirty);
        Assert.Equal(undo, workspace.History.UndoCount); Assert.Equal(redo, workspace.History.RedoCount); Assert.Equal(0, notifications);
    }
}
