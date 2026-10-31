using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Application.Tests;

public sealed class PresentationEditSessionTests
{
    [Fact]
    public void 无变化会话提交保留原引用且预演不能越过展示范围()
    {
        var source = ExclusiveDisplayFixture.Create(); var session = new PresentationEditSession(source);
        Assert.True(session.TryCommit(source,out var unchanged,out _)); Assert.Same(source,unchanged);
        var wrong = source with { Composition = source.Composition with { CanvasWidth = 800 } };
        Assert.False(session.TryPreview(wrong,out var rejected,out _)); Assert.Same(session,rejected);
        Assert.True(session.TryPreview(source,out var noChange,out _)); Assert.Same(session,noChange);
    }
    [Fact]
    public void 预演会话不会覆盖后来工程引用且有效提交只变更展示()
    {
        var source = ExclusiveDisplayFixture.Create(); var session = new PresentationEditSession(source);
        var root = source.LyricPresentation.Nodes[0];
        Assert.True(SceneTreeEditor.TrySetNode(source,root with { Placement = ScenePlacement.Create(new(.6,.4)) },out var next,out _));
        Assert.True(session.TryPreview(next,out var preview,out _)); Assert.True(preview.HasChanges);
        var later = source with { Name = "另一个编辑" };
        Assert.False(preview.TryCommit(later,out var result,out _)); Assert.Same(later,result);
        Assert.True(preview.TryCommit(source,out var committed,out _)); Assert.Same(source.Lyrics,committed.Lyrics); Assert.Equal(next,committed);
    }
}
