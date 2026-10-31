using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using MakoKaraGenerator.Application;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Media;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

public sealed class BackendBasicMediaTests
{
    private static readonly string[] MediaRoles = ["project.audio", "project.background"];
    private static readonly string[] TemplateRoles = ["presetApplication:application-a", "presetApplication:application-b"];
    [Fact]
    public async Task 改名完整回执预算在提交之前拒绝保name修订dirty和历史()
    {
        using Fixture f = new(host: new string('h', 1200), receiptBytes: 1024);
        ProjectSession session = await f.Create(); Project before = session.Workspace.Project;
        ProjectCommandExecutionResult rejected = await f.Command(session, new RenameProjectCommandDto("changed"));
        Assert.False(rejected.Accepted); Assert.Equal(BackendErrorCode.ResourceTooLarge, rejected.Error!.Code);
        Assert.Same(before, session.Workspace.Project); Assert.Equal("project", session.Workspace.Project.Name);
        Assert.Equal(0, session.ProjectRevision); Assert.Equal(0, session.Workspace.History.UndoCount); Assert.Equal(0, session.Workspace.History.RedoCount);
    }

    [Fact]
    public async Task 音频历史保留租约与峰值且同名同源空操作保redo截断容量淘汰和关闭各自释放()
    {
        using Fixture f = new();
        ProjectSession session = await f.Create(); string first = session.AudioAssetId;
        string firstWave = (await f.Registry.ReadWaveformAsync("a", session.ProjectSessionId)).Response!.WaveformId!;
        StoredAsset second = await f.Upload(2);
        Assert.True((await f.Command(session, new ReplaceProjectAudioAssetCommandDto(second.AssetId))).Accepted);
        string secondWave = (await f.Registry.ReadWaveformAsync("a", session.ProjectSessionId)).Response!.WaveformId!;
        Assert.NotEqual(firstWave, secondWave); Assert.Equal(2, f.Cache.EntryCount);
        ProjectCommandExecutionResult undone = await f.Command(session, new("history.undo"));
        Assert.True(undone.Accepted); Assert.Contains("audio", undone.Response!.ChangeSet.Areas);
        Assert.Contains("waveform", undone.Response.ChangeSet.Areas); Assert.True(undone.Response.ChangeSet.RequiresFullRefresh);
        BackendEventResponse notification = f.Registry.ReadEvents("a", null).Events.Last();
        Assert.Equal("project.changed", notification.Type); Assert.Equal(first, notification.Summary!.AudioAssetId);
        Assert.Equal(undone.Response.ProjectRevision, notification.Summary.ProjectRevision);
        Assert.Equal(undone.Response.ChangeSet.Areas, notification.ChangeSet!.Areas);
        ProjectResourcesResponse restoredResources = (await f.Registry.ReadProjectResourcesAsync("a", session.ProjectSessionId,
            undone.Response.ProjectRevision)).Response!;
        Assert.Equal(first, Assert.Single(restoredResources.Resources).AssetId);
        Assert.Equal(first, session.AudioAssetId); Assert.Equal(firstWave, (await f.Registry.ReadWaveformAsync("a", session.ProjectSessionId)).Response!.WaveformId);
        ProjectCommandExecutionResult noOp = await f.Command(session, new ReplaceProjectAudioAssetCommandDto(first));
        Assert.True(noOp.Accepted); Assert.False(noOp.Response!.Changed); Assert.True(noOp.Response.CanRedo);
        Assert.Empty(noOp.Response.ChangeSet.Areas);
        ProjectCommandExecutionResult sameName = await f.Command(session, new RenameProjectCommandDto("  project  "));
        Assert.False(sameName.Response!.Changed); Assert.True(sameName.Response.CanRedo);
        Assert.Empty(f.Registry.ReadEvents("a", undone.Response.EventCursor).Events);
        Assert.True((await f.Command(session, new RenameProjectCommandDto("new-name"))).Response!.Changed);
        ProjectCommandExecutionResult nameUndo = await f.Command(session, new("history.undo"));
        Assert.Contains("project", nameUndo.Response!.ChangeSet.Areas);
        Assert.Equal("project", f.Registry.ReadEvents("a", null).Events.Last().Summary!.Name);
        ProjectCommandExecutionResult nameRedo = await f.Command(session, new("history.redo"));
        Assert.Contains("project", nameRedo.Response!.ChangeSet.Areas);
        Assert.Equal("new-name", (await f.Registry.ReadSettingsAsync("a", session.ProjectSessionId, nameRedo.Response.ProjectRevision)).Response!.Name);
        Assert.Equal(1, f.Cache.EntryCount);
        f.Clock.Advance(10); f.Resources.CollectExpired();
        Assert.False(f.Resources.TryGetAuthorized("a", second.AssetId, out _));
        Assert.True(f.Resources.TryGetAuthorized("a", first, out _));
        StoredAsset third = await f.Upload(3);
        Assert.True((await f.Command(session, new ReplaceProjectAudioAssetCommandDto(third.AssetId))).Response!.Changed);
        for (int index = 0; index < 201; index++)
            Assert.True((await f.Command(session, new RenameProjectCommandDto("name-" + index))).Response!.Changed);
        Assert.Equal(200, session.Workspace.History.UndoCount); Assert.Equal(1, f.Cache.EntryCount);
        f.Clock.Advance(10); f.Resources.CollectExpired();
        Assert.False(f.Resources.TryGetAuthorized("a", first, out _)); Assert.True(f.Resources.TryGetAuthorized("a", third.AssetId, out _));
        await f.Close(session);
        Assert.Equal(0, f.Cache.EntryCount); f.Clock.Advance(10); f.Resources.CollectExpired();
        Assert.False(f.Resources.TryGetAuthorized("a", third.AssetId, out _));
    }

    [Fact]
    public async Task 准备后固定资源损坏拒绝且候选lease和峰值释放并完整保旧音轨分析输出时长历史()
    {
        using Fixture f = new(); ProjectSession session = await f.Create();
        Project before = session.Workspace.Project;
        StoredAsset replacement = await f.Upload(4);
        f.Registry.BasicAudioPreparationCheckpoint = () => File.WriteAllBytesAsync(replacement.StoragePath, [99]);
        ProjectCommandExecutionResult rejected = await f.Command(session, new ReplaceProjectAudioAssetCommandDto(replacement.AssetId));
        Assert.False(rejected.Accepted); Assert.Equal(BackendErrorCode.TaskExecutionFailed, rejected.Error!.Code);
        Assert.Same(before, session.Workspace.Project); Assert.Equal(0, session.ProjectRevision); Assert.Equal(0, session.Workspace.History.UndoCount);
        Assert.Equal(1, f.Cache.EntryCount);
        f.Clock.Advance(10); f.Resources.CollectExpired(); Assert.False(f.Resources.TryGetAuthorized("a", replacement.AssetId, out _));
        Assert.True(f.Resources.TryGetAuthorized("a", session.AudioAssetId, out _));
    }

    [Fact]
    public async Task 资源投影去重保用途且只列当前音轨背景和已采用分析并校验授权修订分页()
    {
        using Fixture f = new(); ProjectSession session = await f.Create(); StoredAsset unused = await f.Upload(9);
        Project original = session.Workspace.Project;
        var analysis = new AudioAnalysisRevision(original.AudioMedia!.Hash, new string('b', 64), 384, [new(0, .1, .2, null, 0)]);
        session.Workspace.Replace(original with { AudioAnalysis = ProjectAudioAnalysis.Available(analysis),
            Background = BackgroundSource.ForImage(original.AudioPath!) with { Media = original.AudioMedia } });
        ProjectResourcesResponse response = (await f.Registry.ReadProjectResourcesAsync("a", session.ProjectSessionId, "0")).Response!;
        Assert.Equal(2, response.TotalCount);
        ProjectResourceDto media = Assert.Single(response.Resources, resource => resource.Kind == "media");
        Assert.Equal(MediaRoles, media.Roles); Assert.Equal(session.AudioAssetId, media.AssetId);
        Assert.Contains(response.Resources, resource => resource.Kind == "audioAnalysis" && resource.Sha256 == analysis.ContentHash);
        Assert.DoesNotContain(response.Resources, resource => resource.AssetId == unused.AssetId);
        Assert.DoesNotContain(original.AudioPath!, JsonSerializer.Serialize(response), StringComparison.Ordinal);
        Assert.False((await f.Registry.ReadProjectResourcesAsync("b", session.ProjectSessionId, "0")).Accepted);
        Assert.Equal(BackendErrorCode.RevisionConflict, (await f.Registry.ReadProjectResourcesAsync("a", session.ProjectSessionId, "1")).Error!.Code);
        ProjectResourcesResponse page = (await f.Registry.ReadProjectResourcesAsync("a", session.ProjectSessionId, "0", 0, 1)).Response!;
        Assert.Single(page.Resources); Assert.Equal(1, page.NextOffset);
    }

    [Fact]
    public async Task 资源清单按固定模板内容去重且完整用途累计超预算不能借小分页绕过()
    {
        using Fixture f = new(); ProjectSession session = await f.Create();
        StyleTemplate snapshot = StyleTemplate.Create("static", "fixed", StyleTemplateKind.Line,
            LyricStyle.Default with { Opacity = .4f }, [LyricStyleProperty.Opacity]);
        Project source = session.Workspace.Project with { PresetApplications = new([
            new PresetApplication("application-a", new(ProjectEffectBindingKind.Project), snapshot),
            new PresetApplication("application-b", new(ProjectEffectBindingKind.Branch, "primary"), snapshot)]) };
        session.Workspace.Replace(source);
        ProjectResourcesResponse resources = (await f.Registry.ReadProjectResourcesAsync("a", session.ProjectSessionId, "0")).Response!;
        ProjectResourceDto template = Assert.Single(resources.Resources, resource => resource.Kind == "templateSnapshot");
        Assert.Equal(TemplateRoles, template.Roles);
        Assert.Equal("embedded", template.Status); Assert.Equal(64, template.Sha256!.Length); Assert.True(long.Parse(template.SizeBytes!, CultureInfo.InvariantCulture) > 0);
        session.Workspace.Replace(source with { PresetApplications = new([new PresetApplication(new string('x', 1048577),
            new(ProjectEffectBindingKind.Project), snapshot)]) });
        BackendOperationResult<ProjectResourcesResponse> rejected = await f.Registry.ReadProjectResourcesAsync("a", session.ProjectSessionId, "0", 0, 1);
        Assert.Equal(BackendErrorCode.ResourceTooLarge, rejected.Error!.Code); Assert.Null(rejected.Response);
    }

    [Fact]
    public async Task 资源清单公开效果包闭包manifest文件身份role摘要并标记不支持role()
    {
        using Fixture f = new(); ProjectSession session = await f.Create();
        EffectPackageContents valid = EffectPackageFixture.Contents();
        Dictionary<string, byte[]> files = EffectPackageFixture.Files();
        var manifest = EffectPackageFixture.Manifest(files);
        manifest["files"]!.AsArray().First(item => item!["path"]!.GetValue<string>() == "README.md")!["role"] = "font";
        EffectPackageContents unsupported = EffectPackageContents.Create(EffectPackageFixture.Bytes(manifest),
            EffectPackageFixture.Inputs(files), EffectPackageFixture.Profile);
        session.Workspace.Replace(session.Workspace.Project with { Effects = new([valid, unsupported], []) });

        ProjectResourcesResponse response = (await f.Registry.ReadProjectResourcesAsync("a", session.ProjectSessionId, "0")).Response!;
        ProjectResourceDto validManifest = Assert.Single(response.Resources,
            resource => resource.Kind == "effectPackageManifest" && resource.PackageRevision == valid.Revision);
        Assert.Equal("validated", validManifest.Status);
        Assert.Equal("manifest", validManifest.PackageRole);
        Assert.Equal("manifest.json", validManifest.PackagePath);
        Assert.Equal(valid.PackageId, validManifest.PackageId);
        Assert.NotNull(validManifest.PackageClosure);
        Assert.Equal(valid.Files.Length, validManifest.PackageClosure!.FileCount);
        Assert.Equal(valid.Files.Length, validManifest.PackageClosure.ValidatedFileCount);
        Assert.Equal(0, validManifest.PackageClosure.UnsupportedFileCount);
        Assert.Equal(valid.Revision, validManifest.PackageClosure.PackageRevision);
        Assert.Contains(response.Resources, resource => resource.Kind == "effectPackageFile"
            && resource.PackageRevision == valid.Revision && resource.PackageRole == "csharp"
            && resource.PackagePath == "src/PackEffect.cs" && resource.Status == "validated");

        ProjectResourceDto unsupportedManifest = Assert.Single(response.Resources,
            resource => resource.Kind == "effectPackageManifest" && resource.PackageRevision == unsupported.Revision);
        Assert.Equal("unsupported", unsupportedManifest.Status);
        Assert.Equal(1, unsupportedManifest.PackageClosure!.UnsupportedFileCount);
        ProjectResourceDto unsupportedFile = Assert.Single(response.Resources,
            resource => resource.Kind == "effectPackageFile" && resource.PackageRevision == unsupported.Revision
                && resource.PackagePath == "README.md");
        Assert.Equal("font", unsupportedFile.PackageRole);
        Assert.Equal("unsupported", unsupportedFile.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 背景准备不持Writer并在第二次写入核对修改和关闭且释放候选租约(bool close)
    {
        using Fixture f = new(); ProjectSession session = await f.Create();
        StoredAsset image = await f.Resources.UploadAsync("a", "background.png", "image/png", new MemoryStream([1, 2, 3]));
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously), release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Registry.BackgroundPreparationCheckpoint = async () => { entered.TrySetResult(); await release.Task; };
        Task<ProjectCommandExecutionResult> pending = f.Command(session, new SetBackgroundAssetCommandDto(image.AssetId, "image"));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        if (close) await f.Close(session);
        else Assert.True((await f.Command(session, new RenameProjectCommandDto("concurrent"))).Accepted);
        release.TrySetResult();
        ProjectCommandExecutionResult result = await pending.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(result.Accepted);
        Assert.Equal(close ? BackendErrorCode.SessionClosing : BackendErrorCode.RevisionConflict, result.Error!.Code);
        Assert.Equal(BackgroundKind.Color, session.Workspace.Project.Background.Kind);
        f.Clock.Advance(10); f.Resources.CollectExpired();
        Assert.False(f.Resources.TryGetAuthorized("a", image.AssetId, out _));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TemporaryDirectory _directory = new();
        internal readonly TestClock Clock = new();
        internal readonly ResourceStore Resources;
        internal readonly ProjectSessionRegistry Registry;
        internal readonly WaveformCache Cache = new();
        internal Fixture(string host = "host", int receiptBytes = 64 * 1024)
        {
            Resources = new(new() { ResourceRoot = _directory.FilePath("assets"), UnreferencedAssetLifetimeSeconds = 1 }, Clock);
            Registry = new(host, Resources, new(Decode), Clock, commandRetention: new() { MaxReceiptBytes = receiptBytes }, waveformCache: Cache);
            Registry.ConfigurePersistence(new() { StateRoot = _directory.FilePath("state"), AutoSaveInterval = TimeSpan.FromHours(1),
                AllowedProjectRoots = new Dictionary<string, string> { ["projects"] = _directory.FilePath("projects") } }, loadAudio: Decode);
        }
        private static Task<LoadedWaveform?> Decode(string path, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); float value = File.ReadAllBytes(path)[0] / 128f;
            var peaks = WaveformPeakPyramid.Build(Enumerable.Repeat(value, 512).ToArray(), 8000);
            return Task.FromResult<LoadedWaveform?>(new(new(peaks.DurationUs, 8000, 1, "pcm", "wav"), peaks));
        }
        internal Task<StoredAsset> Upload(byte value) => Resources.UploadAsync("a", "sound.wav", "audio/wav", new MemoryStream([value, value, value]));
        internal async Task<ProjectSession> Create()
        {
            StoredAsset audio = await Upload(1);
            StoredAsset lyrics = await Resources.UploadAsync("a", "song.lrc", "text/plain", new MemoryStream("[00:00.00]hello\n"u8.ToArray()));
            ProjectSessionCreateResult result = await Registry.CreateAsync("a", new("project", audio.AssetId, lyrics.AssetId));
            Assert.True(result.Accepted, result.Error?.Message); return result.Session!;
        }
        internal RequestMetadataDto Meta() => new(Guid.NewGuid().ToString("N"), Registry.HostInstanceId, Clock.GetUtcNow().AddMinutes(1));
        internal Task<ProjectCommandExecutionResult> Command(ProjectSession session, ProjectCommandDto command)
        {
            var meta = Meta();
            return Registry.ExecuteCommandAsync("a", session.ProjectSessionId,
                new(session.ProjectRevision.ToString(CultureInfo.InvariantCulture), command, meta.RequestId, meta.HostInstanceId, meta.ValidUntilUtc));
        }
        internal async Task Close(ProjectSession session)
        {
            JobResponse job = (await Registry.StartCloseAsync("a", session.ProjectSessionId,
                new(session.ProjectRevision.ToString(CultureInfo.InvariantCulture), "discard", Metadata: Meta()))).Response!;
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
            while ((job = Registry.ReadJob("a", job.JobId).Response!).State is not ("succeeded" or "failed" or "cancelled")) await Task.Delay(10, timeout.Token);
            Assert.Equal("succeeded", job.State);
        }
        public void Dispose() { Registry.Dispose(); _directory.Dispose(); }
    }
    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        internal void Advance(int seconds) => _now = _now.AddSeconds(seconds);
    }
}
