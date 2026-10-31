using MakoKaraGenerator.Application.Editing;
using MakoKaraGenerator.Core.Commands;
using MakoKaraGenerator.Core.Diagnostics;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Core.Validation;
using MakoKaraGenerator.TestSupport;
using static MakoKaraGenerator.Application.Tests.LyricContentEditUseCaseTests;

namespace MakoKaraGenerator.Application.Tests;

public sealed class LyricLineOperationUseCaseTests
{
    [Fact]
    public void 复制精确大整数时间及属性并映射注音且邻行冲突真实保留()
    {
        const long Start = 9_007_199_254_740_993;
        LyricLine line = LyricLine.Create("one", "甲", TimeRange.Create(Start, Start + 1_000_003), TimingStatus.Imported,
            [LyricToken.Create("base", "甲", TimeRange.Create(Start + 7, Start + 900_009), TimingStatus.Estimated) with
                { StyleOverride = new() { UnsungColor = RgbaColor.Black } }]) with
        {
            StyleOverride = new() { FontSizePx = 47 },
            RubyAnnotations = [RubyAnnotation.Create("ruby", "base", "こう", "ja", .7, 3),
                RubyAnnotation.Create("orphan-ruby", "missing", "孤")],
        };
        var next = LyricLine.Create("two", "乙", TimeRange.Create(Start + 1_500_000, Start + 3_000_000), TimingStatus.Imported);
        Project source = Project.Create("精确复制", lyrics: LyricsDocument.Create([line, next]));
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(source);
        int notifications = 0;
        workspace.History.Changed += (_, _) => notifications++;

        var result = LyricContentEditUseCase.DuplicateLine(workspace, Baseline(workspace));
        Assert.True(result.Accepted);
        Assert.True(result.OverlapsNext);
        LyricLine copy = result.Project.Lyrics.Lines[1];
        Assert.Equal(TimeRange.Create(Start + 1_000_003, Start + 2_000_006), copy.Time);
        Assert.Equal(TimeRange.Create(Start + 1_000_010, Start + 1_900_012), copy.Tokens[0].Time);
        Assert.Equal(TimingStatus.Estimated, copy.Tokens[0].TimingStatus);
        Assert.Same(line.StyleOverride, copy.StyleOverride);
        Assert.Same(line.Tokens[0].StyleOverride, copy.Tokens[0].StyleOverride);
        Assert.NotEqual(line.Id, copy.Id);
        Assert.NotEqual(line.Tokens[0].Id, copy.Tokens[0].Id);
        Assert.Equal(copy.Tokens[0].Id, copy.RubyAnnotations[0].BaseTokenId);
        Assert.Equal("missing", copy.RubyAnnotations[1].BaseTokenId);
        RubyAnnotation sourceRuby = line.RubyAnnotations[0];
        RubyAnnotation copiedRuby = copy.RubyAnnotations[0];
        Assert.Equal(sourceRuby.Text, copiedRuby.Text);
        Assert.Equal(sourceRuby.Language, copiedRuby.Language);
        Assert.Equal(sourceRuby.RelativeScale, copiedRuby.RelativeScale);
        Assert.Equal(sourceRuby.GapPx, copiedRuby.GapPx);
        Assert.Equal(sourceRuby.Alignment, copiedRuby.Alignment);
        Assert.Equal(copiedRuby.Segments.Length, sourceRuby.Segments.Length);
        Assert.Equal(sourceRuby.Segments.Select(segment => segment.Text), copiedRuby.Segments.Select(segment => segment.Text));
        Assert.Equal(sourceRuby.Segments.Select(segment => segment.Time), copiedRuby.Segments.Select(segment => segment.Time));
        Assert.All(copiedRuby.Segments, segment => Assert.DoesNotContain(segment.Id,
            sourceRuby.Segments.Select(sourceSegment => sourceSegment.Id)));
        Assert.Same(line, result.Project.Lyrics.Lines[0]);
        Assert.Same(next, result.Project.Lyrics.Lines[2]);
        Assert.Equal(copy.Id, result.CreatedLineId);
        Assert.Equal(copy.Id, result.SelectedLineId);
        Assert.Equal(1, result.InsertedIndex);
        Assert.Equal("primary", Assert.Single(result.ChangeSet.StructuralBranchIds));
        Assert.Equal(1, workspace.History.UndoCount);
        Assert.Equal(1, notifications);
        Assert.Equal("lyrics.duplicate-line", workspace.UndoName);
        Assert.Same(source, workspace.Undo());
        Assert.Same(result.Project, workspace.Redo());
        Assert.Equal(3, notifications);
    }

    [Fact]
    public void 连续复制含注音行的全局标识无碰撞并且每次分别撤销()
    {
        Project source = Sample();
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(source);
        var first = LyricContentEditUseCase.DuplicateLine(workspace, Baseline(workspace));
        var second = LyricContentEditUseCase.DuplicateLine(workspace, Baseline(workspace));
        Assert.True(first.Accepted); Assert.True(second.Accepted);
        Assert.NotEqual(first.CreatedLineId, second.CreatedLineId);
        var ids = second.Project.Lyrics.Branches.Select(branch => branch.Id).Concat(second.Project.Lyrics.AllLines
            .SelectMany(line => new[] { line.Id }
                .Concat(line.Tokens.Select(token => token.Id))
                .Concat(line.RubyAnnotations.SelectMany(ruby => new[] { ruby.Id }.Concat(ruby.Segments.Select(segment => segment.Id)))))).ToArray();
        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.DoesNotContain(ProjectValidator.Validate(second.Project, ValidationContext.Open).Issues,
            issue => issue.Code == DiagnosticCode.DuplicateIdentifier);
        Assert.Equal(2, workspace.History.UndoCount);
        Assert.Same(first.Project, workspace.Undo());
        Assert.Same(source, workspace.Undo());
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void 复制缺失或非法时间不伪造合法定时(bool invalid)
    {
        TimeRange? time = invalid ? new(9, 2) : null;
        TimingStatus status = invalid ? TimingStatus.Invalid : TimingStatus.Missing;
        var line = LyricLine.Create("one", "甲", time, status, [LyricToken.Create("base", "甲", time, status)]);
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(Project.Create("真实状态", lyrics: LyricsDocument.Create([line])));
        var result = LyricContentEditUseCase.DuplicateLine(workspace, Baseline(workspace));
        Assert.True(result.Accepted);
        Assert.Equal(time, result.Project.Lyrics.Lines[1].Time);
        Assert.Equal(status, result.Project.Lyrics.Lines[1].TimingStatus);
        Assert.Equal(time, result.Project.Lyrics.Lines[1].Tokens[0].Time);
        Assert.False(result.OverlapsNext);
    }

    [Fact]
    public void 非主来源复制和最后一行删除保持主来源且不重绑FollowPrimary()
    {
        Project source = Sample();
        var translated = LyricLine.Create("translated", "译") with { PrimaryLineId = "one" };
        source = source with { Lyrics = source.Lyrics with { Branches = source.Lyrics.Branches.Add(
            LyricBranch.Create("translation", "译文", LyricBranchRole.Translation, [translated], timingMode: LyricBranchTimingMode.FollowPrimary)) } };
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(source);
        var copy = LyricContentEditUseCase.DuplicateLine(workspace, Baseline(workspace, "translated", "translation"));
        Assert.True(copy.Accepted);
        Assert.Equal("one", copy.Project.Lyrics.Branches[1].Lines[1].PrimaryLineId);
        Assert.Null(copy.Project.Lyrics.Branches[1].Lines[1].Time);
        Assert.True(LyricContentEditUseCase.RemoveLine(workspace, Baseline(workspace, copy.CreatedLineId!, "translation")).Accepted);
        var removed = LyricContentEditUseCase.RemoveLine(workspace, Baseline(workspace, "translated", "translation"));
        Assert.True(removed.Accepted);
        Assert.Null(removed.SelectedLineId);
        Assert.Empty(removed.Project.Lyrics.Branches[1].Lines);
        Assert.Equal("primary", removed.Project.Lyrics.PrimaryBranchId);
        Assert.Same(source.Lyrics.PrimaryBranch, removed.Project.Lyrics.PrimaryBranch);
    }

    [Fact]
    public void 删除原行清理独占剪辑和覆盖但保留其他来源关联警告并可恢复()
    {
        Project source = ExclusiveDisplayFixture.Create();
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(source);
        var result = LyricContentEditUseCase.RemoveLine(workspace, Baseline(workspace, "line-2"));
        Assert.True(result.Accepted);
        Assert.Empty(result.Project.LyricPresentation.Programs[0].Clips);
        Assert.Null(result.Project.LyricPresentation.FindLine("line-2"));
        Assert.Same(source.Lyrics.Branches[1], result.Project.Lyrics.Branches[1]);
        Assert.Equal("line-2", result.Project.Lyrics.Branches[1].Lines[2].PrimaryLineId);
        Assert.Contains(ProjectValidator.Validate(result.Project, ValidationContext.Open).Issues,
            issue => issue.Code == DiagnosticCode.MissingLyricAssociation && issue.Severity == IssueSeverity.Warning);
        Assert.True(result.ChangeSet.PresentationChanged);
        Assert.Equal("line-3", result.SelectedLineId);
        Assert.Equal(1, workspace.History.UndoCount);
        Assert.Same(source, workspace.Undo());
        Assert.Same(result.Project, workspace.Redo());
    }

    [Fact]
    public void 复制同时复制内部展示覆盖且删除只清理副本引用()
    {
        Project source = ExclusiveDisplayFixture.Create();
        source = source with { LyricPresentation = source.LyricPresentation with
            { Programs = source.LyricPresentation.Programs.SetItem(0, source.LyricPresentation.Programs[0] with { Clips = [] }) } };
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(source);
        var result = LyricContentEditUseCase.DuplicateLine(workspace, Baseline(workspace, "line-2"));
        Assert.True(result.Accepted);
        Assert.Equal(source.LyricPresentation.FindLine("line-2")! with { LineId = result.CreatedLineId! },
            result.Project.LyricPresentation.FindLine(result.CreatedLineId!));
        Assert.True(result.ChangeSet.PresentationChanged);
        Assert.True(LyricContentEditUseCase.RemoveLine(workspace, Baseline(workspace, result.CreatedLineId!)).Accepted);
        Assert.NotNull(workspace.Project.LyricPresentation.FindLine("line-2"));
        Assert.Null(workspace.Project.LyricPresentation.FindLine(result.CreatedLineId!));
        Assert.Equal(2, workspace.History.UndoCount);
        Assert.Same(result.Project, workspace.Undo());
    }

    [Fact]
    public void 复制若进入独占显示区整笔拒绝且不丢已有重做()
    {
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(ExclusiveDisplayFixture.Create());
        workspace.Execute(ProjectCommand.Create("name", project => project with { Name = "临时" }));
        workspace.Undo();
        AssertRejected(workspace, () => LyricContentEditUseCase.DuplicateLine(workspace, Baseline(workspace, "line-1")),
            LyricContentEditErrorCode.DomainValidationFailed);
        Assert.Equal(1, workspace.History.RedoCount);
    }

    [Fact]
    public void 插入稳定前一行不用旧下标且新增始终未定时空行()
    {
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(Sample());
        var baseline = Baseline(workspace);
        var request = new LyricLineInsertRequest(workspace, workspace.Generation, "primary", "one", baseline.ExpectedLine);
        workspace.Execute(ProjectCommand.Create("order", project => project with
            { Lyrics = project.Lyrics with { Lines = [project.Lyrics.Lines[1], project.Lyrics.Lines[0]] } }));
        var result = LyricContentEditUseCase.AddLineAfter(workspace, request);
        Assert.True(result.Accepted);
        Assert.Equal(2, result.InsertedIndex);
        LyricLine inserted = result.Project.Lyrics.Lines[2];
        Assert.Equal(inserted.Id, result.SelectedLineId);
        Assert.Empty(inserted.Text); Assert.Empty(inserted.Tokens);
        Assert.Null(inserted.Time); Assert.Equal(TimingStatus.Missing, inserted.TimingStatus);
        Assert.Equal("one", result.Project.Lyrics.Lines[1].Id);
        Assert.Equal("lyrics.add-line", workspace.UndoName);
    }

    [Fact]
    public void 无选择可追加到空来源而不改变其他来源()
    {
        Project source = Sample();
        source = source with { Lyrics = source.Lyrics with { Branches = source.Lyrics.Branches.Add(
            LyricBranch.Create("empty", "空译文", LyricBranchRole.Translation, timingMode: LyricBranchTimingMode.FollowPrimary)) } };
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(source);
        var result = LyricContentEditUseCase.AddLineAfter(workspace, new(workspace, workspace.Generation, "empty", null));
        Assert.True(result.Accepted); Assert.Equal(0, result.InsertedIndex);
        Assert.Same(source.Lyrics.PrimaryBranch, result.Project.Lyrics.PrimaryBranch);
        Assert.Equal("primary", result.Project.Lyrics.PrimaryBranchId);
        Assert.Null(result.Project.Lyrics.Branches[1].Lines[0].PrimaryLineId);
        Assert.Equal("empty", Assert.Single(result.ChangeSet.StructuralBranchIds));
        Assert.Same(source, workspace.Undo());
    }

    [Theory]
    [InlineData(0, "two")] [InlineData(1, "three")] [InlineData(2, "two")]
    public void 删除后选择沿原位置下一行或末尾剩余行(int index, string selected)
    {
        Project source = Sample();
        source = source with { Lyrics = source.Lyrics with { Lines = source.Lyrics.Lines.Add(LyricLine.Create("three", "丁")) } };
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(source);
        string removedId = source.Lyrics.Lines[index].Id;
        var result = LyricContentEditUseCase.RemoveLine(workspace, Baseline(workspace, removedId));
        Assert.True(result.Accepted);
        Assert.Equal(selected, result.SelectedLineId);
        Assert.DoesNotContain(result.Project.Lyrics.Lines, line => line.Id == removedId);
        Assert.Equal(1, workspace.History.UndoCount);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void 结构操作拒绝旧完整行而不消费其他行或增加历史(int operation)
    {
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(Sample());
        var baseline = Baseline(workspace);
        Assert.True(LyricContentEditUseCase.SetRubyText(workspace, RubyRequest(workspace, "first", "变更")).Accepted);
        AssertRejected(workspace, () => operation switch
        {
            0 => LyricContentEditUseCase.AddLineAfter(workspace, new(workspace, workspace.Generation, "primary", "one", baseline.ExpectedLine)),
            1 => LyricContentEditUseCase.DuplicateLine(workspace, baseline),
            _ => LyricContentEditUseCase.RemoveLine(workspace, baseline),
        }, LyricContentEditErrorCode.TargetStale);
    }

    [Fact]
    public void 已有展示结构错误拒绝新增且未提交新行()
    {
        Project source = ExclusiveDisplayFixture.Create();
        source = source with { LyricPresentation = source.LyricPresentation with
            { Programs = source.LyricPresentation.Programs.SetItem(0, source.LyricPresentation.Programs[0] with { RootContainerId = "missing" }) } };
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(source);
        AssertRejected(workspace, () => LyricContentEditUseCase.AddLineAfter(workspace,
            new(workspace, workspace.Generation, "primary", null)), LyricContentEditErrorCode.DomainValidationFailed);
    }
}
