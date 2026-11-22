using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using MakoKaraGenerator.Application;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Media;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.TestSupport;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

public sealed class BackendEditingCompletionTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly JsonSerializerOptions OmitJson = new(Json) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    private static readonly ProjectLineEnterAnimationDto Animation = new("400000", "EaseOut", .25f, .8f, 12);

    [Fact]
    public async Task 布局切换保留全部旧节点几何内部设置且无变化保留重做()
    {
        await using Fixture f = await Fixture.Create();
        var source = f.Workspace.Project;
        var tree = source.LyricPresentation.WithDefaultPrograms(source.Lyrics);
        string container = tree.FindProgram("primary")!.DefaultContainerId;
        string oldId = "slot/existing";
        var placement = ScenePlacement.Create(new(.2, .7), ScenePlacementAnchor.BottomRight, 12, 1.2);
        var node = tree.FindNode(container)!;
        tree = tree with { Nodes = tree.Nodes.SetItem(tree.Nodes.IndexOf(node), node with { Layout = node.Layout! with { MaxWidthRatio = .65 } })
            .Add(new() { Id = oldId, ParentId = container, Placement = placement, ReferenceRect = new(.5, .6) }) };
        f.Workspace.Replace(source with { LyricPresentation = tree });
        Accept(await f.Command("0", new SetOrchestrationLayoutKindCommandDto(container, "SlotSequence", [oldId], 1)));
        var sequence = f.Workspace.Project.LyricPresentation.FindNode(container)!.Layout!;
        Assert.Equal(2, sequence.SlotIds.Length); Assert.Equal(oldId, sequence.SlotIds[0]); Assert.Equal(2, sequence.SlotAnimation!.MaxVisibleLines);
        Assert.Equal(.65, sequence.MaxWidthRatio); Assert.Equal(placement, f.Workspace.Project.LyricPresentation.FindNode(oldId)!.Placement);
        Accept(await f.Command("1", new SetOrchestrationLayoutKindCommandDto(container, "Auto", [], 0)));
        Assert.Equal(LyricContainerLayoutKind.Auto, f.Workspace.Project.LyricPresentation.FindNode(container)!.Layout!.Kind);
        Assert.All(sequence.SlotIds, id => Assert.NotNull(f.Workspace.Project.LyricPresentation.FindNode(id)));
        Accept(await f.Command("2", new SetOrchestrationLayoutKindCommandDto(container, "SlotSequence", sequence.SlotIds.ToArray(), 0)));
        var final = f.Workspace.Project;
        Accept(await f.Command("3", new ProjectCommandDto("history.undo")));
        var noop = Accept(await f.Command("4", new SetOrchestrationLayoutKindCommandDto(container, "Auto", [], 0)));
        Assert.False(noop.Changed); Assert.True(noop.CanRedo); Assert.Equal("4", noop.ProjectRevision);
        Accept(await f.Command("4", new ProjectCommandDto("history.redo")));
        Assert.Equal(final, f.Workspace.Project); Assert.Equal(placement, f.Workspace.Project.LyricPresentation.FindNode(oldId)!.Placement);
        Assert.Same(source.Lyrics, f.Workspace.Project.Lyrics);
    }

    [Fact]
    public async Task 已有非空槽位同序列配置无变化且保留重做()
    {
        await using Fixture f = await Fixture.Create();
        string container = f.Workspace.Project.LyricPresentation.FindProgram("primary")!.DefaultContainerId;
        Accept(await f.Command("0", new SetOrchestrationLayoutKindCommandDto(container, "SlotSequence", [], 2)));
        var sequence = f.Workspace.Project.LyricPresentation.FindNode(container)!.Layout!.SlotIds.ToArray();
        Accept(await f.Command("1", new SetProjectEnterAnimationCommandDto(Animation)));
        Accept(await f.Command("2", new ProjectCommandDto("history.undo")));
        var source = f.Workspace.Project;
        var noop = Accept(await f.Command("3", new SetOrchestrationLayoutKindCommandDto(container, "SlotSequence", sequence, 0)));
        Assert.False(noop.Changed); Assert.True(noop.CanRedo); Assert.Same(source, f.Workspace.Project);
        Assert.Equal(1, f.Workspace.History.UndoCount); Assert.Equal(1, f.Workspace.History.RedoCount);
    }

    [Fact]
    public async Task 稳定来源本句动画继承清除不影响微调或其他来源()
    {
        await using Fixture f = await Fixture.Create();
        Accept(await f.Command("0", new AddTranslationBranchCommandDto("翻译", null)));
        var branch = f.Workspace.Project.Lyrics.Branches.Single(value => value.Id != "primary");
        Accept(await f.Command("1", new AddLineCommandDto(branch.Id, null)));
        branch = f.Workspace.Project.Lyrics.Branches.Single(value => value.Id == branch.Id);
        string line = branch.Lines[0].Id;
        var transform = new ProjectLineLocalTransformDto(.1, -.2, 15, 1.5);
        Accept(await f.Command("2", new SetLineLocalTransformCommandDto(branch.Id, line, transform)));
        Accept(await f.Command("3", new SetProjectEnterAnimationCommandDto(Animation)));
        var inherited = (await f.Registry.ReadLinePresentationAsync("client", f.Id, "4", branch.Id, line)).Response!;
        Assert.Equal("project", inherited.AnimationSource); Assert.Null(inherited.LocalEnterAnimation); Assert.Equal(Animation, inherited.EffectiveEnterAnimation);
        var neutral = Animation with { DurationUs = "0", StartOpacity = 1, StartScale = 1, StartOffsetPx = 0 };
        Accept(await f.Command("4", new SetLineEnterAnimationCommandDto(branch.Id, line, neutral)));
        var local = (await f.Registry.ReadLinePresentationAsync("client", f.Id, "5", branch.Id, line)).Response!;
        Assert.Equal("line", local.AnimationSource); Assert.Equal(neutral, local.LocalEnterAnimation); Assert.Equal(neutral, local.EffectiveEnterAnimation);
        Assert.Equal(transform, local.LocalTransform);
        Assert.Null(f.Workspace.Project.LyricPresentation.FindLine(f.LineId));
        Accept(await f.Command("5", new SetLineEnterAnimationCommandDto(branch.Id, line, null)));
        Assert.Equal(transform, (await f.Registry.ReadLinePresentationAsync("client", f.Id, "6", branch.Id, line)).Response!.LocalTransform);
        Assert.NotNull(f.Workspace.Project.LyricPresentation.FindLine(line));
        Accept(await f.Command("6", new SetLineLocalTransformCommandDto(branch.Id, line, null)));
        Assert.Null(f.Workspace.Project.LyricPresentation.FindLine(line));
        Accept(await f.Command("7", new SetProjectEnterAnimationCommandDto(null)));
        var cleared = (await f.Registry.ReadLinePresentationAsync("client", f.Id, "8", branch.Id, line)).Response!;
        Assert.Equal("none", cleared.AnimationSource); Assert.Null(cleared.EffectiveEnterAnimation);
    }

    [Fact]
    public async Task 动画非法输入失效目标完整回执预算拒绝不污染历史且规范化重试()
    {
        await using Fixture f = await Fixture.Create();
        var valid = Accept(await f.Command("0", new SetLineEnterAnimationCommandDto("primary", f.LineId, Animation)));
        Assert.Equal("1", valid.ProjectRevision);
        Accept(await f.Command("1", new ProjectCommandDto("history.undo")));
        Project source = f.Workspace.Project;
        foreach (ProjectCommandDto command in new ProjectCommandDto[]
        {
            new SetLineEnterAnimationCommandDto("absent", f.LineId, Animation),
            new SetLineEnterAnimationCommandDto("primary", "absent", Animation),
            new SetProjectEnterAnimationCommandDto(Animation with { DurationUs = "10000001" }),
            new SetProjectEnterAnimationCommandDto(Animation with { StartScale = float.NaN }),
            new SetProjectEnterAnimationCommandDto(Animation with { StartOpacity = float.PositiveInfinity }),
            new SetProjectEnterAnimationCommandDto(Animation with { StartOffsetPx = 10001 }),
            new SetProjectEnterAnimationCommandDto(Animation with { Easing = "easeout" }),
            new SetProjectEnterAnimationCommandDto(Animation) with { Type = "history.undo" },
        })
        {
            var rejected = await f.Command("2", command); Assert.False(rejected.Accepted);
            Assert.Same(source, f.Workspace.Project); Assert.Equal(0, f.Workspace.History.UndoCount); Assert.Equal(1, f.Workspace.History.RedoCount);
        }
        var noop = Accept(await f.Command("2", new SetLineEnterAnimationCommandDto("primary", f.LineId, null)));
        Assert.False(noop.Changed); Assert.True(noop.CanRedo);
        Assert.Equal(BackendErrorCode.SessionNotFound, (await f.Registry.ReadLinePresentationAsync("foreign", f.Id, "2", "primary", f.LineId)).Error!.Code);
        Assert.Equal(BackendErrorCode.RevisionConflict, (await f.Registry.ReadLinePresentationAsync("client", f.Id, "0", "primary", f.LineId)).Error!.Code);
        string requestId = Guid.NewGuid().ToString("N");
        var request = new ProjectCommandRequest("2", new SetProjectEnterAnimationCommandDto(Animation), requestId, "host", DateTimeOffset.UtcNow.AddMinutes(1));
        var first = await f.Registry.ExecuteCommandAsync("client", f.Id, request);
        var retry = await f.Registry.ExecuteCommandAsync("client", f.Id, request with { Command = new SetProjectEnterAnimationCommandDto(Animation with { DurationUs = "+0400000" }) });
        Assert.Equal(JsonSerializer.Serialize(Accept(first), Json), JsonSerializer.Serialize(Accept(retry), Json));
    }

    [Theory]
    [InlineData("receipt")]
    [InlineData("candidate")]
    [InlineData("projection")]
    public async Task 布局动画完整预算在一次历史前检查(string budget)
    {
        await using Fixture f = await Fixture.Create();
        var source = f.Workspace.Project; var tree = source.LyricPresentation.WithDefaultPrograms(source.Lyrics);
        string container = tree.FindProgram("primary")!.DefaultContainerId;
        if (budget == "candidate") tree = tree with { Nodes = tree.Nodes.AddRange(Enumerable.Range(0, 2000 - tree.Nodes.Length).Select(i => new SceneContainerDefinition { Id = "budget/" + i })) };
        if (budget == "projection") tree = tree with { Nodes = tree.Nodes.SetItem(0, tree.Nodes[0] with { Name = new string('中', 200000) }) };
        f.Workspace.Replace(source with { LyricPresentation = tree }); source = f.Workspace.Project;
        if (budget == "receipt") typeof(ProjectSession).GetField("_branchReceiptMaxBytes", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(f.Session, 100);
        ProjectCommandDto command = budget == "candidate" ? new SetOrchestrationLayoutKindCommandDto(container, "SlotSequence", [], 1)
            : new SetProjectEnterAnimationCommandDto(Animation);
        var rejected = await f.Command("0", command);
        Assert.False(rejected.Accepted); Assert.Equal(BackendErrorCode.ResourceTooLarge, rejected.Error!.Code); Assert.Null(rejected.Error.FailureCode);
        Assert.Same(source, f.Workspace.Project); Assert.Equal(0, f.Workspace.History.UndoCount);
    }

    [Fact]
    public async Task 布局错误成员内容与静态来源拒绝保持工程和历史()
    {
        await using Fixture f = await Fixture.Create();
        var source = f.Workspace.Project; var tree = source.LyricPresentation.WithDefaultPrograms(source.Lyrics);
        string container = tree.FindProgram("primary")!.DefaultContainerId;
        f.Workspace.Replace(source with { LyricPresentation = tree }); source = f.Workspace.Project;
        foreach (ProjectCommandDto command in new ProjectCommandDto[]
        {
            new SetOrchestrationLayoutKindCommandDto("missing", "Auto", [], 0),
            new SetOrchestrationLayoutKindCommandDto(tree.FindProgram("primary")!.RootContainerId, "Auto", [], 0),
            new SetOrchestrationLayoutKindCommandDto(container, "SlotSequence", ["missing"], 0),
            new SetOrchestrationLayoutKindCommandDto(container, "SlotSequence", [tree.FindProgram("primary")!.RootContainerId], 0),
            new SetOrchestrationLayoutKindCommandDto(container, "SlotSequence", [], 65),
            new SetOrchestrationLayoutKindCommandDto(container, "Auto", [], 1),
        })
        {
            var rejected = await f.Command("0", command); Assert.False(rejected.Accepted);
            Assert.Same(source, f.Workspace.Project); Assert.Equal(0, f.Workspace.History.UndoCount);
        }
        Accept(await f.Command("0", new SetOrchestrationProgramAnimationPolicyCommandDto("primary", "Static")));
        source = f.Workspace.Project;
        var staticRejected = await f.Command("1", new SetOrchestrationLayoutKindCommandDto(container, "SlotSequence", [], 1));
        Assert.False(staticRejected.Accepted); Assert.Equal("PresentationInvalid", staticRejected.Error!.FailureCode);
        Assert.Same(source, f.Workspace.Project); Assert.Equal(1, f.Workspace.History.UndoCount);
    }

    [Fact]
    public void 本句读投影显式输出空值且Core稳定目标修改保留本句微调()
    {
        using JsonDocument doc = JsonDocument.Parse(JsonSerializer.Serialize(new ProjectLinePresentationResponse("s", "0", "b", "l", null, null, null, "none"), OmitJson));
        foreach (string name in new[] { "localTransform", "localEnterAnimation", "effectiveEnterAnimation" })
            Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty(name).ValueKind);
    }

    private static ProjectCommandResponse Accept(ProjectCommandExecutionResult result)
    { Assert.True(result.Accepted, result.Error?.Message); return result.Response!; }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly TemporaryDirectory _directory = new();
        internal ProjectSessionRegistry Registry = null!;
        internal ProjectSession Session = null!;
        internal string Id => Session.ProjectSessionId;
        internal ProjectWorkspace Workspace => Session.Workspace;
        internal string LineId => Workspace.Project.Lyrics.Lines[0].Id;
        internal Task<ProjectCommandExecutionResult> Command(string revision, ProjectCommandDto command) => Registry.ExecuteCommandAsync("client", Id,
            new(revision, command, Guid.NewGuid().ToString("N"), Session.HostInstanceId, DateTimeOffset.UtcNow.AddMinutes(1)));
        internal static async Task<Fixture> Create()
        {
            Fixture f = new(); ResourceStore resources = new(new() { ResourceRoot = f._directory.Path });
            var audio = await resources.UploadAsync("client", "song.wav", "audio/wav", new MemoryStream([1]));
            var lyrics = await resources.UploadAsync("client", "song.lrc", "text/plain", new MemoryStream("[00:00.00]主\n"u8.ToArray()));
            WaveformPeakBuilder peaks = new(); peaks.Append(new float[24000]);
            LoadedWaveform waveform = new(new AudioStreamInfo(3000000, 8000, 1, "pcm", "wav"), peaks.Build(8000));
            f.Registry = new("host", resources, new((_, _) => Task.FromResult<LoadedWaveform?>(waveform)));
            f.Session = (await f.Registry.CreateAsync("client", new("completion", audio.AssetId, lyrics.AssetId))).Session!;
            return f;
        }
        public async ValueTask DisposeAsync() { await Registry.DisposeAsync(); _directory.Dispose(); }
    }
}
