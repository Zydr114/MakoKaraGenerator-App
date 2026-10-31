using System.Reflection;
using System.Text.Json;
using MakoKaraGenerator.Application;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Media;
using MakoKaraGenerator.TestSupport;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

public sealed class BackendLeadInCommandTests
{
    [Fact]
    public async Task 提示标记最大数量完整回显且重试回执数组不被消费者改写()
    {
        await using Fixture f = await Fixture.Create();
        ProjectCommandRequest request = Fixture.Request("0", new CreateOrchestrationCountdownCommandDto(f.Root, null, 64));
        var first = Accepted(await f.Registry.ExecuteCommandAsync("client", f.Id, request));
        string id = first.LeadInResult!.IndicatorContainerId!;
        string original = first.LeadInResult.MarkNodeIds![0];
        first.LeadInResult.MarkNodeIds[0] = "polluted";
        var retried = Accepted(await f.Registry.ExecuteCommandAsync("client", f.Id, request));
        Assert.Equal(original, retried.LeadInResult!.MarkNodeIds![0]);
        Assert.Equal("1", retried.ProjectRevision);
        Assert.Equal(64, retried.LeadInResult.MarkNodeIds.Length);
        Assert.Equal(1, f.Workspace.History.UndoCount);
        var model = (await f.Registry.ReadOrchestrationAsync("client", f.Id, "1")).Response!;
        Assert.Equal(64, model.Nodes.Single(n => n.Id == id).Content!.MarkNodeIds!.Length);
        Assert.Equal(BackendErrorCode.InvalidRequest, (await f.Command("1", new CreateOrchestrationCountdownCommandDto(f.Root, null, 65))).Error!.Code);
        Assert.Equal(1, f.Workspace.History.UndoCount);
    }

    [Fact]
    public async Task 等价前奏时长重试使用同一规范意图回执且只记一次历史()
    {
        await using Fixture f = await Fixture.Create();
        var countdown = Accepted(await f.Command("0", new CreateOrchestrationCountdownCommandDto(f.Root, null, 3)));
        string indicator = countdown.LeadInResult!.IndicatorContainerId!;
        string lineId = f.Workspace.Project.Lyrics.PrimaryBranch!.Lines[0].Id;
        var create = new CreateOrchestrationLeadInCommandDto("primary", "cue/canonical", null, lineId, indicator, "02000000", true);
        ProjectCommandRequest request = Fixture.Request("1", create);
        var first = Accepted(await f.Registry.ExecuteCommandAsync("client", f.Id, request));
        var replay = Accepted(await f.Registry.ExecuteCommandAsync("client", f.Id, request with { Command = create with { DurationUs = "+2000000" } }));
        Assert.Equal(first.ProjectRevision, replay.ProjectRevision);
        Assert.Equal(2, f.Workspace.History.UndoCount);
        var update = new UpdateOrchestrationLeadInCommandDto("primary", "cue/canonical", "新名称", lineId, indicator, "2000000", true);
        request = Fixture.Request("2", update);
        var updated = Accepted(await f.Registry.ExecuteCommandAsync("client", f.Id, request));
        replay = Accepted(await f.Registry.ExecuteCommandAsync("client", f.Id, request with { Command = update with { DurationUs = "002000000" } }));
        Assert.Equal(updated.ProjectRevision, replay.ProjectRevision);
        Assert.Equal(3, f.Workspace.History.UndoCount);
    }

    [Fact]
    public async Task 完整回执或新读投影超预算先拒绝且不产生历史()
    {
        await using Fixture small = await Fixture.Create(receiptBytes: 1024);
        var receipt = await small.Command("0", new CreateOrchestrationCountdownCommandDto(small.Root, null, 64));
        Assert.Equal(BackendErrorCode.ResourceTooLarge, receipt.Error!.Code);
        Assert.Equal(0, small.Workspace.History.UndoCount);
        Assert.Equal("0", (await small.Registry.ReadSummaryAsync("client", small.Id))!.ProjectRevision);

        await using Fixture huge = await Fixture.Create();
        Project before = huge.Workspace.Project;
        var tree = before.LyricPresentation;
        huge.Workspace.Replace(before with { LyricPresentation = tree with { Nodes = tree.Nodes.SetItem(0,
            tree.Nodes[0] with { Name = new string('x', ProjectReadModelLimits.MaxResponseBytes + 1) }) } });
        var readBudget = await huge.Command("0", new CreateOrchestrationCountdownCommandDto(huge.Root, null, 3));
        Assert.Equal(BackendErrorCode.ResourceTooLarge, readBudget.Error!.Code);
        Assert.Equal(0, huge.Workspace.History.UndoCount);
    }

    [Fact]
    public async Task 同修订并发仅一次写入且缺失来源目标返回稳定失败()
    {
        await using Fixture f = await Fixture.Create();
        var writes = await Task.WhenAll(f.Command("0", new CreateOrchestrationCountdownCommandDto(f.Root, null, 3)),
            f.Command("0", new CreateOrchestrationCountdownCommandDto(f.Root, null, 3)));
        Assert.Single(writes, w => w.Accepted);
        Assert.Equal(BackendErrorCode.RevisionConflict, Assert.Single(writes, w => !w.Accepted).Error!.Code);
        Assert.Equal("TargetStale", (await f.Command("1", new DeleteOrchestrationLeadInCommandDto("primary", "absent"))).Error!.FailureCode);
        Assert.Equal("SourceMissing", (await f.Command("1", new DeleteOrchestrationLeadInCommandDto("missing", "absent"))).Error!.FailureCode);
        Assert.Equal(0, f.Workspace.Project.LyricPresentation.Programs.Sum(p => p.LeadIns.Length));
    }

    private static ProjectCommandResponse Accepted(ProjectCommandExecutionResult result)
    { Assert.True(result.Accepted, result.Error?.ToString()); return result.Response!; }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly TemporaryDirectory _directory = new();
        internal ProjectSessionRegistry Registry = null!;
        internal ProjectSession Session = null!;
        internal ProjectWorkspace Workspace => (ProjectWorkspace)typeof(ProjectSession).GetProperty("Workspace", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Session)!;
        internal string Id => Session.ProjectSessionId;
        internal string Root => Workspace.Project.LyricPresentation.WithDefaultPrograms(Workspace.Project.Lyrics).FindProgram("primary")!.RootContainerId;
        internal static async Task<Fixture> Create(int receiptBytes = 65536)
        {
            Fixture f = new();
            ResourceStore resources = new(new() { ResourceRoot = f._directory.Path });
            var lyrics = await resources.UploadAsync("client", "song.lrc", "text/plain", new MemoryStream("[00:02.00]甲\n"u8.ToArray()));
            var audio = await resources.UploadAsync("client", "song.wav", "audio/wav", new MemoryStream([1, 2, 3]));
            WaveformPeakBuilder peaks = new(); peaks.Append(new float[80000]);
            LoadedWaveform waveform = new(new AudioStreamInfo(10000000, 8000, 1, "pcm", "wav"), peaks.Build(8000));
            f.Registry = new("host", resources, new((_, _) => Task.FromResult<LoadedWaveform?>(waveform)), commandRetention: new() { MaxReceiptBytes = receiptBytes });
            f.Session = (await f.Registry.CreateAsync("client", new("lead-in", audio.AssetId, lyrics.AssetId))).Session!;
            Assert.NotNull(f.Session);
            return f;
        }
        internal static ProjectCommandRequest Request(string revision, ProjectCommandDto command) =>
            new(revision, command, Guid.NewGuid().ToString("N"), "host", DateTimeOffset.UtcNow.AddMinutes(1));
        internal Task<ProjectCommandExecutionResult> Command(string revision, ProjectCommandDto command) =>
            Registry.ExecuteCommandAsync("client", Id, Request(revision, command));
        public async ValueTask DisposeAsync() { await Registry.DisposeAsync(); _directory.Dispose(); }
    }
}
