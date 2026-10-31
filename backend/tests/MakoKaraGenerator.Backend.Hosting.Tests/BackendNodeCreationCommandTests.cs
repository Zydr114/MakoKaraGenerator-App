using System.Reflection;
using System.Text.Json;
using MakoKaraGenerator.Application;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Media;
using MakoKaraGenerator.TestSupport;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

public sealed class BackendNodeCreationCommandTests
{
    [Theory]
    [InlineData("group", null, 1)]
    [InlineData("auto", null, 1)]
    [InlineData("slotSequence", 1, 2)]
    [InlineData("slotSequence", 64, 65)]
    public async Task 闭合创建原子返回全部真实身份并使用默认几何(string kind, int? slots, int count)
    {
        await using Fixture f = await Fixture.Create();
        Project before = f.Workspace.Project;
        var response = Accepted(await f.Command("0", new CreateOrchestrationNodeCommandDto(kind, f.Root, null, slots)));
        var result = response.StructureResult!;
        Assert.Equal("createNode", result.Operation); Assert.True(result.Changed);
        Assert.Equal(count, result.CreatedNodeIds!.Length); Assert.Equal(result.NodeId, result.CreatedNodeIds[0]);
        Assert.Equal(count, result.CreatedNodeIds.Distinct(StringComparer.Ordinal).Count());
        var tree = f.Workspace.Project.LyricPresentation;
        var root = tree.FindNode(result.NodeId!)!;
        Assert.Equal(f.Root, root.ParentId); Assert.Null(root.Name);
        Assert.Equal(ScenePlacement.Default, root.Placement); Assert.Equal(SceneReferenceRect.Canvas, root.ReferenceRect);
        Assert.All(result.CreatedNodeIds, id => Assert.NotNull(tree.FindNode(id)));
        if (kind == "group") Assert.Null(root.Layout);
        else if (kind == "auto") Assert.Equal(new LyricContainerLayout(), root.Layout);
        else
        {
            Assert.Equal(LyricContainerLayoutKind.SlotSequence, root.Layout!.Kind);
            Assert.Equal(result.CreatedNodeIds.Skip(1), root.Layout.SlotIds);
            Assert.Equal(Math.Min(2, slots!.Value), root.Layout.SlotAnimation!.MaxVisibleLines);
            Assert.True(root.Layout.SlotAnimation.ToProfile(root.Layout.SlotIds.Select(id => tree.FindNode(id)!)).IsValid);
        }
        Assert.Same(before.Lyrics, f.Workspace.Project.Lyrics);
        Assert.Equal(before.LyricPresentation.Programs, tree.Programs);
        Assert.Equal(1, f.Workspace.History.UndoCount); Assert.Equal("1", response.ProjectRevision);
        Assert.Equal(["orchestration", "renderInputs"], response.ChangeSet.Areas);
        Assert.True(response.ChangeSet.RequiresFullRefresh);
        Accepted(await f.Command("1", new("history.undo"))); Assert.Equal(before, f.Workspace.Project);
        Accepted(await f.Command("2", new("history.redo"))); Assert.Equal(root, f.Workspace.Project.LyricPresentation.FindNode(root.Id));
    }

    [Fact]
    public async Task 根层创建复制重试回执隔离且撤销重做保持身份()
    {
        await using Fixture f = await Fixture.Create();
        var created = Accepted(await f.Command("0", new CreateOrchestrationNodeCommandDto("slotSequence", null, "原容器", 3)));
        string source = created.StructureResult!.NodeId!;
        var before = f.Workspace.Project;
        var request = Fixture.Request("1", new CopyOrchestrationSubtreeCommandDto(source, null));
        var copied = Accepted(await f.Registry.ExecuteCommandAsync("client", f.Id, request));
        var result = copied.StructureResult!; string root = result.NodeId!;
        Assert.Equal("copySubtree", result.Operation); Assert.Equal(4, result.CreatedNodeIds!.Length);
        Assert.Equal(root, result.CreatedNodeIds[0]); Assert.Null(f.Workspace.Project.LyricPresentation.FindNode(root)!.Name);
        Assert.Null(result.ParentId); Assert.Empty(created.StructureResult.CreatedNodeIds!.Intersect(result.CreatedNodeIds));
        Assert.Equal(before.LyricPresentation.Programs, f.Workspace.Project.LyricPresentation.Programs);
        Assert.Same(before.PresetApplications, f.Workspace.Project.PresetApplications);
        string[] fixedIds = result.CreatedNodeIds.ToArray(); result.CreatedNodeIds[0] = "polluted";
        var replay = Accepted(await f.Registry.ExecuteCommandAsync("client", f.Id, request));
        Assert.Equal(fixedIds, replay.StructureResult!.CreatedNodeIds); Assert.Equal("2", replay.ProjectRevision);
        Assert.Equal(2, f.Workspace.History.UndoCount);
        Accepted(await f.Command("2", new("history.undo"))); Assert.Equal(before, f.Workspace.Project);
        Accepted(await f.Command("3", new("history.redo")));
        Assert.All(fixedIds, id => Assert.NotNull(f.Workspace.Project.LyricPresentation.FindNode(id)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 槽位或标记叶副本追加外部拥有者引用且提示组复制重映射(bool mark)
    {
        await using Fixture f = await Fixture.Create();
        string owner;
        if (mark) owner = Accepted(await f.Command("0", new CreateOrchestrationCountdownCommandDto(f.Root, "提示", 3))).LeadInResult!.IndicatorContainerId!;
        else owner = Accepted(await f.Command("0", new CreateOrchestrationNodeCommandDto("slotSequence", f.Root, "序列", 3))).StructureResult!.NodeId!;
        var before = f.Workspace.Project;
        var originalOwner = before.LyricPresentation.FindNode(owner)!;
        string child = mark ? ((CountdownSequenceContent)originalOwner.Content!).MarkNodeIds[0] : originalOwner.Layout!.SlotIds[0];
        var leaf = Accepted(await f.Command("1", new CopyOrchestrationSubtreeCommandDto(child, "叶副本"))).StructureResult!;
        var tree = f.Workspace.Project.LyricPresentation;
        var changedOwner = tree.FindNode(owner)!;
        var ownerIds = mark ? ((CountdownSequenceContent)changedOwner.Content!).MarkNodeIds : changedOwner.Layout!.SlotIds;
        Assert.Equal(4, ownerIds.Length); Assert.Equal(leaf.NodeId, ownerIds[^1]);
        Assert.Equal(owner, tree.FindNode(leaf.NodeId!)!.ParentId);
        Assert.Equal(tree.FindNode(child)!.Content, tree.FindNode(leaf.NodeId!)!.Content);
        var copied = Accepted(await f.Command("2", new CopyOrchestrationSubtreeCommandDto(owner, "完整副本"))).StructureResult!;
        var copyOwner = f.Workspace.Project.LyricPresentation.FindNode(copied.NodeId!)!;
        var internalIds = mark ? ((CountdownSequenceContent)copyOwner.Content!).MarkNodeIds : copyOwner.Layout!.SlotIds;
        Assert.Equal(5, copied.CreatedNodeIds!.Length); Assert.Empty(ownerIds.Intersect(internalIds));
        Assert.All(internalIds, id => Assert.Equal(copyOwner.Id, f.Workspace.Project.LyricPresentation.FindNode(id)!.ParentId));
        Assert.Equal(before.LyricPresentation.Programs, f.Workspace.Project.LyricPresentation.Programs);
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("cycle")]
    [InlineData("parent")]
    [InlineData("slot")]
    [InlineData("mark")]
    [InlineData("unknown")]
    public async Task 坏源树明确拒绝不泄露异常或产生历史(string defect)
    {
        await using Fixture f = await Fixture.Create();
        var project = f.Workspace.Project; var tree = project.LyricPresentation;
        var root = tree.FindNode(f.Root)!;
        var broken = defect switch
        {
            "duplicate" => tree with { Nodes = tree.Nodes.Add(root) },
            "cycle" => tree with { Nodes = tree.Nodes.SetItem(tree.Nodes.IndexOf(root), root with { ParentId = root.Id }) },
            "parent" => tree with { Nodes = tree.Nodes.SetItem(tree.Nodes.IndexOf(root), root with { ParentId = "absent" }) },
            "slot" => tree with { Nodes = tree.Nodes.Add(new() { Id = "broken", ParentId = root.Id,
                Layout = new() { Kind = LyricContainerLayoutKind.SlotSequence, SlotIds = ["absent"], SlotAnimation = new() } }) },
            "mark" => tree with { Nodes = tree.Nodes.Add(new() { Id = "broken", ParentId = root.Id,
                Content = new CountdownSequenceContent { MarkNodeIds = ["absent"] } }) },
            _ => tree with { Nodes = tree.Nodes.Add(new() { Id = "broken", Content = new UnknownContent() }) },
        };
        f.Workspace.Replace(project with { LyricPresentation = broken });
        Project before = f.Workspace.Project;
        foreach (ProjectCommandDto command in new ProjectCommandDto[] { new CreateOrchestrationNodeCommandDto("group", null, null, null), new CopyOrchestrationSubtreeCommandDto(root.Id, null) })
        {
            var rejected = await f.Command("0", command);
            Assert.Equal(BackendErrorCode.CommandRejected, rejected.Error!.Code); Assert.Equal("PresentationInvalid", rejected.Error.FailureCode);
            Assert.Same(before, f.Workspace.Project); Assert.Equal(0, f.Workspace.History.UndoCount);
        }
    }

    [Fact]
    public async Task 创建父目标与参数不合法时保留重做且同修订并发仅成功一次()
    {
        await using Fixture f = await Fixture.Create();
        var node = Accepted(await f.Command("0", new CreateOrchestrationNodeCommandDto("auto", f.Root, "自动", null))).StructureResult!.NodeId!;
        Assert.Equal("ContainerInvalid", (await f.Command("1", new CreateOrchestrationNodeCommandDto("group", node, null, null))).Error!.FailureCode);
        Assert.Equal("TargetStale", (await f.Command("1", new CreateOrchestrationNodeCommandDto("group", "missing", null, null))).Error!.FailureCode);
        Assert.Equal("TargetStale", (await f.Command("1", new CopyOrchestrationSubtreeCommandDto("missing", null))).Error!.FailureCode);
        Accepted(await f.Command("1", new("history.undo")));
        foreach (ProjectCommandDto invalid in new ProjectCommandDto[]
        {
            new CreateOrchestrationNodeCommandDto("Auto", null, null, null), new CreateOrchestrationNodeCommandDto("group", null, null, 1),
            new CreateOrchestrationNodeCommandDto("slotSequence", null, null, 0), new CreateOrchestrationNodeCommandDto("slotSequence", null, null, 65),
            new CopyOrchestrationSubtreeCommandDto(f.Root, new string('x', 257)),
            new CreateOrchestrationNodeCommandDto("group", null, null, null) { StartUs = "0" },
        }) Assert.Equal(BackendErrorCode.InvalidRequest, (await f.Command("2", invalid)).Error!.Code);
        Assert.True(f.Workspace.CanRedo); Assert.Equal(0, f.Workspace.History.UndoCount);
        var concurrent = await Task.WhenAll(f.Command("2", new CreateOrchestrationNodeCommandDto("group", null, null, null)),
            f.Command("2", new CopyOrchestrationSubtreeCommandDto(f.Root, null)));
        Assert.Single(concurrent, r => r.Accepted);
        Assert.Equal(BackendErrorCode.RevisionConflict, Assert.Single(concurrent, r => !r.Accepted).Error!.Code);
    }

    [Theory]
    [InlineData("nodes")]
    [InlineData("implicit")]
    [InlineData("depth")]
    [InlineData("copy")]
    [InlineData("items")]
    [InlineData("lyrics")]
    [InlineData("text")]
    [InlineData("encodedText")]
    [InlineData("receipt")]
    public async Task 完整候选或回执超限在提交前拒绝(string budget)
    {
        await using Fixture f = await Fixture.Create(budget == "receipt" ? 1024 : 65536);
        var project = f.Workspace.Project; var tree = project.LyricPresentation;
        ProjectCommandDto command = new CreateOrchestrationNodeCommandDto("group", null, null, null);
        if (budget is "nodes" or "implicit")
            tree = tree with { Nodes = tree.Nodes.AddRange(Enumerable.Range(0, 2000 - tree.Nodes.Length).Select(i => new SceneContainerDefinition { Id = "extra/" + i })) };
        if (budget == "implicit") project = project with { Lyrics = project.Lyrics with
            { Branches = project.Lyrics.Branches.Add(project.Lyrics.PrimaryBranch! with { Id = "implicit-source" }) } };
        if (budget == "depth")
        {
            tree = tree with { Nodes = tree.Nodes.AddRange(Enumerable.Range(1, 64).Select(i => new SceneContainerDefinition { Id = "deep/" + i, ParentId = i == 1 ? null : "deep/" + (i - 1) })) };
            command = new CreateOrchestrationNodeCommandDto("group", "deep/64", null, null);
        }
        if (budget == "copy")
        {
            tree = tree with { Nodes = tree.Nodes.AddRange(Enumerable.Range(0, 129).Select(i => new SceneContainerDefinition { Id = "copy/" + i, ParentId = i == 0 ? null : "copy/0" })) };
            command = new CopyOrchestrationSubtreeCommandDto("copy/0", null);
        }
        if (budget == "items") tree = tree with { LineOverrides = [.. Enumerable.Range(0, 20001).Select(i => new LyricLinePresentation { BranchId = "primary", LineId = "line/" + i })] };
        if (budget == "lyrics")
        {
            var branch = project.Lyrics.PrimaryBranch!; var line = branch.Lines[0];
            project = project with { Lyrics = project.Lyrics with { Branches = project.Lyrics.Branches.SetItem(0, branch with
            { Lines = branch.Lines.SetItem(0, line with { Tokens = [.. Enumerable.Range(0, OrchestrationNodeCreationLimits.MaxInputItems + 1)
                .Select(i => line.Tokens[0] with { Id = "token/" + i })] }) }) } };
        }
        if (budget == "text") tree = tree with { Nodes = tree.Nodes.SetItem(0, tree.Nodes[0] with { Name = new string('x', ProjectReadModelLimits.MaxResponseBytes + 1) }) };
        if (budget == "encodedText") tree = tree with { Nodes = tree.Nodes.SetItem(0, tree.Nodes[0] with { Name = new string('中', 200000) }) };
        if (budget == "receipt") command = new CreateOrchestrationNodeCommandDto("slotSequence", f.Root, null, 64);
        f.Workspace.Replace(project with { LyricPresentation = tree });
        var before = f.Workspace.Project;
        var rejected = await f.Command("0", command);
        Assert.Equal(BackendErrorCode.ResourceTooLarge, rejected.Error!.Code); Assert.Null(rejected.Error.FailureCode);
        Assert.Same(before, f.Workspace.Project); Assert.Equal(0, f.Workspace.History.UndoCount);
        Assert.Equal("0", (await f.Registry.ReadSummaryAsync("client", f.Id))!.ProjectRevision);
    }

    [Fact]
    public async Task 复制一百二十八节点完整回显且旧结构回执不输出新增字段()
    {
        await using Fixture f = await Fixture.Create();
        var project = f.Workspace.Project;
        f.Workspace.Replace(project with { LyricPresentation = project.LyricPresentation with { Nodes = project.LyricPresentation.Nodes.AddRange(
            Enumerable.Range(0, 128).Select(i => new SceneContainerDefinition { Id = "copy/" + i, ParentId = i == 0 ? null : "copy/0" })) } });
        var copied = Accepted(await f.Command("0", new CopyOrchestrationSubtreeCommandDto("copy/0", null)));
        Assert.Equal(128, copied.StructureResult!.CreatedNodeIds!.Length);
        var old = Accepted(await f.Command("1", new SetOrchestrationNodeCommandDto("copy/0", "名字")));
        Assert.DoesNotContain("createdNodeIds", JsonSerializer.Serialize(old, JsonSerializerOptions.Web), StringComparison.Ordinal);
    }

    private sealed record UnknownContent : SceneContent;
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
            f.Session = (await f.Registry.CreateAsync("client", new("nodes", audio.AssetId, lyrics.AssetId))).Session!;
            return f;
        }
        internal static ProjectCommandRequest Request(string revision, ProjectCommandDto command) =>
            new(revision, command, Guid.NewGuid().ToString("N"), "host", DateTimeOffset.UtcNow.AddMinutes(1));
        internal Task<ProjectCommandExecutionResult> Command(string revision, ProjectCommandDto command) => Registry.ExecuteCommandAsync("client", Id, Request(revision, command));
        public async ValueTask DisposeAsync() { await Registry.DisposeAsync(); _directory.Dispose(); }
    }
}
