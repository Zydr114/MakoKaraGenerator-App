using System.Security.Cryptography;
using MakoKaraGenerator.Application.Editing;
using MakoKaraGenerator.Core.Commands;
using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Application.Tests;

public sealed class ProjectAudioAnalysisEditUseCaseTests
{
    private static readonly string SourceHash = new('a', 64);

    [Fact]
    public void 采用与清除各自独立一步且撤销重做恢复完整旧工程和精确分析()
    {
        Project source = Source();
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(source);
        ProjectAudioAnalysis analysis = Analysis();
        int notifications = 0;
        workspace.History.Changed += (_, _) => notifications++;

        ProjectAudioAnalysisEditResult accepted = ProjectAudioAnalysisEditUseCase.AdoptPrepared(workspace,
            ProjectAudioAnalysisEditUseCase.Capture(workspace), analysis);
        Assert.True(accepted.Accepted);
        Assert.True(accepted.Changed);
        Assert.Null(accepted.Error);
        Assert.Same(analysis, accepted.Project.AudioAnalysis);
        Assert.Same(source.AudioMedia, accepted.Project.AudioMedia);
        Assert.Same(source.Lyrics, accepted.Project.Lyrics);
        Assert.Same(source.LyricPresentation, accepted.Project.LyricPresentation);
        Assert.Same(source.LyricStyle, accepted.Project.LyricStyle);
        Assert.Same(source.Composition, accepted.Project.Composition);
        Assert.Same(source.Background, accepted.Project.Background);
        Assert.Same(source.Effects, accepted.Project.Effects);
        Assert.Same(source.PresetApplications, accepted.Project.PresetApplications);
        Assert.Equal("audio.analysis", workspace.UndoName);
        Assert.Equal(1, workspace.History.UndoCount);
        Assert.Equal(1, notifications);
        Assert.True(workspace.IsDirty);
        Assert.Same(source, workspace.Undo());
        Assert.False(workspace.IsDirty);
        Assert.Same(accepted.Project, workspace.Redo());

        ProjectAudioAnalysisEditResult cleared = ProjectAudioAnalysisEditUseCase.Clear(workspace,
            ProjectAudioAnalysisEditUseCase.Capture(workspace));
        Assert.True(cleared.Accepted);
        Assert.True(cleared.Changed);
        Assert.Null(cleared.Project.AudioAnalysis);
        Assert.Equal("audio.analysis", workspace.UndoName);
        Assert.Equal(2, workspace.History.UndoCount);
        Assert.Same(accepted.Project, workspace.Undo());
        Assert.Same(analysis.Data, workspace.Project.AudioAnalysis!.Data);
        Assert.Same(cleared.Project, workspace.Redo());
        Assert.Equal(6, notifications);
        Assert.Same(accepted.Project, workspace.Undo());
        Assert.Same(source, workspace.Undo());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void 内容相等或空清除不改变引用脏状态重做通知与捕获连续性(bool available)
    {
        Project source = Source() with { AudioAnalysis = available ? Analysis() : null };
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(source);
        workspace.Execute(ProjectCommand.Create("other", project => project with { Name = "临时编辑" }));
        workspace.Undo();
        ProjectAudioAnalysisEditBaseline baseline = ProjectAudioAnalysisEditUseCase.Capture(workspace);
        AssertUnchanged(workspace, () => available
            ? ProjectAudioAnalysisEditUseCase.AdoptPrepared(workspace, baseline, Analysis())
            : ProjectAudioAnalysisEditUseCase.Clear(workspace, baseline));

        ProjectAudioAnalysisEditResult following = available
            ? ProjectAudioAnalysisEditUseCase.Clear(workspace, baseline)
            : ProjectAudioAnalysisEditUseCase.AdoptPrepared(workspace, baseline, Analysis());
        Assert.True(following.Accepted);
        Assert.True(following.Changed);
        Assert.Equal(1, workspace.History.UndoCount);
        Assert.Equal(0, workspace.History.RedoCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void 清除保留的失败或无流分析能够一次撤销恢复真实状态(bool noStream)
    {
        ProjectAudioAnalysis previous = noStream ? ProjectAudioAnalysis.NoStream("ActualNoStream", "真实无流")
            : ProjectAudioAnalysis.Failed("ActualFailure", "真实失败");
        Project source = Source() with { AudioAnalysis = previous };
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(source);
        var result = ProjectAudioAnalysisEditUseCase.Clear(workspace, ProjectAudioAnalysisEditUseCase.Capture(workspace));
        Assert.True(result.Accepted);
        Assert.True(result.Changed);
        Assert.Null(result.Project.AudioAnalysis);
        Assert.Equal(1, workspace.History.UndoCount);
        Assert.Same(source, workspace.Undo());
        Assert.Same(previous, workspace.Project.AudioAnalysis);
        Assert.Same(result.Project, workspace.Redo());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void 失败或无流运行结果不能被当作已准备成功分析采用(bool noStream)
    {
        ProjectWorkspace workspace = WithRedo();
        ProjectAudioAnalysis unavailable = noStream ? ProjectAudioAnalysis.NoStream("ActualNoStream", "真实无流")
            : ProjectAudioAnalysis.Failed("ActualFailure", "真实失败");
        AssertRejected(workspace, () => ProjectAudioAnalysisEditUseCase.AdoptPrepared(workspace,
            ProjectAudioAnalysisEditUseCase.Capture(workspace), unavailable), ProjectAudioAnalysisEditErrorCode.PreparedAnalysisUnavailable);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void 两个入口拒绝其他工作区或同工程引用的新代次(bool clear, bool generation)
    {
        ProjectWorkspace original = WithRedo();
        var baseline = ProjectAudioAnalysisEditUseCase.Capture(original);
        ProjectWorkspace current = generation ? original : ProjectWorkspace.Adopt(original.Project);
        if (generation) current.StartNew(current.Project);
        AssertRejected(current, () => Invoke(current, baseline, clear), ProjectAudioAnalysisEditErrorCode.WorkspaceStale);
    }

    [Theory]
    [InlineData("path", ProjectAudioAnalysisEditErrorCode.AudioSourceStale)]
    [InlineData("hash", ProjectAudioAnalysisEditErrorCode.AudioSourceStale)]
    [InlineData("size", ProjectAudioAnalysisEditErrorCode.AudioSourceStale)]
    [InlineData("analysis", ProjectAudioAnalysisEditErrorCode.AnalysisStale)]
    [InlineData("project", ProjectAudioAnalysisEditErrorCode.ProjectStale)]
    public void 原音频分析或完整工程变化后旧基线不能覆盖当前状态(string change, ProjectAudioAnalysisEditErrorCode code)
    {
        ProjectWorkspace workspace = WithRedo();
        var baseline = ProjectAudioAnalysisEditUseCase.Capture(workspace);
        workspace.Execute(ProjectCommand.Create("other", project => change switch
        {
            "path" => project with { AudioPath = "/fixed/other.wav" },
            "hash" => project with { AudioMedia = new(new string('c', 64), 4096, "source.wav") },
            "size" => project with { AudioMedia = new(SourceHash, 4097, "source.wav") },
            "analysis" => project with { AudioAnalysis = Analysis() },
            _ => project with { Name = "另一工程内容" },
        }));
        AssertRejected(workspace, () => ProjectAudioAnalysisEditUseCase.AdoptPrepared(workspace, baseline, Analysis()), code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void 编辑后撤销回相同工程引用也不能复活旧基线(bool clear)
    {
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(Source() with { AudioAnalysis = clear ? Analysis() : null });
        var baseline = ProjectAudioAnalysisEditUseCase.Capture(workspace);
        workspace.Execute(ProjectCommand.Create("other", project => project with { Name = "编辑后撤销" }));
        workspace.Undo();
        Assert.Same(baseline.ExpectedProject, workspace.Project);
        AssertRejected(workspace, () => Invoke(workspace, baseline, clear), ProjectAudioAnalysisEditErrorCode.EditContinuityStale);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void 无音频或异源分析在完整历史保持时明确拒绝(bool mismatch)
    {
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(mismatch ? Source() : Project.Create("无音频"));
        workspace.Execute(ProjectCommand.Create("other", project => project with { Name = "临时编辑" }));
        workspace.Undo();
        AssertRejected(workspace, () => ProjectAudioAnalysisEditUseCase.AdoptPrepared(workspace,
            ProjectAudioAnalysisEditUseCase.Capture(workspace), Analysis(mismatch ? new string('c', 64) : SourceHash)),
            mismatch ? ProjectAudioAnalysisEditErrorCode.AnalysisSourceMismatch : ProjectAudioAnalysisEditErrorCode.AudioSourceMissing);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void 活动历史组合不能吞并采用或清除的独立步骤(bool clear)
    {
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(Source() with { AudioAnalysis = clear ? Analysis() : null });
        using (workspace.BeginGroup("outer"))
        {
            workspace.Execute(ProjectCommand.Create("other", project => project with { Name = "组内编辑" }));
            var baseline = ProjectAudioAnalysisEditUseCase.Capture(workspace);
            AssertRejected(workspace, () => Invoke(workspace, baseline, clear), ProjectAudioAnalysisEditErrorCode.HistoryGroupActive);
            Assert.True(workspace.History.IsInGroup);
            Assert.Equal("outer", workspace.UndoName);
        }
        Assert.Equal(1, workspace.History.UndoCount);
        Assert.Equal(clear, workspace.Project.AudioAnalysis is not null);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void 组内无变化保持原组合且不使同一捕获失效(bool available)
    {
        Project source = Source() with { AudioAnalysis = available ? Analysis() : null };
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(source);
        using (workspace.BeginGroup("outer"))
        {
            var baseline = ProjectAudioAnalysisEditUseCase.Capture(workspace);
            AssertUnchanged(workspace, () => available
                ? ProjectAudioAnalysisEditUseCase.AdoptPrepared(workspace, baseline, Analysis())
                : ProjectAudioAnalysisEditUseCase.Clear(workspace, baseline));
            AssertUnchanged(workspace, () => available
                ? ProjectAudioAnalysisEditUseCase.AdoptPrepared(workspace, baseline, Analysis())
                : ProjectAudioAnalysisEditUseCase.Clear(workspace, baseline));
            Assert.True(workspace.History.IsInGroup);
        }
        Assert.False(workspace.History.IsInGroup);
        Assert.Equal(0, workspace.History.UndoCount);
    }

    [Fact]
    public void 共享采用保存重开保留完整规范字节且清除撤销和再次保存保持归属()
    {
        using TemporaryDirectory directory = new();
        byte[] audio = [1, 2, 3, 4];
        string audioPath = directory.FilePath("original.wav");
        File.WriteAllBytes(audioPath, audio);
        string hash = Convert.ToHexStringLower(SHA256.HashData(audio));
        Project source = Source() with { AudioPath = audioPath, AudioMedia = new(hash, audio.Length, "original.wav") };
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(source);
        ProjectAudioAnalysis analysis = Analysis(hash);
        var accepted = ProjectAudioAnalysisEditUseCase.AdoptPrepared(workspace,
            ProjectAudioAnalysisEditUseCase.Capture(workspace), analysis);
        Assert.True(accepted.Accepted);
        byte[] canonical = AudioAnalysisJson.Encode(analysis.Data!);
        string saved = directory.FilePath("saved.mkgproj");
        workspace.SaveAs(saved);
        Assert.False(workspace.IsDirty);
        Assert.Equal(canonical, File.ReadAllBytes(Path.Combine(saved, analysis.Data!.RelativePath)));
        File.Delete(audioPath);

        ProjectWorkspace reopened = ProjectWorkspace.CreateNew("重开候选");
        var opened = reopened.Open(saved);
        Assert.True(opened.Loaded, opened.Report.ToSummary());
        Assert.Equal(analysis, reopened.Project.AudioAnalysis);
        Assert.Equal(canonical, AudioAnalysisJson.Encode(reopened.Project.AudioAnalysis!.Data!));
        Assert.Equal(audio, File.ReadAllBytes(reopened.Project.AudioPath!));
        Assert.False(reopened.IsDirty);
        Project old = reopened.Project;
        var cleared = ProjectAudioAnalysisEditUseCase.Clear(reopened, ProjectAudioAnalysisEditUseCase.Capture(reopened));
        Assert.True(cleared.Changed);
        Assert.True(reopened.IsDirty);
        Assert.Same(old, reopened.Undo());
        Assert.False(reopened.IsDirty);
        Assert.Same(cleared.Project, reopened.Redo());
        string copy = directory.FilePath("cleared.mkgproj");
        reopened.SaveAs(copy);
        Assert.False(reopened.IsDirty);
        Assert.Null(ProjectStore.Load(copy).Project!.AudioAnalysis);
        Assert.False(Directory.Exists(Path.Combine(copy, "data/analysis")));
        Assert.Equal(canonical, File.ReadAllBytes(Path.Combine(saved, analysis.Data.RelativePath)));
    }

    private static Project Source() => Project.Create("分析编辑", audioPath: "/fixed/source.wav")
        with { AudioMedia = new(SourceHash, 4096, "source.wav") };

    private static ProjectAudioAnalysis Analysis(string? hash = null) => ProjectAudioAnalysis.Available(new(
        hash ?? SourceHash, new string('b', 64), 961,
        [new(0, .12345678901234567, .4, null, 0), new(20_000, .3, .2, .75, .8)]));

    private static ProjectWorkspace WithRedo()
    {
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(Source());
        workspace.Execute(ProjectCommand.Create("other", project => project with { Name = "临时编辑" }));
        workspace.Undo();
        return workspace;
    }

    private static ProjectAudioAnalysisEditResult Invoke(ProjectWorkspace workspace,
        ProjectAudioAnalysisEditBaseline baseline, bool clear) => clear
        ? ProjectAudioAnalysisEditUseCase.Clear(workspace, baseline)
        : ProjectAudioAnalysisEditUseCase.AdoptPrepared(workspace, baseline, Analysis());

    private static void AssertRejected(ProjectWorkspace workspace, Func<ProjectAudioAnalysisEditResult> operation,
        ProjectAudioAnalysisEditErrorCode code) => AssertPreserved(workspace, operation, false, code);

    private static void AssertUnchanged(ProjectWorkspace workspace, Func<ProjectAudioAnalysisEditResult> operation) =>
        AssertPreserved(workspace, operation, true, null);

    private static void AssertPreserved(ProjectWorkspace workspace, Func<ProjectAudioAnalysisEditResult> operation,
        bool accepted, ProjectAudioAnalysisEditErrorCode? code)
    {
        Project source = workspace.Project;
        int undo = workspace.History.UndoCount, redo = workspace.History.RedoCount;
        string? undoName = workspace.UndoName, redoName = workspace.RedoName;
        bool dirty = workspace.IsDirty, inGroup = workspace.History.IsInGroup;
        int notifications = 0;
        workspace.History.Changed += OnChanged;
        ProjectAudioAnalysisEditResult result;
        try { result = operation(); }
        finally { workspace.History.Changed -= OnChanged; }
        Assert.Equal(accepted, result.Accepted);
        Assert.False(result.Changed);
        Assert.Equal(code, result.Error?.Code);
        Assert.Same(source, result.Project);
        Assert.Same(source, workspace.Project);
        Assert.Equal(undo, workspace.History.UndoCount);
        Assert.Equal(redo, workspace.History.RedoCount);
        Assert.Equal(undoName, workspace.UndoName);
        Assert.Equal(redoName, workspace.RedoName);
        Assert.Equal(dirty, workspace.IsDirty);
        Assert.Equal(inGroup, workspace.History.IsInGroup);
        Assert.Equal(0, notifications);
        void OnChanged(object? sender, EventArgs args) => notifications++;
    }
}
