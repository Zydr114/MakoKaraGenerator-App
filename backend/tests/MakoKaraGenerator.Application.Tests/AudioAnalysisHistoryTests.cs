using MakoKaraGenerator.Core.Commands;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Application.Tests;

public sealed class AudioAnalysisHistoryTests
{
    [Fact]
    public void 接受分析与切换音频均共用历史且撤销恢复完整旧修订()
    {
        string hash = new string('a', 64); var media = new MediaFileRevision(hash, 4, "old.wav");
        var data = new AudioAnalysisRevision(hash, new string('b', 64), 1, [new(0, .1, .2, null, 0)]);
        var source = Project.Create("分析", audioPath: "/tmp/old.wav") with { AudioMedia = media }; ProjectHistory history = new();
        var accepted = history.Execute(new SetAudioAnalysisCommand(ProjectAudioAnalysis.Available(data)), source);
        Assert.Equal(1, history.UndoCount); Assert.Same(source, history.Undo(accepted)); Assert.Same(accepted, history.Redo(source));
        Assert.Same(accepted, history.Execute(new SetAudioAnalysisCommand(ProjectAudioAnalysis.Available(new(hash, new string('b', 64), 1, data.Points))), accepted)); Assert.Equal(1, history.UndoCount);
        var switched = history.Execute(ProjectCommand.Create("audio.source", p => ProjectMediaEditor.SetAudioPath(p, "/tmp/new.wav")), accepted);
        Assert.Null(switched.AudioAnalysis); Assert.Null(switched.AudioMedia); Assert.Same(accepted, history.Undo(switched)); Assert.Same(media, accepted.AudioMedia);
        Assert.Throws<ArgumentException>(() => new SetAudioAnalysisCommand(ProjectAudioAnalysis.Available(new(new string('c', 64), new string('b', 64), 1, data.Points))).Apply(accepted));
    }
    [Fact]
    public void 保存资源失败不改变路径保存引用或当前分析()
    {
        using TemporaryDirectory temp = new(); var workspace = ProjectWorkspace.CreateNew("旧状态"); string saved = temp.FilePath("old.mkgproj"); workspace.SaveAs(saved);
        var failed = ProjectAudioAnalysis.Failed("ActualFailure", "实际失败");
        workspace.Execute(ProjectCommand.Create("audio.source", p => ProjectMediaEditor.SetAudioPath(p, temp.FilePath("missing.wav"))));
        workspace.Execute(new SetAudioAnalysisCommand(failed)); var current = workspace.Project;
        Assert.Throws<FileNotFoundException>(() => workspace.SaveAs(temp.FilePath("new.mkgproj"))); Assert.True(workspace.IsDirty);
        Assert.Equal(Path.GetFullPath(saved), workspace.DirectoryPath); Assert.Same(current, workspace.Project); Assert.Same(failed, workspace.Project.AudioAnalysis);
    }
}
