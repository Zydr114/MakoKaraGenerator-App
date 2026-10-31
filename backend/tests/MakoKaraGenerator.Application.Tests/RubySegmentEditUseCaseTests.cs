using System.Collections.Immutable;
using MakoKaraGenerator.Application.Editing;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;

namespace MakoKaraGenerator.Application.Tests;

public sealed class RubySegmentEditUseCaseTests
{
    private static readonly RubyEditTarget Target = new("primary", "line", "token", "ruby");
    [Fact]
    public void 独立片段时间允许超行重叠并保持基础元素及其它片段且准备无副作用()
    {
        var workspace = Workspace(); var source = workspace.Project;
        var prepared = RubySegmentEditUseCase.PrepareSetTime(workspace, Baseline(workspace), "a", 4_000_001, 5_000_007);
        Assert.Same(source, workspace.Project); Assert.Equal(0, workspace.History.UndoCount);
        var result = RubySegmentEditUseCase.Commit(workspace, prepared);
        Assert.True(result.Accepted); Assert.True(result.Changed); Assert.Equal(1, workspace.History.UndoCount);
        var ruby = RubySegmentEditUseCase.Read(result.Project, Target)!;
        Assert.Equal(new TimeRange(4_000_001, 5_000_007), ruby.Segments[0].Time);
        Assert.Equal(TimingStatus.Edited, ruby.Segments[0].TimingStatus);
        Assert.Same(source.Lyrics.Lines[0].Tokens[0], result.Project.Lyrics.Lines[0].Tokens[0]);
        Assert.Same(source.Lyrics.Lines[0].RubyAnnotations[0].Segments[1], ruby.Segments[1]);
        Assert.Same(source, workspace.Undo()); Assert.Same(result.Project, workspace.Redo());
    }
    [Fact]
    public void 正文新增顺序删除同步拼接并保留各片段时间与属性()
    {
        var workspace = Workspace();
        Commit(RubySegmentEditUseCase.PrepareSetText(workspace, Baseline(workspace), "a", "きゃ"));
        Assert.Equal("きゃん", Baseline(workspace).Ruby.Text);
        Assert.Equal(new TimeRange(100, 200), Baseline(workspace).Ruby.Segments[0].Time);
        var added = Commit(RubySegmentEditUseCase.PrepareAdd(workspace, Baseline(workspace), "っ", "a"));
        string id = added.CreatedSegmentId!; Assert.Equal(id, added.SelectedSegmentId);
        Assert.Equal("きゃっん", Baseline(workspace).Ruby.Text); Assert.Null(Baseline(workspace).Ruby.Segments[1].Time);
        Commit(RubySegmentEditUseCase.PrepareReorder(workspace, Baseline(workspace), ["b", id, "a"]));
        Assert.Equal("んっきゃ", Baseline(workspace).Ruby.Text); Assert.Equal(new TimeRange(100, 200), Baseline(workspace).Ruby.Segments[2].Time);
        Commit(RubySegmentEditUseCase.PrepareRemove(workspace, Baseline(workspace), id));
        Commit(RubySegmentEditUseCase.PrepareRemove(workspace, Baseline(workspace), "b"));
        Assert.Null(Commit(RubySegmentEditUseCase.PrepareRemove(workspace, Baseline(workspace), "a")).SelectedSegmentId);
        Assert.Empty(workspace.Project.Lyrics.Lines[0].RubyAnnotations);
        Assert.Equal(6, workspace.History.UndoCount); workspace.Undo(); Assert.Single(workspace.Project.Lyrics.Lines[0].RubyAnnotations);
        RubySegmentEditResult Commit(PreparedRubySegmentEdit prepared) { var result = RubySegmentEditUseCase.Commit(workspace, prepared); Assert.True(result.Accepted, result.Error.ToString()); return result; }
    }
    [Fact]
    public void 同值和重复清除保留redo且独立请求不合并()
    {
        var workspace = Workspace();
        RubySegmentEditUseCase.Commit(workspace, RubySegmentEditUseCase.PrepareClearTime(workspace, Baseline(workspace), "a"));
        RubySegmentEditUseCase.Commit(workspace, RubySegmentEditUseCase.PrepareSetText(workspace, Baseline(workspace), "b", "新"));
        workspace.Undo(); var source = workspace.Project;
        var result = RubySegmentEditUseCase.Commit(workspace, RubySegmentEditUseCase.PrepareClearTime(workspace, Baseline(workspace), "a"));
        Assert.False(result.Changed); Assert.Same(source, workspace.Project); Assert.Equal(1, workspace.History.RedoCount);
        workspace.Redo(); Assert.Equal("せ新", Baseline(workspace).Ruby.Text);
    }
    [Fact]
    public void 批次重复无效端点和失效目标全部原子拒绝()
    {
        var workspace = Workspace(); var source = workspace.Project; var baseline = Baseline(workspace);
        ImmutableArray<RubySegmentTimeEdit>[] batches = [
            [new(baseline,"a",1,9),new(baseline,"b",10,10)],
            [new(baseline,"a",1,9),new(baseline,"a",10,20)],
            [new(baseline,"a",1,9),new(baseline,"missing",10,20)],
            [new(baseline,"a",-1,9)]];
        foreach (var batch in batches)
        { Assert.False(RubySegmentEditUseCase.PrepareBatch(workspace, batch).Result.Accepted); Assert.Same(source, workspace.Project); }
        Assert.Equal(0, workspace.History.UndoCount);
        var success = RubySegmentEditUseCase.Commit(workspace, RubySegmentEditUseCase.PrepareBatch(workspace, [new(baseline,"a",10,30),new(baseline,"b",20,40)]));
        Assert.True(success.Accepted); Assert.Equal(2, success.ChangedSegmentCount); Assert.Equal(1, workspace.History.UndoCount);
    }
    [Fact]
    public void 准备后工程变化及错误父链和工作区代次拒绝旧候选()
    {
        var workspace = Workspace(); var baseline = Baseline(workspace);
        var prepared = RubySegmentEditUseCase.PrepareClearTime(workspace, baseline, "a");
        RubySegmentEditUseCase.Commit(workspace, RubySegmentEditUseCase.PrepareSetText(workspace, baseline, "b", "新"));
        Assert.Equal(RubySegmentEditErrorCode.TargetStale, RubySegmentEditUseCase.Commit(workspace, prepared).Error);
        Assert.Equal(RubySegmentEditErrorCode.WorkspaceStale, RubySegmentEditUseCase.Commit(Workspace(), prepared).Error);
        Assert.Null(RubySegmentEditUseCase.Read(workspace.Project, Target with { TokenId = "missing" }));
        Assert.Null(RubySegmentEditUseCase.Read(workspace.Project, Target with { RubyId = "missing" }));
    }
    [Fact]
    public void 旧整段注音路径拒绝丢弃已计时或多段数据而同正文保持noop()
    {
        var workspace = Workspace(); var ruby = Baseline(workspace).Ruby;
        var request = new LyricRubyTextEditRequest(workspace, workspace.Generation, new("primary","line","token"), ruby,"新");
        Assert.Equal(LyricContentEditErrorCode.RubySegmentsRequireExplicitEdit, LyricContentEditUseCase.SetRubyText(workspace, request).Error!.Code);
        Assert.False(LyricContentEditUseCase.SetRubyText(workspace, request with { Text = ruby.Text }).Changed);
        Assert.Same(ruby, Baseline(workspace).Ruby); Assert.Equal(0, workspace.History.UndoCount);
    }
    [Fact]
    public void 空正文与不完整或重复顺序拒绝()
    {
        var workspace = Workspace(); var baseline = Baseline(workspace);
        Assert.Equal(RubySegmentEditErrorCode.EmptyText, RubySegmentEditUseCase.PrepareSetText(workspace, baseline,"a", "").Result.Error);
        Assert.Equal(RubySegmentEditErrorCode.EmptyText, RubySegmentEditUseCase.PrepareAdd(workspace, baseline, "", null).Result.Error);
        Assert.Equal(RubySegmentEditErrorCode.InvalidOrder, RubySegmentEditUseCase.PrepareReorder(workspace, baseline,["a","a"]).Result.Error);
        Assert.Equal(RubySegmentEditErrorCode.InvalidOrder, RubySegmentEditUseCase.PrepareReorder(workspace, baseline,["a"]).Result.Error);
    }

    [Fact]
    public void 同值时间正文和顺序均保留来源状态redo及原引用()
    {
        var workspace = Workspace();
        RubySegmentEditUseCase.Commit(workspace, RubySegmentEditUseCase.PrepareSetText(workspace, Baseline(workspace), "b", "新")); workspace.Undo();
        var source = workspace.Project;
        PreparedRubySegmentEdit[] noOps = [RubySegmentEditUseCase.PrepareSetTime(workspace, Baseline(workspace), "a", 100, 200),
            RubySegmentEditUseCase.PrepareSetText(workspace, Baseline(workspace), "a", "せ"), RubySegmentEditUseCase.PrepareReorder(workspace, Baseline(workspace), ["a", "b"])];
        foreach (var noOp in noOps)
        {
            var result = RubySegmentEditUseCase.Commit(workspace, noOp);
            Assert.True(result.Accepted); Assert.False(result.Changed); Assert.Same(source, result.Project); Assert.Equal(1, workspace.History.RedoCount);
            Assert.Equal(TimingStatus.Imported, Baseline(workspace).Ruby.Segments[0].TimingStatus);
        }
    }
    [Fact]
    public void 跨组批次精确保留大于JS整数上限的端点并一次撤销恢复()
    {
        var workspace = Workspace(); var source = workspace.Project; var line = source.Lyrics.Lines[0];
        var other = line with { Id = "other-line", Tokens = [line.Tokens[0] with { Id = "other-token" }], RubyAnnotations = [line.RubyAnnotations[0] with { Id = "other-ruby", BaseTokenId = "other-token", Segments = [RubyTextSegment.Create("other-segment", "せん")] }] };
        workspace.Replace(source with { Lyrics = LyricsDocument.Create([line, other]) }); source = workspace.Project;
        var target = new RubyEditTarget("primary", "other-line", "other-token", "other-ruby");
        var otherBaseline = new RubySegmentEditBaseline(workspace, workspace.Generation, target, RubySegmentEditUseCase.Read(source, target)!);
        const long start = 9_007_199_254_740_993;
        var prepared = RubySegmentEditUseCase.PrepareBatch(workspace, [new(Baseline(workspace), "a", start, start + 100), new(otherBaseline, "other-segment", start + 10, start + 200)]);
        Assert.Same(source, workspace.Project);
        var result = RubySegmentEditUseCase.Commit(workspace, prepared); Assert.True(result.Accepted); Assert.Equal(2, result.ChangedSegmentCount); Assert.Equal(1, workspace.History.UndoCount);
        Assert.Equal(new TimeRange(start + 10, start + 200), RubySegmentEditUseCase.Read(result.Project, target)!.Segments[0].Time);
        Assert.Same(source, workspace.Undo()); Assert.Same(result.Project, workspace.Redo());
        Assert.Equal(RubySegmentEditErrorCode.InvalidBatch, RubySegmentEditUseCase.PrepareBatch(workspace, []).Result.Error);
    }
    [Fact]
    public void 旧整段路径分别保护单计时片段与多未计时片段并允许旧兼容片段更新()
    {
        foreach (bool timed in new[] { false, true })
        {
            var workspace = Workspace(); var line = workspace.Project.Lyrics.Lines[0]; var ruby = line.RubyAnnotations[0];
            ruby = timed ? ruby with { Text = "せん", Segments = [RubyTextSegment.Create("a", "せん", new(10, 20), TimingStatus.Imported)] }
                : ruby with { Segments = [RubyTextSegment.Create("a", "せ"), RubyTextSegment.Create("b", "ん")] };
            workspace.Replace(workspace.Project with { Lyrics = LyricsDocument.Create([line with { RubyAnnotations = [ruby] }]) });
            var request = new LyricRubyTextEditRequest(workspace, workspace.Generation, new("primary", "line", "token"), ruby, "");
            Assert.Equal(LyricContentEditErrorCode.RubySegmentsRequireExplicitEdit, LyricContentEditUseCase.SetRubyText(workspace, request).Error!.Code);
            Assert.False(LyricContentEditUseCase.SetRubyText(workspace, request with { Text = ruby.Text }).Changed);
            Assert.Same(ruby, Baseline(workspace).Ruby);
        }
        var compatible = Workspace(); var oldLine = compatible.Project.Lyrics.Lines[0]; var oldRuby = RubyAnnotation.Create("ruby", "token", "せん");
        compatible.Replace(compatible.Project with { Lyrics = LyricsDocument.Create([oldLine with { RubyAnnotations = [oldRuby] }]) });
        var success = LyricContentEditUseCase.SetRubyText(compatible, new(compatible, compatible.Generation, new("primary", "line", "token"), oldRuby, "新"));
        Assert.True(success.Accepted); Assert.Single(Baseline(compatible).Ruby.Segments); Assert.Null(Baseline(compatible).Ruby.Segments[0].Time);
    }
    [Fact]
    public void 拆分和重切保留仍存在基础身份的完整片段且撤销恢复()
    {
        var workspace = Workspace(); var oldLine = workspace.Project.Lyrics.Lines[0]; var ruby = oldLine.RubyAnnotations[0];
        workspace.Replace(workspace.Project with { Lyrics = LyricsDocument.Create([oldLine with { Text = "歌声", Tokens = [oldLine.Tokens[0] with { Text = "歌声" }] }]) });
        oldLine = workspace.Project.Lyrics.Lines[0];
        var split = LyricTokenEditUseCase.SplitToken(workspace, new(workspace, workspace.Generation, new("primary", "line", "token"), oldLine));
        Assert.True(split.Accepted); Assert.Same(ruby, Assert.Single(workspace.Project.Lyrics.Lines[0].RubyAnnotations));
        workspace.Undo();
        var resplit = LyricTokenEditUseCase.ResplitLine(workspace, new(new(workspace, workspace.Generation, new("primary", "line"), oldLine), ["歌", "声"], false));
        Assert.True(resplit.Accepted); Assert.Same(ruby, Assert.Single(resplit.Project.Lyrics.Lines[0].RubyAnnotations));
        Assert.Same(ruby, Assert.Single(workspace.Undo().Lyrics.Lines[0].RubyAnnotations));
    }
    private static RubySegmentEditBaseline Baseline(ProjectWorkspace workspace) => new(workspace,workspace.Generation,Target,RubySegmentEditUseCase.Read(workspace.Project,Target)!);
    private static ProjectWorkspace Workspace()
    {
        var ruby = RubyAnnotation.Create("ruby","token","せん","ja", .7, 3, RubyAlignment.End) with { Segments = [
            RubyTextSegment.Create("a","せ",new(100,200),TimingStatus.Imported), RubyTextSegment.Create("b","ん",new(250,300),TimingStatus.Confirmed)] };
        var line = LyricLine.Create("line","歌",new(0,1000),TimingStatus.Imported,[LyricToken.Create("token","歌",new(0,1000),TimingStatus.Imported)]) with { RubyAnnotations=[ruby] };
        return ProjectWorkspace.Adopt(Project.Create("ruby",lyrics:LyricsDocument.Create([line])));
    }
}
