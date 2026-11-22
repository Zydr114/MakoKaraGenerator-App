using System.Text.Json;
using MakoKaraGenerator.Core.Commands;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Media;
using MakoKaraGenerator.TestSupport;
using MakoKaraGenerator.Rendering;
using SkiaSharp;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

public sealed class ProjectEditCandidatesTests
{
    [Fact]
    public async Task 清除背景候选保原图片租约且批量应用可一次撤销重做恢复图片()
    {
        using var f = await Fixture.Create();
        using SKBitmap bitmap = new(8, 8); bitmap.Erase(new SKColor(30, 90, 180, 255));
        using SKImage image = SKImage.FromBitmap(bitmap); using SKData encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        byte[] bytes = encoded.ToArray();
        StoredAsset asset = await f.Resources.UploadAsync("client", "background.png", "image/png", new MemoryStream(bytes));
        Accept(await f.Command("0", new SetBackgroundAssetCommandDto(asset.AssetId, "image")));
        Project source = f.Session.Workspace.Project;
        ProjectCommandDto[] edits = [new ClearBackgroundCommandDto(), new SetBackgroundColorCommandDto(12, 34, 56, 255),
            new RenameProjectCommandDto("candidate background")];
        var applied = Accept(await f.Command("1", new ApplyProjectEditsCommandDto(edits)));
        Assert.Equal("2", applied.ProjectRevision); Assert.Equal(3, applied.EditResults!.Length);
        Assert.All(applied.EditResults, result => Assert.True(result.Changed));
        Assert.Contains("background", applied.ChangeSet.Areas); Assert.Contains("resources", applied.ChangeSet.Areas);
        Assert.Equal(2, f.Session.Workspace.History.UndoCount);
        Project final = f.Session.Workspace.Project;
        Assert.Equal(BackgroundSource.Color, final.Background); await AssertImageReadable();
        Accept(await f.Command("2", new("history.undo")));
        Assert.Same(source, f.Session.Workspace.Project); Assert.Equal(BackgroundKind.Image, f.Session.Workspace.Project.Background.Kind);
        await AssertImageReadable();
        Accept(await f.Command("3", new("history.redo")));
        Assert.Same(final, f.Session.Workspace.Project); await AssertImageReadable();

        async Task AssertImageReadable()
        {
            f.Clock.Advance(10); f.Resources.CollectExpired();
            using ResourceReadLease read = f.Resources.AcquireRead("client", asset.AssetId);
            using MemoryStream actual = new(); await read.Stream.CopyToAsync(actual);
            Assert.Equal(bytes, actual.ToArray());
        }
    }

    [Fact]
    public async Task 背景适配和播放候选复用原命令并一次应用撤销()
    {
        using var f = await Fixture.Create();
        Project source = f.Session.Workspace.Project;
        ProjectCommandDto[] edits = [new SetBackgroundFitCommandDto("cover"), new SetBackgroundPlaybackCommandDto("freezeLastFrame")];
        var applied = Accept(await f.Command("0", new ApplyProjectEditsCommandDto(edits)));
        Assert.Equal("1", applied.ProjectRevision); Assert.All(applied.EditResults!, result => Assert.True(result.Changed));
        Assert.Equal(BackgroundFit.Cover, f.Session.Workspace.Project.Background.Fit);
        Assert.Equal(BackgroundPlaybackMode.FreezeLastFrame, f.Session.Workspace.Project.Background.Playback);
        Assert.Equal(1, f.Session.Workspace.History.UndoCount);
        Accept(await f.Command("1", new("history.undo"))); Assert.Same(source, f.Session.Workspace.Project);
    }

    [Fact]
    public async Task 图片背景附着候选和批量应用沿原会话授权真实测量并释放固定资源()
    {
        using var f = await Fixture.Create();
        using TemporaryDirectory directory = new();
        using SKBitmap bitmap = new(8, 8); bitmap.Erase(new SKColor(30, 90, 180, 255));
        using SKImage image = SKImage.FromBitmap(bitmap); using SKData encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        byte[] bytes = encoded.ToArray();
        MediaFileRevision media = new(WorkerProtocol.ComputeSha256(bytes), bytes.Length, "attachment.png");
        string path = Path.Combine(directory.Path, media.RelativePath); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, bytes);
        Project source = LeadInFixture.Create() with { Background = new() { Kind = BackgroundKind.Image, Path = path, Media = media } };
        f.Session.Workspace.Execute(ProjectCommand.Create("fixture", _ => source));
        f.Session.Workspace.History.Clear(); f.Session.PinnedInputDirectory = directory.Path;
        f.Registry.ConfigureSceneMeasurements(ProjectRuntimePreviewInputTests.Options());
        var cue = source.LyricPresentation.FindProgram("primary")!.LeadIns.Single(cue => cue.Id == "cue-first");
        var attachment = new SetLeadInAttachmentCommandDto("primary", cue.Id, cue.IndicatorContainerId, "FixedParent");
        var applied = Accept(await f.Command("0", new ApplyProjectEditsCommandDto([attachment])));
        Assert.Equal("1", applied.ProjectRevision); Assert.True(applied.Changed);
        Assert.Equal(SceneAttachmentMode.FixedParent, f.Session.Workspace.Project.LyricPresentation.FindNode(cue.IndicatorContainerId)!.Attachment!.Mode);
        Assert.Equal(1, f.Session.Workspace.History.UndoCount);
        Assert.True(f.Session.DrainPinnedInputReadsAsync().IsCompleted);
        Accept(await f.Command("1", new("history.undo"))); Assert.Same(source, f.Session.Workspace.Project);
    }

    [Fact]
    public async Task 顺序编辑一次提交且保留每项选择反馈并一次撤销重做()
    {
        using var f = await Fixture.Create();
        Project before = f.Session.Workspace.Project;
        var line = before.Lyrics.Lines[0];
        var response = Accept(await f.Command("0", new ApplyProjectEditsCommandDto([
            new SetLineTextCommandDto(new("primary", line.Id), "候选歌词"),
            new SetCanvasSizeCommandDto(777, 444), new DuplicateLineCommandDto(new("primary", line.Id))])));
        Assert.Equal("1", response.ProjectRevision); Assert.Equal("project.applyEdits", response.UndoName);
        Assert.Equal(3, response.EditResults!.Length);
        Assert.NotNull(response.EditResults[2].Feedback!.CreatedLineId);
        Assert.NotNull(response.EditResults[2].Feedback!.Selection);
        Assert.Contains("composition", response.ChangeSet.Areas); Assert.Contains("lyricsContent", response.ChangeSet.Areas);
        Assert.All(f.Session.Workspace.Project.Lyrics.Lines, row => Assert.Equal("候选歌词", row.Text));
        Assert.Equal(777, f.Session.Workspace.Project.Composition.CanvasWidth);
        Assert.Equal(1, f.Session.Workspace.History.UndoCount);
        Project final = f.Session.Workspace.Project;
        Accept(await f.Command("1", new("history.undo"))); Assert.Same(before, f.Session.Workspace.Project);
        Accept(await f.Command("2", new("history.redo"))); Assert.Same(final, f.Session.Workspace.Project);
        var events = f.Registry.ReadEvents("client", null).Events;
        Assert.Equal(3, events.Count(item => item.Type == "project.changed"));
    }

    [Fact]
    public async Task 中途失败整体不提交且无变化保留redo()
    {
        using var f = await Fixture.Create();
        Accept(await f.Command("0", new SetCanvasSizeCommandDto(777, 444)));
        Accept(await f.Command("1", new("history.undo")));
        Project before = f.Session.Workspace.Project;
        var history = await f.Registry.ReadHistoryAsync("client", f.Session.ProjectSessionId);
        var rejected = await f.Command("2", new ApplyProjectEditsCommandDto([
            new SetCanvasSizeCommandDto(999, 555), new SetLineTextCommandDto(new("primary", "absent"), "bad")]));
        Assert.False(rejected.Accepted); Assert.Same(before, f.Session.Workspace.Project);
        Assert.Equal(history, await f.Registry.ReadHistoryAsync("client", f.Session.ProjectSessionId));
        var noop = Accept(await f.Command("2", new ApplyProjectEditsCommandDto([
            new SetCanvasSizeCommandDto(999, 555), new SetCanvasSizeCommandDto(before.Composition.CanvasWidth, before.Composition.CanvasHeight)])));
        Assert.False(noop.Changed); Assert.Empty(noop.ChangeSet.Areas);
        Assert.Same(before, f.Session.Workspace.Project);
        Assert.Equal(history, await f.Registry.ReadHistoryAsync("client", f.Session.ProjectSessionId));
    }

    [Fact]
    public async Task 候选准备后当前工程变化时最终CAS拒绝且不覆盖并发编辑()
    {
        using var f = await Fixture.Create();
        TaskCompletionSource prepared = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Registry.EditPreparationCheckpoint = async () => { prepared.SetResult(); await resume.Task; };
        Task<ProjectCommandExecutionResult> pending = f.Command("0", new ApplyProjectEditsCommandDto([
            new SetCanvasSizeCommandDto(777, 444), new SetLineTextCommandDto(new("primary", f.Session.Workspace.Project.Lyrics.Lines[0].Id), "candidate")]));
        await prepared.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Accept(await f.Command("0", new SetCanvasSizeCommandDto(888, 555)));
        resume.SetResult();
        Assert.Equal(BackendErrorCode.RevisionConflict, (await pending).Error!.Code);
        Assert.Equal(888, f.Session.Workspace.Project.Composition.CanvasWidth);
        Assert.Equal("original", f.Session.Workspace.Project.Lyrics.Lines[0].Text);
        Assert.Equal(1, f.Session.Workspace.History.UndoCount);
    }

    [Theory]
    [InlineData("history.undo")]
    [InlineData("audio.replaceAsset")]
    [InlineData("lyrics.importAsset")]
    [InlineData("composition.setBackgroundAsset")]
    [InlineData("project.applyEdits")]
    public async Task 历史外部资源与嵌套批处理不得作为草稿项(string type)
    {
        using var f = await Fixture.Create();
        Project before = f.Session.Workspace.Project;
        var response = await f.Command("0", new ApplyProjectEditsCommandDto([new(type)]));
        Assert.Equal(BackendErrorCode.CommandNotSupported, response.Error!.Code);
        Assert.Same(before, f.Session.Workspace.Project); Assert.Equal(0, f.Session.Workspace.History.UndoCount);
    }

    [Theory]
    [InlineData("{\"type\":\"project.applyEdits\",\"edits\":[],\"extra\":1}")]
    [InlineData("{\"type\":\"project.applyEdits\",\"edits\":[],\"edits\":[]}")]
    [InlineData("{\"type\":\"project.applyEdits\",\"edits\":null}")]
    public void 严格批量命令拒绝重复未知与空数组字段(string json) =>
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ProjectCommandDto>(json));

    private static ProjectCommandResponse Accept(ProjectCommandExecutionResult result)
    { Assert.True(result.Accepted, result.Error?.Message); return result.Response!; }

    private sealed class Fixture : IDisposable
    {
        private readonly TemporaryDirectory _directory = new();
        internal readonly TestClock Clock = new();
        internal ResourceStore Resources = null!;
        internal ProjectSessionRegistry Registry = null!;
        internal ProjectSession Session = null!;
        internal static async Task<Fixture> Create()
        {
            var f = new Fixture();
            ResourceStore resources = f.Resources = new(new() { ResourceRoot = Path.Combine(f._directory.Path, "resources"),
                UnreferencedAssetLifetimeSeconds = 1 }, f.Clock);
            var audio = await resources.UploadAsync("client", "song.wav", "audio/wav", new MemoryStream([1, 2, 3]));
            var lyrics = await resources.UploadAsync("client", "song.lrc", "text/plain", new MemoryStream("[00:00.00]original\n"u8.ToArray()));
            WaveformPeakBuilder builder = new(); builder.Append(new float[24000]);
            LoadedWaveform waveform = new(new AudioStreamInfo(3000000, 8000, 1, "pcm", "wav"), builder.Build(8000));
            f.Registry = new("host", resources, new((_, _) => Task.FromResult<LoadedWaveform?>(waveform)), f.Clock);
            f.Session = (await f.Registry.CreateAsync("client", new("edits", audio.AssetId, lyrics.AssetId))).Session!;
            return f;
        }
        internal Task<ProjectCommandExecutionResult> Command(string revision, ProjectCommandDto command) =>
            Registry.ExecuteCommandAsync("client", Session.ProjectSessionId, new(revision, command, Guid.NewGuid().ToString("N"), "host", Clock.GetUtcNow().AddMinutes(1)));
        public void Dispose() { Registry.Dispose(); _directory.Dispose(); }
    }

    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        internal void Advance(int seconds) => _now = _now.AddSeconds(seconds);
    }
}
