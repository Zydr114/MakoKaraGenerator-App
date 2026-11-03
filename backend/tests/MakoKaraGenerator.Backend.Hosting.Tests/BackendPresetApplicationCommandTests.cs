using System.Text.Json;
using MakoKaraGenerator.Application;
using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Media;
using MakoKaraGenerator.TestSupport;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

public sealed partial class BackendPresetApplicationCommandTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 0, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task 严格命令可从固定模板快照应用并支持撤销重做与CAS门禁()
    {
        await using Fixture f = await Fixture.CreateAsync();
        TemplateListResponse builtin = (await f.Registry.ReadTemplatesAsync("builtin", null)).Response!;
        TemplateSummaryDto template = builtin.Templates[0];
        var apply = new ApplyPresetApplicationCommandDto("builtin", template.TemplateId, builtin.Revision,
            [new("project")], null);

        ProjectCommandResponse accepted = AssertAccepted(await f.Command("0", apply));
        Assert.True(accepted.Changed);
        Assert.Single(f.SessionProject.PresetApplications.Items);
        Assert.Equal(template.TemplateId, Assert.Single(f.SessionProject.PresetApplications.Items).Snapshot.Id);

        ProjectCommandExecutionResult stale = await f.Command("0", apply);
        Assert.Equal(BackendErrorCode.RevisionConflict, stale.Error?.Code);

        ProjectCommandResponse undone = AssertAccepted(await f.Command(accepted.ProjectRevision, new("history.undo")));
        Assert.True(undone.Changed);
        Assert.Empty(f.SessionProject.PresetApplications.Items);
        ProjectCommandResponse redone = AssertAccepted(await f.Command(undone.ProjectRevision, new("history.redo")));
        Assert.Single(f.SessionProject.PresetApplications.Items);
        Assert.Equal(redone.ProjectRevision, f.SessionRevision);
    }

    [Fact]
    public async Task 更新保留应用身份种子启停且移除不删除固定快照资源()
    {
        await using Fixture f = await Fixture.CreateAsync();
        TemplateListResponse builtin = (await f.Registry.ReadTemplatesAsync("builtin", null)).Response!;
        TemplateSummaryDto template = builtin.Templates[0];
        ProjectCommandResponse applied = AssertAccepted(await f.Command("0", new ApplyPresetApplicationCommandDto(
            "builtin", template.TemplateId, builtin.Revision, [new("project")], null)));
        PresetApplication before = Assert.Single(f.SessionProject.PresetApplications.Items);
        ProjectCommandResponse disabled = AssertAccepted(await f.Command(applied.ProjectRevision,
            new SetPresetApplicationEnabledCommandDto(before.Id, false)));
        PresetApplication disabledApplication = Assert.Single(f.SessionProject.PresetApplications.Items);
        Assert.False(disabledApplication.Enabled);

        ProjectCommandResponse updated = AssertAccepted(await f.Command(disabled.ProjectRevision,
            new UpdatePresetApplicationCommandDto(before.Id, "builtin", template.TemplateId, builtin.Revision, null)));
        PresetApplication after = Assert.Single(f.SessionProject.PresetApplications.Items);
        Assert.Equal(before.Id, after.Id);
        Assert.Equal(disabledApplication.Enabled, after.Enabled);
        Assert.Equal(before.Identities, after.Identities);

        ProjectCommandResponse removed = AssertAccepted(await f.Command(updated.ProjectRevision,
            new RemovePresetApplicationCommandDto([before.Id])));
        Assert.Empty(f.SessionProject.PresetApplications.Items);
        Assert.Equal(removed.ProjectRevision, f.SessionRevision);
    }

    [Fact]
    public void 严格分型拒绝缺字段未知库和额外字段()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ProjectCommandDto>(
            "{\"type\":\"presetApplications.apply\",\"templateId\":\"x\",\"templateRevision\":\"missing\",\"targets\":[]}", Json));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ProjectCommandDto>(
            "{\"type\":\"presetApplications.setEnabled\",\"applicationId\":\"x\",\"enabled\":true,\"extra\":1}", Json));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ProjectCommandDto>(
            "{\"type\":\"presetApplications.apply\",\"templateLibraryId\":\"other\",\"templateId\":\"x\",\"templateRevision\":\"missing\",\"targets\":[{\"kind\":\"project\"}]}", Json));
    }

    private static ProjectCommandResponse AssertAccepted(ProjectCommandExecutionResult result)
    {
        Assert.True(result.Accepted, $"{result.Error?.Code}: {result.Error?.Field}: {result.Error?.Message}");
        return result.Response!;
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly TemporaryDirectory _directory;
        internal ProjectSessionRegistry Registry { get; }
        internal ProjectSession Session { get; }
        internal string SessionRevision => ((ProjectSessionSummaryResponse)Registry.ReadSummaryAsync("client", Session.ProjectSessionId).GetAwaiter().GetResult()!).ProjectRevision;
        internal Project SessionProject => ((ProjectWorkspace)typeof(ProjectSession).GetProperty("Workspace", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(Session)!).Project;

        private Fixture(TemporaryDirectory directory, ProjectSessionRegistry registry, ProjectSession session) =>
            (_directory, Registry, Session) = (directory, registry, session);

        internal static async Task<Fixture> CreateAsync(int receiptBytes = 65536)
        {
            TemporaryDirectory directory = new();
            ResourceStore resources = new(new() { ResourceRoot = directory.Path });
            StoredAsset lyrics = await resources.UploadAsync("client", "song.lrc", "text/plain", new MemoryStream("[00:00.00]甲\n"u8.ToArray()));
            string wavPath = directory.FilePath("song.wav");
            WavWriter.Write(wavPath, WavWriter.BuildSegmentedTone(8000, (0.25, 0.5f)), 8000);
            StoredAsset audio = await resources.UploadAsync("client", "song.wav", "audio/wav", new MemoryStream(await File.ReadAllBytesAsync(wavPath)));
            WaveformPeakBuilder peaks = new();
            peaks.Append(new float[2000]);
            LoadedWaveform waveform = new(new AudioStreamInfo(250000, 8000, 1, "pcm", "wav"), peaks.Build(8000));
            ProjectSessionRegistry registry = new("host", resources, new((_, _) => Task.FromResult<LoadedWaveform?>(waveform)), new FixedClock(), commandRetention: new() { MaxReceiptBytes = receiptBytes });
            registry.ConfigurePersistence(new() { StateRoot = Path.Combine(directory.Path, "state") });
            registry.ConfigureTemplateLibrary();
            ProjectSessionCreateResult created = await registry.CreateAsync("client", new("preset", audio.AssetId, lyrics.AssetId));
            Assert.True(created.Accepted, created.Error?.ToString());
            ProjectSession session = created.Session!;
            return new(directory, registry, session);
        }

        internal string SaveTemplate(StyleTemplate template)
        {
            string index = StyleTemplateLibrary.ResolvePath(Path.Combine(_directory.Path, "state", "libraries"));
            MakoKaraGenerator.Core.Persistence.StyleTemplateLibrary.Save(index, [template]);
            return Registry.ReadTemplatesAsync("personal", null).GetAwaiter().GetResult().Response!.Revision;
        }

        internal Task<ProjectCommandExecutionResult> Command(string revision, ProjectCommandDto command) =>
            Registry.ExecuteCommandAsync("client", Session.ProjectSessionId,
                new(revision, command, Guid.NewGuid().ToString("N"), "host", Now.AddMinutes(1)));
        public async ValueTask DisposeAsync()
        {
            await Registry.DisposeAsync();
            _directory.Dispose();
        }

        private sealed class FixedClock : TimeProvider
        {
            public override DateTimeOffset GetUtcNow() => Now;
        }
    }
}
