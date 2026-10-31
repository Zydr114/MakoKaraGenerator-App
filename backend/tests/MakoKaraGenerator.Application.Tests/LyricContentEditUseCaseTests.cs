using MakoKaraGenerator.Application.Editing;
using MakoKaraGenerator.Core.Commands;
using MakoKaraGenerator.Core.Diagnostics;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Core.Validation;

namespace MakoKaraGenerator.Application.Tests;

public sealed class LyricContentEditUseCaseTests
{
    [Theory]
    [InlineData("甲X👨‍👩‍👧‍👦é乙", 4)]
    [InlineData("甲👩🏽‍🚀é乙", 3)]
    public void 正文按完整Unicode元素重切且未改区域保留身份时间与样式(string text, int preserved)
    {
        Project source = Sample();
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(source);
        LyricContentEditResult result = LyricContentEditUseCase.SetLineText(workspace, TextRequest(workspace, text));
        LyricLine original = source.Lyrics.Lines[0];
        LyricLine edited = result.Project.Lyrics.Lines[0];

        Assert.True(result.Accepted);
        Assert.Equal(text, string.Concat(edited.Tokens.Select(token => token.Text)));
        Assert.Equal(preserved, result.PreservedCount);
        Assert.Equal(1, result.ResetCount);
        Assert.Same(original.Tokens[0], edited.Tokens[0]);
        Assert.Same(original.Tokens[^2], edited.Tokens[^2]);
        Assert.Same(original.Tokens[^1], edited.Tokens[^1]);
        Assert.Equal(original.Time, edited.Time);
        Assert.Equal(original.TimingStatus, edited.TimingStatus);
        Assert.Same(original.StyleOverride, edited.StyleOverride);
        LyricToken created = Assert.Single(edited.Tokens.Where(token => !original.Tokens.Any(old => old.Id == token.Id)));
        Assert.Null(created.Time);
        Assert.Equal(TimingStatus.Missing, created.TimingStatus);
        Assert.Equal("lyrics.set-line-text", workspace.UndoName);
        Assert.Equal(1, workspace.History.UndoCount);
        Assert.Same(source.Lyrics.Lines[1], workspace.Project.Lyrics.Lines[1]);
        Assert.Same(source, workspace.Undo());
        Assert.Same(result.Project, workspace.Redo());
    }

    [Fact]
    public void 跨改动边界的重切不丢未改文字并清理被删除基础元素的注音()
    {
        var merged = LyricToken.Create("merged", "夜空", TimeRange.Create(1, 2), TimingStatus.Imported);
        var tail = LyricToken.Create("tail", "尾", TimeRange.Create(2, 3), TimingStatus.Imported);
        var ruby = RubyAnnotation.Create("ruby-source", merged.Id, "よぞら");
        LyricLine line = LyricLine.Create("one", "夜空尾", TimeRange.Create(0, 4), TimingStatus.Imported,
            [merged, tail]) with { RubyAnnotations = [ruby] };
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(Project.Create("重切", lyrics: LyricsDocument.Create([line])));

        var result = LyricContentEditUseCase.SetLineText(workspace, TextRequest(workspace, "夜雨尾"));
        Assert.True(result.Accepted);
        Assert.Collection(result.Project.Lyrics.Lines[0].Tokens,
            token => Assert.Equal("夜", token.Text), token => Assert.Equal("雨", token.Text), token => Assert.Equal("尾", token.Text));
        Assert.Same(tail, result.Project.Lyrics.Lines[0].Tokens[2]);
        Assert.All(result.Project.Lyrics.Lines[0].Tokens.Take(2), token => Assert.Null(token.Time));
        Assert.Empty(result.Project.Lyrics.Lines[0].RubyAnnotations);
        Assert.DoesNotContain(ProjectValidator.Validate(result.Project, ValidationContext.Open).Issues,
            issue => issue.Code == DiagnosticCode.MissingLyricAssociation);
        Assert.Same(ruby, Assert.Single(workspace.Undo().Lyrics.Lines[0].RubyAnnotations));
    }

    [Fact]
    public void 原正文未变时输入使用最新时间而非捕获时的旧工程子树()
    {
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(Sample());
        var request = TextRequest(workspace, "甲X👨‍👩‍👧‍👦é乙");
        var timing = LyricTimingEditUseCase.SetTime(workspace,
            new(workspace, workspace.Generation, new("primary", "one", "first"), workspace.Project.Lyrics.Lines[0].Tokens[0].Time,
                null, 1_100_017));
        Assert.True(timing.Accepted);
        LyricToken latest = workspace.Project.Lyrics.Lines[0].Tokens[0];

        Assert.True(LyricContentEditUseCase.SetLineText(workspace, request).Accepted);
        Assert.Same(latest, workspace.Project.Lyrics.Lines[0].Tokens[0]);
        Assert.Equal(1_100_017, workspace.Project.Lyrics.Lines[0].Tokens[0].Time!.Value.EndUs);
        Assert.Equal(2, workspace.History.UndoCount);
    }

    [Fact]
    public void 非主来源正文和注音仅编辑明确来源并保留FollowPrimary关联()
    {
        Project source = Sample();
        LyricLine translated = LyricLine.Create("translated", "译", tokens: [LyricToken.Create("translated-token", "译")])
            with { PrimaryLineId = "one" };
        var translation = LyricBranch.Create("translation", "译文", LyricBranchRole.Translation, [translated],
            timingMode: LyricBranchTimingMode.FollowPrimary);
        source = source with { Lyrics = source.Lyrics with { Branches = source.Lyrics.Branches.Add(translation) } };
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(source);
        var text = LyricContentEditUseCase.SetLineText(workspace,
            new(workspace, workspace.Generation, new("translation", "translated"), "译", "译文"));
        Assert.True(text.Accepted);
        var ruby = LyricContentEditUseCase.SetRubyText(workspace,
            new(workspace, workspace.Generation, new("translation", "translated", "translated-token"), null, "やく"));

        Assert.True(ruby.Accepted);
        Assert.Same(source.Lyrics.PrimaryBranch, workspace.Project.Lyrics.PrimaryBranch);
        Assert.Equal("primary", workspace.Project.Lyrics.PrimaryBranchId);
        Assert.Equal("one", workspace.Project.Lyrics.Branches[1].Lines[0].PrimaryLineId);
        Assert.Equal(LyricBranchTimingMode.FollowPrimary, workspace.Project.Lyrics.Branches[1].TimingMode);
        Assert.Null(workspace.Project.Lyrics.Branches[1].Lines[0].Time);
        Assert.Equal("translation", Assert.Single(ruby.ChangeSet.Rows).BranchId);
    }

    [Fact]
    public void 连续正文仅同稳定行合并不同目标可逐次撤销()
    {
        Project source = Sample();
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(source);
        Assert.True(LyricContentEditUseCase.SetLineText(workspace, TextRequest(workspace, "甲" )).Accepted);
        Assert.True(LyricContentEditUseCase.SetLineText(workspace, TextRequest(workspace, "甲乙")).Accepted);
        Project first = workspace.Project;
        Assert.Equal(1, workspace.History.UndoCount);
        Assert.True(LyricContentEditUseCase.SetLineText(workspace, TextRequest(workspace, "丙丁", "two")).Accepted);
        Assert.Equal(2, workspace.History.UndoCount);
        Assert.Same(first, workspace.Undo());
        Assert.Same(source, workspace.Undo());
    }

    [Fact]
    public void 相同正文和空注音不变保持引用干净重做和零通知()
    {
        Project source = Sample();
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(source);
        Project changed = LyricContentEditUseCase.SetLineText(workspace, TextRequest(workspace, "修改")).Project;
        workspace.Undo();
        int notifications = 0;
        workspace.History.Changed += (_, _) => notifications++;

        var text = LyricContentEditUseCase.SetLineText(workspace, TextRequest(workspace, source.Lyrics.Lines[0].Text));
        var ruby = LyricContentEditUseCase.SetRubyText(workspace, RubyRequest(workspace, "last", string.Empty));
        Assert.True(text.Accepted); Assert.True(ruby.Accepted);
        Assert.False(text.Changed); Assert.False(ruby.Changed);
        Assert.Same(ProjectLyricsChangeSet.None, text.ChangeSet);
        Assert.Same(source, workspace.Project);
        Assert.False(workspace.IsDirty);
        Assert.Equal(0, workspace.History.UndoCount);
        Assert.Equal(1, workspace.History.RedoCount);
        Assert.Equal(0, notifications);
        Assert.Same(changed, workspace.Redo());
    }

    [Fact]
    public void 空正文是可编辑中间状态且空操作不切断连续正文组()
    {
        Project source = Sample();
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(source);
        Assert.True(LyricContentEditUseCase.SetLineText(workspace, TextRequest(workspace, null)).Accepted);
        Assert.Empty(workspace.Project.Lyrics.Lines[0].Tokens);
        Assert.Equal(string.Empty, workspace.Project.Lyrics.Lines[0].Text);
        Assert.True(LyricContentEditUseCase.SetLineText(workspace, TextRequest(workspace, string.Empty)).Accepted);
        Assert.True(LyricContentEditUseCase.SetLineText(workspace, TextRequest(workspace, "新")).Accepted);
        Assert.Equal(1, workspace.History.UndoCount);
        Assert.Same(source, workspace.Undo());
    }

    [Fact]
    public void 注音保留完整属性删除只影响指定基础字且一次历史可恢复()
    {
        Project source = Sample();
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(source);
        RubyAnnotation original = source.Lyrics.Lines[0].RubyAnnotations[0];
        int notifications = 0;
        workspace.History.Changed += (_, _) => notifications++;
        var result = LyricContentEditUseCase.SetRubyText(workspace, RubyRequest(workspace, "first", "こう2"));
        RubyAnnotation edited = result.Project.Lyrics.Lines[0].RubyAnnotations[0];
        Assert.True(result.Accepted);
        Assert.Equal(original with { Text = "こう2" }, edited);
        Assert.Same(source.Lyrics.Lines[0].Tokens[0], result.Project.Lyrics.Lines[0].Tokens[0]);
        Assert.Equal(LyricLineChangeKind.Ruby, Assert.Single(result.ChangeSet.Rows).Kind);
        Assert.Equal("line.ruby", workspace.UndoName);
        Assert.Equal(1, notifications);
        Assert.Same(source, workspace.Undo());
        Assert.Same(result.Project, workspace.Redo());
        Assert.True(LyricContentEditUseCase.SetRubyText(workspace, RubyRequest(workspace, "first", string.Empty)).Accepted);
        Assert.Empty(workspace.Project.Lyrics.Lines[0].RubyAnnotations);
        Assert.Same(result.Project, workspace.Undo());
    }

    [Fact]
    public void 连续注音只合并同稳定基础字不同元素与正文操作分别撤销()
    {
        Project source = Sample();
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(source);
        Assert.True(LyricContentEditUseCase.SetRubyText(workspace, RubyRequest(workspace, "first", "a"), true).Accepted);
        Assert.True(LyricContentEditUseCase.SetRubyText(workspace, RubyRequest(workspace, "first", "ab"), true).Accepted);
        Project first = workspace.Project;
        Assert.Equal(1, workspace.History.UndoCount);
        Assert.True(LyricContentEditUseCase.SetRubyText(workspace, RubyRequest(workspace, "last", "b"), true).Accepted);
        Project second = workspace.Project;
        Assert.True(LyricContentEditUseCase.SetLineText(workspace, TextRequest(workspace, "甲" )).Accepted);
        Assert.Equal(3, workspace.History.UndoCount);
        Assert.Same(second, workspace.Undo());
        Assert.Same(first, workspace.Undo());
        Assert.Same(source, workspace.Undo());
    }

    [Fact]
    public void 旧正文旧注音及相同文字但旧注音属性均拒绝且不损失重做()
    {
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(Sample());
        var oldText = TextRequest(workspace, "旧输入");
        var oldRuby = RubyRequest(workspace, "first", "旧注音");
        Assert.True(LyricContentEditUseCase.SetLineText(workspace, TextRequest(workspace, "新正文")).Accepted);
        AssertRejected(workspace, () => LyricContentEditUseCase.SetLineText(workspace, oldText), LyricContentEditErrorCode.TargetStale);
        workspace.Undo();
        var line = workspace.Project.Lyrics.Lines[0];
        workspace.Execute(ProjectCommand.Create("ruby.metadata", project => project with { Lyrics = project.Lyrics with
            { Lines = project.Lyrics.Lines.SetItem(0, line with { RubyAnnotations = [line.RubyAnnotations[0] with { GapPx = 9 }] }) } }));
        AssertRejected(workspace, () => LyricContentEditUseCase.SetRubyText(workspace, oldRuby), LyricContentEditErrorCode.TargetStale);
        workspace.Undo();
        AssertRejected(workspace, () => LyricContentEditUseCase.SetRubyText(workspace, oldRuby with
            { Target = new("primary", "one", "missing") }), LyricContentEditErrorCode.TargetStale);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public void 五类请求拒绝同标识新工作区与同工作区新代次(int operation)
    {
        ProjectWorkspace captured = ProjectWorkspace.Adopt(Sample());
        var text = TextRequest(captured, "新的");
        var ruby = RubyRequest(captured, "first", "新");
        var baseline = Baseline(captured);
        var insert = new LyricLineInsertRequest(captured, captured.Generation, "primary", "one", baseline.ExpectedLine);
        ProjectWorkspace other = ProjectWorkspace.Adopt(captured.Project);
        AssertRejected(other, () => Invoke(other), LyricContentEditErrorCode.WorkspaceStale);
        captured.StartNew(Sample());
        AssertRejected(captured, () => Invoke(captured), LyricContentEditErrorCode.WorkspaceStale);

        LyricContentEditResult Invoke(ProjectWorkspace target) => operation switch
        {
            0 => LyricContentEditUseCase.SetLineText(target, text),
            1 => LyricContentEditUseCase.SetRubyText(target, ruby),
            2 => LyricContentEditUseCase.AddLineAfter(target, insert),
            3 => LyricContentEditUseCase.DuplicateLine(target, baseline),
            _ => LyricContentEditUseCase.RemoveLine(target, baseline),
        };
    }

    [Fact]
    public void 稳定行定位不消费旧下标且旧通用命令不吞入共享正文合并()
    {
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(Sample());
        var request = TextRequest(workspace, "明确一行");
        workspace.Execute(ProjectCommand.Create("lyrics.set-line-text", project => project with
            { Lyrics = project.Lyrics with { Lines = [project.Lyrics.Lines[1], project.Lyrics.Lines[0]] } }, mergeable: true));
        Project moved = workspace.Project;
        Assert.True(LyricContentEditUseCase.SetLineText(workspace, request).Accepted);
        Assert.Same(moved.Lyrics.Lines[0], workspace.Project.Lyrics.Lines[0]);
        Assert.Equal("明确一行", workspace.Project.Lyrics.Lines[1].Text);
        Assert.Equal(2, workspace.History.UndoCount);
        Assert.Same(moved, workspace.Undo());
    }

    [Fact]
    public void 正文真实变化使旧拖动永久失效而相同正文不终止手势()
    {
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(Sample());
        Assert.True(LyricTimingEditUseCase.TryBeginGesture(workspace,
            new(workspace, workspace.Generation, new("primary", "one", "first"), workspace.Project.Lyrics.Lines[0].Tokens[0].Time),
            out var gesture, out _));
        Assert.NotNull(gesture);
        Assert.True(LyricContentEditUseCase.SetLineText(workspace, TextRequest(workspace, workspace.Project.Lyrics.Lines[0].Text)).Accepted);
        Assert.True(gesture.IsActive);
        Assert.True(LyricContentEditUseCase.SetLineText(workspace, TextRequest(workspace, "改字")).Accepted);
        workspace.Undo();
        Assert.False(gesture.IsActive);
        Assert.False(gesture.Update(null, 1_100_011).Accepted);
    }

    [Fact]
    public void 正文保留区域仍须通过原行时间校验且拒绝不写任何新元素()
    {
        Project source = Sample();
        var line = source.Lyrics.Lines[0];
        source = source with { Lyrics = source.Lyrics with { Lines = source.Lyrics.Lines.SetItem(0, line with
            { Tokens = line.Tokens.SetItem(0, line.Tokens[0] with { Time = TimeRange.Create(0, 1_100_011) }) }) } };
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(source);
        AssertRejected(workspace, () => LyricContentEditUseCase.SetLineText(workspace, TextRequest(workspace, "甲X👨‍👩‍👧‍👦é乙")),
            LyricContentEditErrorCode.DomainValidationFailed);
    }

    [Fact]
    public void 行和注音请求形状不符或明确来源消失均拒绝()
    {
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(Sample());
        AssertRejected(workspace, () => LyricContentEditUseCase.SetLineText(workspace, TextRequest(workspace, "新") with
            { Target = new("primary", "one", "first") }), LyricContentEditErrorCode.InvalidTarget);
        AssertRejected(workspace, () => LyricContentEditUseCase.SetRubyText(workspace, RubyRequest(workspace, "first", "新") with
            { Target = new("primary", "one") }), LyricContentEditErrorCode.InvalidTarget);
        AssertRejected(workspace, () => LyricContentEditUseCase.RemoveLine(workspace, Baseline(workspace) with
            { Target = new("primary", "one", "first") }), LyricContentEditErrorCode.InvalidTarget);
        AssertRejected(workspace, () => LyricContentEditUseCase.SetLineText(workspace, TextRequest(workspace, "新") with
            { Target = new("gone", "one") }), LyricContentEditErrorCode.TargetStale);
        AssertRejected(workspace, () => LyricContentEditUseCase.AddLineAfter(workspace,
            new(workspace, workspace.Generation, "gone", null)), LyricContentEditErrorCode.TargetStale);
    }

    internal static Project Sample()
    {
        LyricLine line = LyricLine.Create("one", "甲👨‍👩‍👧‍👦é乙", TimeRange.Create(1_000_007, 5_000_009), TimingStatus.Imported,
            [LyricToken.Create("first", "甲", TimeRange.Create(1_000_007, 1_200_011), TimingStatus.Imported) with
                { StyleOverride = new() { SungColor = RgbaColor.Black } },
             LyricToken.Create("emoji", "👨‍👩‍👧‍👦", TimeRange.Create(2_000_003, 2_500_009), TimingStatus.Imported),
             LyricToken.Create("combining", "é", TimeRange.Create(3_000_003, 3_500_009), TimingStatus.Estimated),
             LyricToken.Create("last", "乙", TimeRange.Create(4_000_003, 4_500_009), TimingStatus.Imported)]) with
        {
            StyleOverride = new() { FontSizePx = 47 },
            RubyAnnotations = [RubyAnnotation.Create("ruby-first", "first", "こう", "ja", .7, 3, RubyAlignment.Start)],
        };
        return Project.Create("正文", lyrics: LyricsDocument.Create([line, LyricLine.Create("two", "丙") ]));
    }

    internal static LyricLineTextEditRequest TextRequest(ProjectWorkspace workspace, string? text, string lineId = "one") =>
        new(workspace, workspace.Generation, new("primary", lineId), workspace.Project.Lyrics.Lines.First(line => line.Id == lineId).Text, text);

    internal static LyricRubyTextEditRequest RubyRequest(ProjectWorkspace workspace, string tokenId, string text) =>
        new(workspace, workspace.Generation, new("primary", "one", tokenId), workspace.Project.Lyrics.Lines.First(line => line.Id == "one")
            .RubyAnnotations.FirstOrDefault(ruby => ruby.BaseTokenId == tokenId), text);

    internal static LyricContentEditBaseline Baseline(ProjectWorkspace workspace, string lineId = "one", string branchId = "primary") =>
        new(workspace, workspace.Generation, new(branchId, lineId), workspace.Project.Lyrics.Branches.First(branch => branch.Id == branchId)
            .Lines.First(line => line.Id == lineId));

    internal static void AssertRejected(ProjectWorkspace workspace, Func<LyricContentEditResult> action, LyricContentEditErrorCode code)
    {
        Project source = workspace.Project;
        int undo = workspace.History.UndoCount, redo = workspace.History.RedoCount;
        bool dirty = workspace.IsDirty;
        var result = action();
        Assert.False(result.Accepted); Assert.False(result.Changed);
        Assert.Equal(code, result.Error?.Code);
        Assert.Same(source, result.Project); Assert.Same(source, workspace.Project);
        Assert.Equal(undo, workspace.History.UndoCount); Assert.Equal(redo, workspace.History.RedoCount);
        Assert.Equal(dirty, workspace.IsDirty);
    }
}
