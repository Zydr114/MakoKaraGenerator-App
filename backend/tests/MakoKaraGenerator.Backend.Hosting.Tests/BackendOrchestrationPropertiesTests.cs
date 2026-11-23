using System.Reflection;
using System.Text.Json;
using MakoKaraGenerator.Application;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Rendering;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Media;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.TestSupport;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

public sealed class BackendOrchestrationPropertiesTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly ProjectOrchestrationPlacementDto Geometry = new(1.2, -.3, "TopLeft", 17, 1.4);
    private static readonly ProjectOrchestrationReferenceRectDto Rect = new(.8, .6);
    private static readonly ProjectOrchestrationSafeAreaDto Area = new(.1, .2, .85, .95);
    private static ProjectOrchestrationSlotAnimationDto Animation => new("LeadBeforeNextStart", .3, "12000", "23000", 3,
        "ReplacePrevious", "UntilNextReveal");

    [Fact]
    public async Task 节点布局程序属性单次历史精确回显撤销重做与静态闭包持久化保持()
    {
        await using Fixture f = await Fixture.Create();
        Project initial = f.Workspace.Project;
        var model = await f.Model("0"); var program = model.Programs.Single(p => p.BranchId == "primary");
        var originalNode = model.Nodes.Single(n => n.Id == program.RootContainerId);
        var geometry = Accepted(await f.Command("0", new SetOrchestrationNodeGeometryCommandDto(program.RootContainerId, Geometry, Rect)));
        Assert.Equal(new OrchestrationPropertiesCommandResultDto("setNodeGeometry", program.RootContainerId, null, true), geometry.PropertiesResult);
        Assert.Equal(["orchestration", "renderInputs"], geometry.ChangeSet.Areas);
        var read = (await f.Model("1")).Nodes.Single(n => n.Id == program.RootContainerId);
        Assert.Equal(Geometry, read.Placement); Assert.Equal(Rect, read.ReferenceRect);
        Assert.Equal(originalNode.ParentId, read.ParentId); Assert.Equal(originalNode.Name, read.Name);
        Accepted(await f.Command("1", new SetOrchestrationLayoutSettingsCommandDto(program.DefaultContainerId, Area, .72)));
        Accepted(await f.Command("2", new SetOrchestrationProgramAnimationPolicyCommandDto("primary", "Static")));
        Assert.Equal(3, f.Workspace.History.UndoCount); Assert.Same(initial.Lyrics, f.Workspace.Project.Lyrics);
        var final = f.Workspace.Project;
        var layout = (await f.Model("3")).Nodes.Single(n => n.Id == program.DefaultContainerId).Layout!;
        Assert.Equal(Area, layout.SafeArea); Assert.Equal(.72, layout.MaxWidthRatio);
        Assert.Equal("Static", (await f.Model("3")).Programs.Single(p => p.BranchId == "primary").AnimationPolicy);
        var snapshot = ProjectSnapshot.FromProject(final);
        var decoded = StaticProjectSnapshotCodec.Decode(StaticProjectSnapshotCodec.Encode(snapshot, StaticProjectSnapshotCodec.SelectProfile(snapshot)));
        Assert.Equal(final.LyricPresentation, decoded.LyricPresentation);
        string path = Path.Combine(f.DirectoryPath, "properties.mkgproj"); f.Workspace.SaveAs(path);
        ProjectWorkspace reopened = ProjectWorkspace.CreateNew("reopened"); Assert.True(reopened.Open(path).Loaded);
        Assert.Equal(final.LyricPresentation, reopened.Project.LyricPresentation);
        Accepted(await f.Command("3", new("history.undo")));
        var noop = Accepted(await f.Command("4", new SetOrchestrationProgramAnimationPolicyCommandDto("primary", null)));
        Assert.False(noop.Changed); Assert.True(noop.CanRedo); Assert.Equal("4", noop.ProjectRevision);
        Accepted(await f.Command("4", new("history.redo"))); Assert.Same(final, f.Workspace.Project);
        Assert.Equal(3, f.Workspace.History.UndoCount);
    }

    [Fact]
    public async Task 完整槽位动画保槽位身份位置且非法策略与null完整拒绝()
    {
        await using Fixture f = await Fixture.Create();
        Accepted(await f.Command("0", new BasicLayoutCommandDto("primary", "KaraokeDouble", null, "two", ["a", "b"], "selected", "retained", false)));
        var model = await f.Model("1"); string target = model.Programs.Single(p => p.BranchId == "primary").DefaultContainerId;
        var original = model.Nodes.Single(n => n.Id == target).Layout!;
        var slots = model.Nodes.Where(n => original.SlotIds.Contains(n.Id)).ToArray();
        var changed = Accepted(await f.Command("1", new SetOrchestrationSlotAnimationCommandDto(target, Animation)));
        Assert.True(changed.Changed); Assert.Equal(2, f.Workspace.History.UndoCount);
        var after = await f.Model("2"); var layout = after.Nodes.Single(n => n.Id == target).Layout!;
        Assert.Equal(Animation, layout.SlotAnimation); Assert.Equal(original.SlotIds, layout.SlotIds);
        Assert.Equal(slots, after.Nodes.Where(n => original.SlotIds.Contains(n.Id)).ToArray());
        Project source = f.Workspace.Project;
        foreach (ProjectCommandDto invalid in new ProjectCommandDto[]
        {
            new SetOrchestrationSlotAnimationCommandDto(target, null),
            new SetOrchestrationProgramAnimationPolicyCommandDto("primary", "Static"),
            new SetOrchestrationSlotAnimationCommandDto(LyricSceneIds.Regular("primary"), Animation),
        })
        {
            var result = await f.Command("2", invalid);
            Assert.False(result.Accepted); Assert.Equal("PresentationInvalid", result.Error!.FailureCode);
            Assert.Same(source, f.Workspace.Project); Assert.Equal(2, f.Workspace.History.UndoCount);
        }
        var same = Accepted(await f.Command("2", new SetOrchestrationSlotAnimationCommandDto(target, Animation)));
        Assert.False(same.Changed);
        var snapshot = ProjectSnapshot.FromProject(source);
        var decoded = StaticProjectSnapshotCodec.Decode(StaticProjectSnapshotCodec.Encode(snapshot, StaticProjectSnapshotCodec.SelectProfile(snapshot)));
        Assert.Equal(source.LyricPresentation, decoded.LyricPresentation);
        string path = Path.Combine(f.DirectoryPath, "slot-properties.mkgproj"); f.Workspace.SaveAs(path);
        var reopened = ProjectWorkspace.CreateNew("slot-reopened"); Assert.True(reopened.Open(path).Loaded);
        Assert.Equal(source.LyricPresentation, reopened.Project.LyricPresentation);
        Accepted(await f.Command("2", new("history.undo")));
        Assert.Equal(original.SlotAnimation, (await f.Model("3")).Nodes.Single(n => n.Id == target).Layout!.SlotAnimation);
        var noop = Accepted(await f.Command("3", new SetOrchestrationSlotAnimationCommandDto(target, original.SlotAnimation)));
        Assert.False(noop.Changed); Assert.True(noop.CanRedo);
        Accepted(await f.Command("3", new("history.redo")));
        Assert.Equal(Animation, (await f.Model("4")).Nodes.Single(n => n.Id == target).Layout!.SlotAnimation);
    }

    [Fact]
    public async Task 指纹序列化前拒绝旧默认位置与新属性内存非有限数值且保留重做()
    {
        await using Fixture f = await Fixture.Create(); var model = await f.Model("0"); var program = model.Programs.Single(p => p.BranchId == "primary");
        Accepted(await f.Command("0", new SetOrchestrationNodeGeometryCommandDto(program.RootContainerId, Geometry, Rect)));
        Accepted(await f.Command("1", new("history.undo"))); Project before = f.Workspace.Project;
        foreach (double value in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, 0, -1 })
        {
            ProjectCommandDto[] commands = [new SetOrchestrationLayoutDefaultPlacementCommandDto(program.DefaultContainerId, Geometry with { Scale = value }),
                new SetOrchestrationNodeGeometryCommandDto(program.RootContainerId, Geometry with { Scale = value }, Rect),
                new SetOrchestrationNodeGeometryCommandDto(program.RootContainerId, Geometry, Rect with { WidthRatio = value })];
            foreach (var command in commands)
            {
                var result = await f.Command("2", command); Assert.Equal(BackendErrorCode.InvalidRequest, result.Error!.Code);
                Assert.Same(before, f.Workspace.Project); Assert.Equal(1, f.Workspace.History.RedoCount);
            }
        }
        foreach (var command in new ProjectCommandDto[]
        {
            new SetOrchestrationLayoutDefaultPlacementCommandDto(program.DefaultContainerId, Geometry with { X = double.NaN }),
            new SetOrchestrationLayoutDefaultPlacementCommandDto(program.DefaultContainerId, Geometry with { Y = double.PositiveInfinity }),
            new SetOrchestrationLayoutDefaultPlacementCommandDto(program.DefaultContainerId, Geometry with { RotationDegrees = double.NegativeInfinity }),
            new SetOrchestrationLayoutSettingsCommandDto(program.DefaultContainerId, Area, double.NaN),
            new SetOrchestrationLayoutSettingsCommandDto(program.DefaultContainerId, Area with { Bottom = double.PositiveInfinity }, .9),
            new SetOrchestrationSlotAnimationCommandDto(program.DefaultContainerId, Animation with { RevealProgress = double.NaN }),
            new SetOrchestrationProgramAnimationPolicyCommandDto("primary", "1"),
            new SetOrchestrationSlotAnimationCommandDto(program.DefaultContainerId, Animation with { RevealMode = "1" }),
        }) Assert.Equal(BackendErrorCode.InvalidRequest, (await f.Command("2", command)).Error!.Code);
        Assert.Equal(BackendErrorCode.RevisionConflict, (await f.Command("0", new SetOrchestrationNodeGeometryCommandDto(program.RootContainerId, Geometry, Rect))).Error!.Code);
        Assert.Equal("TargetStale", (await f.Command("2", new SetOrchestrationNodeGeometryCommandDto("absent", Geometry, Rect))).Error!.FailureCode);
        Assert.Equal("TargetStale", (await f.Command("2", new SetOrchestrationProgramAnimationPolicyCommandDto("absent", null))).Error!.FailureCode);
        Assert.Equal("TargetStale", (await f.Command("2", new SetOrchestrationLayoutSettingsCommandDto(program.RootContainerId, Area, .8))).Error!.FailureCode);
        Assert.Same(before, f.Workspace.Project); Assert.Equal(1, f.Workspace.History.RedoCount);
    }

    [Fact]
    public async Task 完整候选投影或回执超预算先拒绝且不产生历史()
    {
        await using Fixture f = await Fixture.Create(); var model = await f.Model("0"); var program = model.Programs.Single(p => p.BranchId == "primary");
        Project source = f.Workspace.Project; var tree = source.LyricPresentation;
        f.Workspace.Replace(source with { LyricPresentation = tree with { Nodes = tree.Nodes.SetItem(0,
            tree.Nodes[0] with { Name = new string('x', ProjectReadModelLimits.MaxResponseBytes + 1) }) } });
        var result = await f.Command("0", new SetOrchestrationNodeGeometryCommandDto(program.RootContainerId, Geometry, Rect));
        Assert.Equal(BackendErrorCode.ResourceTooLarge, result.Error!.Code); Assert.Equal(0, f.Workspace.History.UndoCount);
        f.Workspace.Replace(source);
        // A reduced receipt budget exercises precommit response accounting independently of the projection budget.
        typeof(ProjectSession).GetField("_branchReceiptMaxBytes", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(f.Session, 100);
        result = await f.Command("0", new SetOrchestrationNodeGeometryCommandDto(program.RootContainerId, Geometry, Rect));
        Assert.Equal(BackendErrorCode.ResourceTooLarge, result.Error!.Code); Assert.Same(source, f.Workspace.Project);
        Assert.Equal(0, f.Workspace.History.UndoCount);
    }

    [Fact]
    public async Task 同修订并发只提交一次且规范动画时长回执重试稳定()
    {
        await using Fixture f = await Fixture.Create(); var model = await f.Model("0"); var root = model.Programs.Single(p => p.BranchId == "primary").RootContainerId;
        var results = await Task.WhenAll(f.Command("0", new SetOrchestrationNodeGeometryCommandDto(root, Geometry, Rect)),
            f.Command("0", new SetOrchestrationNodeGeometryCommandDto(root, Geometry with { Scale = 2 }, Rect)));
        Assert.Single(results, r => r.Accepted); Assert.Equal(BackendErrorCode.RevisionConflict, Assert.Single(results, r => !r.Accepted).Error!.Code);
        Accepted(await f.Command("1", new BasicLayoutCommandDto("primary", "KaraokeDouble", null, "two", ["a", "b"], "selected", "retained", false)));
        string target = (await f.Model("2")).Programs.Single(p => p.BranchId == "primary").DefaultContainerId;
        var input = new SetOrchestrationSlotAnimationCommandDto(target, Animation with { RevealDelayUs = "0012000", RevealMode = "leadBeforeNextStart" });
        var request = Fixture.Request("2", input);
        var first = Accepted(await f.Registry.ExecuteCommandAsync("client", f.Id, request));
        var replay = Accepted(await f.Registry.ExecuteCommandAsync("client", f.Id, request with { Command = input with { SlotAnimation = Animation } }));
        Assert.Equal(first.ProjectRevision, replay.ProjectRevision); Assert.Equal(3, f.Workspace.History.UndoCount);
    }

    [Fact]
    public void 严格JSON完整形状命名枚举微秒字符串有限数值且null必须显式()
    {
        ProjectCommandDto[] valid = [new SetOrchestrationNodeGeometryCommandDto("node", Geometry, Rect),
            new SetOrchestrationLayoutSettingsCommandDto("node", Area, .8), new SetOrchestrationSlotAnimationCommandDto("node", Animation),
            new SetOrchestrationSlotAnimationCommandDto("node", null), new SetOrchestrationProgramAnimationPolicyCommandDto("primary", null)];
        foreach (var command in valid)
        {
            string raw = JsonSerializer.Serialize(command, Json);
            Assert.Equal(command, JsonSerializer.Deserialize<ProjectCommandDto>(raw, Json));
        }
        string geometry = JsonSerializer.Serialize<ProjectCommandDto>(valid[0], Json);
        string animation = JsonSerializer.Serialize<ProjectCommandDto>(valid[2], Json);
        foreach (string invalid in new[]
        {
            geometry.Replace("\"scale\":1.4", "\"scale\":1e400"), geometry.Replace("\"scale\":1.4", "\"scale\":0"),
            geometry.Replace("\"scale\":1.4", "\"scale\":1.4,\"scale\":2"), geometry.Replace("\"anchor\":\"TopLeft\"", "\"anchor\":\"1\""),
            geometry.Replace("\"widthRatio\":0.8", "\"widthRatio\":null"), geometry.Replace("\"widthRatio\":0.8", "\"widthRatio\":0.8,\"unknown\":0"),
            animation.Replace("\"revealDelayUs\":\"12000\"", "\"revealDelayUs\":12000"),
            animation.Replace("\"revealLeadUs\":\"23000\"", "\"revealLeadUs\":\"-1\""),
            animation.Replace("\"maxVisibleLines\":3", "\"maxVisibleLines\":3.5"), animation.Replace("\"revealMode\":\"LeadBeforeNextStart\"", "\"revealMode\":\"0\""),
            animation.Replace("\"revealProgress\":0.3", "\"revealProgress\":0.3,\"revealProgress\":0.5"),
            "{\"type\":\"orchestration.setProgramAnimationPolicy\",\"branchId\":\"primary\"}",
            "{\"type\":\"orchestration.setSlotAnimation\",\"containerId\":\"node\"}",
        }) Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ProjectCommandDto>(invalid, Json));
    }

    private static ProjectCommandResponse Accepted(ProjectCommandExecutionResult result)
    { Assert.True(result.Accepted, result.Error?.ToString()); return result.Response!; }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly TemporaryDirectory _directory = new();
        internal string DirectoryPath => _directory.Path;
        internal ProjectSessionRegistry Registry = null!;
        internal ProjectSession Session = null!;
        internal string Id => Session.ProjectSessionId;
        internal ProjectWorkspace Workspace => Session.Workspace;
        internal static ProjectCommandRequest Request(string revision, ProjectCommandDto command) =>
            new(revision, command, Guid.NewGuid().ToString("N"), "host", DateTimeOffset.UtcNow.AddMinutes(1));
        internal Task<ProjectCommandExecutionResult> Command(string revision, ProjectCommandDto command) => Registry.ExecuteCommandAsync("client", Id, Request(revision, command));
        internal async Task<ProjectOrchestrationResponse> Model(string revision) => (await Registry.ReadOrchestrationAsync("client", Id, revision)).Response!;
        internal static async Task<Fixture> Create()
        {
            Fixture f = new(); ResourceStore resources = new(new() { ResourceRoot = f._directory.Path });
            var audio = await resources.UploadAsync("client", "song.wav", "audio/wav", new MemoryStream([1]));
            var lyrics = await resources.UploadAsync("client", "song.lrc", "text/plain", new MemoryStream("[00:00.00]主\n"u8.ToArray()));
            WaveformPeakBuilder peaks = new(); peaks.Append(new float[24000]);
            LoadedWaveform waveform = new(new AudioStreamInfo(3000000, 8000, 1, "pcm", "wav"), peaks.Build(8000));
            f.Registry = new("host", resources, new((_, _) => Task.FromResult<LoadedWaveform?>(waveform)));
            f.Session = (await f.Registry.CreateAsync("client", new("properties", audio.AssetId, lyrics.AssetId))).Session!;
            return f;
        }
        public async ValueTask DisposeAsync() { await Registry.DisposeAsync(); _directory.Dispose(); }
    }
}
