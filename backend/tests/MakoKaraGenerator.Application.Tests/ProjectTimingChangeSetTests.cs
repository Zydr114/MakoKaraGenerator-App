using MakoKaraGenerator.Application.Editing;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;

namespace MakoKaraGenerator.Application.Tests;

public sealed class ProjectTimingChangeSetTests
{
    [Fact]
    public void 时间提交撤销重做摘要标记同一个稳定目标且全部使预览失效()
    {
        Project before = LyricTimingEditUseCaseTests.Sample();
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(before);
        LyricTimingEditTarget target = new("primary", "one", "first");
        LyricTimingEditResult result = LyricTimingEditUseCase.Commit(workspace,
            new(workspace, workspace.Generation, target, before.Lyrics.Lines[0].Tokens[0].Time, null, 2_200_000));
        Assert.True(result.Accepted);
        Project after = result.Project;

        foreach ((Project source, Project destination) in new[] { (before, after), (after, workspace.Undo()), (before, workspace.Redo()) })
        {
            ProjectTimingChangeSet changes = LyricTimingEditUseCase.DescribeChanges(source, destination);
            Assert.True(changes.HasChanges);
            Assert.True(changes.InvalidatesPreview);
            Assert.False(changes.FullRefreshRequired);
            Assert.Equal(target, Assert.Single(changes.Targets));
        }
        Assert.Same(ProjectTimingChangeSet.None, LyricTimingEditUseCase.DescribeChanges(before, before));
    }

    [Fact]
    public void 仅时间来源状态变化也在行或元素目标中保留()
    {
        Project source = LyricTimingEditUseCaseTests.Sample();
        LyricLine old = source.Lyrics.Lines[0];
        LyricLine changed = old with { TimingStatus = TimingStatus.Confirmed,
            Tokens = old.Tokens.SetItem(1, old.Tokens[1] with { TimingStatus = TimingStatus.Estimated }) };
        Project after = source with { Lyrics = source.Lyrics with { Lines = source.Lyrics.Lines.SetItem(0, changed) } };

        ProjectTimingChangeSet changes = LyricTimingEditUseCase.DescribeChanges(source, after);

        Assert.Collection(changes.Targets,
            target => Assert.Equal(new LyricTimingEditTarget("primary", "one"), target),
            target => Assert.Equal(new LyricTimingEditTarget("primary", "one", "second"), target));
        Assert.False(changes.FullRefreshRequired);
        Assert.True(changes.InvalidatesPreview);
    }

    [Fact]
    public void 多来源时间差异标记明确来源而不是当前主来源()
    {
        Project source = LyricTimingEditUseCaseTests.Sample();
        LyricBranch added = LyricBranch.Create("custom", "独立", lines: [LyricLine.Create("extra", "额外")]);
        source = source with { Lyrics = source.Lyrics with { Branches = source.Lyrics.Branches.Add(added) } };
        Project after = source with { Lyrics = source.Lyrics.ReplaceBranch(added with
            { Lines = [added.Lines[0] with { Time = TimeRange.Create(10, 20), TimingStatus = TimingStatus.Edited }] }) };

        ProjectTimingChangeSet changes = LyricTimingEditUseCase.DescribeChanges(source, after);

        Assert.Equal(new LyricTimingEditTarget("custom", "extra"), Assert.Single(changes.Targets));
        Assert.False(changes.FullRefreshRequired);
    }

    [Theory]
    [InlineData("project")]
    [InlineData("document")]
    [InlineData("branch")]
    [InlineData("line-text")]
    [InlineData("line-ruby")]
    [InlineData("token-text")]
    [InlineData("token-style")]
    [InlineData("reorder-lines")]
    [InlineData("reorder-tokens")]
    [InlineData("remove-token")]
    public void 结构或时间以外的变化不能冒充纯时间增量(string kind)
    {
        Project source = LyricTimingEditUseCaseTests.Sample();
        LyricLine line = source.Lyrics.Lines[0];
        LyricToken token = line.Tokens[0];
        Project after = kind switch
        {
            "project" => source with { AudioPath = "/different.wav" },
            "document" => source with { Lyrics = source.Lyrics with { SchemaVersion = source.Lyrics.SchemaVersion + 1 } },
            "branch" => source with { Lyrics = source.Lyrics.ReplaceBranch(source.Lyrics.PrimaryBranch! with { Language = "ja" }) },
            "line-text" => Replace(line with { Text = "其他" }),
            "line-ruby" => Replace(line with { RubyAnnotations = [RubyAnnotation.Create("ruby", token.Id, "あ")] }),
            "token-text" => Replace(line with { Tokens = line.Tokens.SetItem(0, token with { Text = "别" }) }),
            "token-style" => Replace(line with { Tokens = line.Tokens.SetItem(0, token with { StyleOverride = new() { Opacity = .5f } }) }),
            "reorder-lines" => source with { Lyrics = source.Lyrics with { Lines = [source.Lyrics.Lines[1], line] } },
            "reorder-tokens" => Replace(line with { Tokens = [line.Tokens[1], token] }),
            "remove-token" => Replace(line with { Tokens = line.Tokens.RemoveAt(0) }),
            _ => throw new InvalidOperationException(kind),
        };

        ProjectTimingChangeSet changes = LyricTimingEditUseCase.DescribeChanges(source, after);

        Assert.Same(ProjectTimingChangeSet.FullRefresh, changes);
        Assert.True(changes.HasChanges);
        Assert.True(changes.InvalidatesPreview);
        Assert.Empty(changes.Targets);

        Project Replace(LyricLine updated) => source with
            { Lyrics = source.Lyrics with { Lines = source.Lyrics.Lines.SetItem(0, updated) } };
    }

    [Fact]
    public void 主歌词时间摘要使跟随来源所读的正式预览整体失效()
    {
        Project source = LyricTimingEditUseCaseTests.Sample();
        LyricBranch translation = LyricBranch.Create("translation", "翻译", LyricBranchRole.Translation,
            [LyricLine.Create("translated", "译") with { PrimaryLineId = "one" }], timingMode: LyricBranchTimingMode.FollowPrimary);
        source = source with { Lyrics = source.Lyrics with { Branches = source.Lyrics.Branches.Add(translation) } };
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(source);

        LyricTimingEditResult result = LyricTimingEditUseCase.Commit(workspace,
            new(workspace, workspace.Generation, new("primary", "one"), source.Lyrics.Lines[0].Time, 900_000, null));

        Assert.True(result.Accepted);
        Assert.Same(translation, result.Project.Lyrics.Branches[1]);
        Assert.True(result.ChangeSet.InvalidatesPreview);
        Assert.True(LyricTimingEditUseCase.DescribeChanges(source, result.Project).InvalidatesPreview);
    }
}
