using System.Collections.Immutable;
using MakoKaraGenerator.Application.Editing;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using static MakoKaraGenerator.Application.Tests.LyricContentEditUseCaseTests;

namespace MakoKaraGenerator.Application.Tests;

public sealed class ProjectLyricsChangeSetTests
{
    [Fact]
    public void 正文注音元素与时间合并摘要保留稳定行并失效预览()
    {
        Project source = Sample();
        LyricLine line = source.Lyrics.Lines[0];
        Project after = source with { Lyrics = source.Lyrics with { Lines = source.Lyrics.Lines.SetItem(0, line with
        {
            Text = "新的", Time = TimeRange.Create(1_000_000, 6_000_000), TimingStatus = TimingStatus.Edited,
            RubyAnnotations = [line.RubyAnnotations[0] with { Text = "新的注音" }],
            Tokens = line.Tokens.SetItem(0, line.Tokens[0] with { Text = "新", TimingStatus = TimingStatus.Edited }),
        }) } };
        var changes = LyricContentEditUseCase.DescribeChanges(source, after);
        Assert.False(changes.FullRefreshRequired); Assert.True(changes.HasChanges); Assert.True(changes.InvalidatesPreview);
        Assert.Equal(new LyricLineChange("primary", "one", LyricLineChangeKind.Text | LyricLineChangeKind.Ruby
            | LyricLineChangeKind.Tokens | LyricLineChangeKind.Timing), Assert.Single(changes.Rows));
        Assert.Empty(changes.StructuralBranchIds);
        Assert.False(changes.PresentationChanged);
    }

    [Fact]
    public void 正文重切撤销的两个方向都描述正文及元素并跳过未变行()
    {
        Project source = Sample();
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(source);
        var result = LyricContentEditUseCase.SetLineText(workspace, TextRequest(workspace, "甲X👨‍👩‍👧‍👦é乙"));
        Assert.True(result.Accepted);
        foreach (var changes in new[] { result.ChangeSet, LyricContentEditUseCase.DescribeChanges(result.Project, source) })
        {
            Assert.Equal("one", Assert.Single(changes.Rows).LineId);
            Assert.Equal(LyricLineChangeKind.Text | LyricLineChangeKind.Tokens, changes.Rows[0].Kind);
            Assert.False(changes.FullRefreshRequired); Assert.Empty(changes.StructuralBranchIds);
        }
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void 行插入删除或重排仅标记对应结构来源(int operation)
    {
        Project source = Sample();
        var translation = LyricBranch.Create("translation", "译文", LyricBranchRole.Translation, [LyricLine.Create("translated", "译")]);
        source = source with { Lyrics = source.Lyrics with { Branches = source.Lyrics.Branches.Add(translation) } };
        var lines = source.Lyrics.Lines;
        var changed = operation switch
        {
            0 => lines.Add(LyricLine.Create("three", "丁")),
            1 => lines.RemoveAt(0),
            _ => lines.Reverse().ToImmutableArray(),
        };
        var after = source with { Lyrics = source.Lyrics with { Lines = changed } };
        var changes = LyricContentEditUseCase.DescribeChanges(source, after);
        Assert.Equal("primary", Assert.Single(changes.StructuralBranchIds));
        Assert.Empty(changes.Rows); Assert.False(changes.FullRefreshRequired);
        Assert.Same(translation, after.Lyrics.Branches[1]);
    }

    [Fact]
    public void 展示引用独立变化由既有展示出口刷新且相等克隆无实际变化()
    {
        Project source = Sample();
        Project after = source with { LyricPresentation = source.LyricPresentation with { LineOverrides = [
            new() { BranchId = "primary", LineId = "one", LocalTransform = LyricLocalTransform.Neutral }] } };
        var changes = LyricContentEditUseCase.DescribeChanges(source, after);
        Assert.True(changes.PresentationChanged); Assert.True(changes.HasChanges);
        Assert.Empty(changes.Rows); Assert.Empty(changes.StructuralBranchIds); Assert.False(changes.FullRefreshRequired);
        Assert.Same(ProjectLyricsChangeSet.None, LyricContentEditUseCase.DescribeChanges(source, source));
        var equivalent = source with { Lyrics = source.Lyrics with { Lines = source.Lyrics.Lines.Select(line => line with { }).ToImmutableArray() } };
        Assert.Same(ProjectLyricsChangeSet.None, LyricContentEditUseCase.DescribeChanges(source, equivalent));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public void 未知工程来源行或元素字段变化保守完整刷新(int field)
    {
        Project source = Sample();
        var line = source.Lyrics.Lines[0];
        Project after = field switch
        {
            0 => source with { Name = "其他工程字段" },
            1 => source with { Lyrics = source.Lyrics with { SchemaVersion = -1 } },
            2 => source with { Lyrics = source.Lyrics with { Branches = source.Lyrics.Branches.SetItem(0, source.Lyrics.Branches[0] with { Name = "别名" }) } },
            3 => source with { Lyrics = source.Lyrics with { Lines = source.Lyrics.Lines.SetItem(0, line with { PrimaryLineId = "another" }) } },
            4 => source with { Lyrics = source.Lyrics with { Lines = source.Lyrics.Lines.SetItem(0, line with { StyleOverride = new() { Opacity = .5f } }) } },
            _ => source with { Lyrics = source.Lyrics with { Lines = source.Lyrics.Lines.SetItem(0, line with
                { Tokens = line.Tokens.SetItem(0, line.Tokens[0] with { StyleOverride = new() { Opacity = .5f } }) }) } },
        };
        Assert.Same(ProjectLyricsChangeSet.FullRefresh, LyricContentEditUseCase.DescribeChanges(source, after));
        Assert.True(ProjectLyricsChangeSet.FullRefresh.InvalidatesPreview);
    }

    [Fact]
    public void 元素结构变化不能掩盖保留元素的未知属性变化()
    {
        Project source = Sample();
        var line = source.Lyrics.Lines[0];
        var after = source with { Lyrics = source.Lyrics with { Lines = source.Lyrics.Lines.SetItem(0, line with
        {
            Tokens = line.Tokens.SetItem(0, line.Tokens[0] with { StyleOverride = new() { Opacity = .5f } })
                .Add(LyricToken.Create("new-token", "新")),
        }) } };
        Assert.Same(ProjectLyricsChangeSet.FullRefresh, LyricContentEditUseCase.DescribeChanges(source, after));
    }
}
