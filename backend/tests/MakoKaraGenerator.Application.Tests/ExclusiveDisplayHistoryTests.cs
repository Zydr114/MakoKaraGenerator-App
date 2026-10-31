using MakoKaraGenerator.Application;
using MakoKaraGenerator.Core.Commands;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Application.Tests;

public sealed class ExclusiveDisplayHistoryTests
{
    [Fact]
    public void 完整树提交一步撤销恢复绑定和源引用且删除独占源可重做()
    {
        var configured = ExclusiveDisplayFixture.Create();
        var original = configured with { LyricPresentation = configured.LyricPresentation with
            { Programs = configured.LyricPresentation.Programs.SetItem(0, configured.LyricPresentation.Programs[0] with { Clips = [] }) } };
        ProjectHistory history = new();
        var applied = history.Execute(ProjectCommand.Create("scene.configure", source =>
            PresentationEditor.TrySetTree(source, configured.LyricPresentation, out var result, out _) ? result : source), original);
        Assert.Equal(1, history.UndoCount); Assert.Same(original.Lyrics, applied.Lyrics);
        Assert.Same(original, history.Undo(applied)); Assert.Same(applied, history.Redo(original));
        var deleted = history.Execute(ProjectCommand.Create("line.delete", source =>
        {
            var lyrics = source.Lyrics with { Lines = source.Lyrics.Lines.RemoveAt(2) };
            return source with { Lyrics = lyrics, LyricPresentation = source.LyricPresentation.RemoveDeletedSources(lyrics) };
        }), applied);
        Assert.Empty(deleted.LyricPresentation.Programs[0].Clips);
        Assert.Same(applied, history.Undo(deleted)); Assert.Same(deleted, history.Redo(applied));
    }
}
