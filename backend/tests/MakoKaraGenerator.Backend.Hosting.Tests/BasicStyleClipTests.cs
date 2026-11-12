using System.Reflection;
using System.Text.Json;
using MakoKaraGenerator.Application;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Media;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.TestSupport;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

public sealed class BasicStyleClipTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    [Fact]
    public async Task 片段完整成员扩缩拆合一HTTP一步历史重放及保存重开()
    {
        await using Fixture f = await Fixture.Create();
        var created = Ok(await f.Command("0", new CreateOrchestrationClipCommandDto("primary", "clip/a", "段", ["line/1", "line/2"], f.Container)));
        Assert.Equal("1", created.ProjectRevision);
        var grown = Ok(await f.Command("1", new SetOrchestrationClipMembersCommandDto("primary", "clip/a", ["line/0", "line/1", "line/2", "line/3"])));
        Assert.Equal(4, Assert.Single(grown.ClipMembersResult!.Clips).LineIds.Length);
        var shrunk = Ok(await f.Command("2", new SetOrchestrationClipMembersCommandDto("primary", "clip/a", ["line/1", "line/2", "line/3"])));
        Assert.Equal("3", shrunk.ProjectRevision);
        Project beforeSplit = f.Workspace.Project;
        var request = f.Request("3", new SplitOrchestrationClipCommandDto("primary", "clip/a", "line/2", "clip/b"));
        var split = Ok(await f.Registry.ExecuteCommandAsync("client", f.Id, request));
        Assert.Equal(2, split.ClipMembersResult!.Clips.Length); Assert.Equal("clip/a", split.ClipMembersResult.Clips[0].Id);
        Assert.Equal(["line/1"], split.ClipMembersResult.Clips[0].LineIds);
        Assert.Equal(["line/2", "line/3"], split.ClipMembersResult.Clips[1].LineIds);
        Assert.All(split.ClipMembersResult.Clips, clip => Assert.Equal(["line/1", "line/2", "line/3"], clip.ScheduleContext!.LineIds));
        var replay = Ok(await f.Registry.ExecuteCommandAsync("client", f.Id, request));
        Assert.Equal(JsonSerializer.Serialize(split), JsonSerializer.Serialize(replay)); Assert.Equal(4, f.Workspace.History.UndoCount);
        var merged = Ok(await f.Command("4", new MergeOrchestrationClipsCommandDto("primary", ["clip/b", "clip/a"], "clip/a")));
        Assert.Equal("5", merged.ProjectRevision); Assert.Equal(["clip/b"], merged.ClipMembersResult!.RemovedClipIds);
        Assert.Equal(["line/1", "line/2", "line/3"], Assert.Single(merged.ClipMembersResult.Clips).LineIds);
        var final = f.Workspace.Project;
        string path = Path.Combine(f.Directory, "members.mkgproj"); f.Workspace.SaveAs(path);
        var reopened = ProjectWorkspace.CreateNew("reopened"); Assert.True(reopened.Open(path).Loaded);
        Assert.Equal(final.LyricPresentation, reopened.Project.LyricPresentation);
        Ok(await f.Command("5", new("history.undo")));
        var noop = Ok(await f.Command("6", new SetOrchestrationClipMembersCommandDto("primary", "clip/a", ["line/1"])));
        Assert.False(noop.Changed); Assert.True(noop.CanRedo); Assert.Equal("6", noop.ProjectRevision);
        Ok(await f.Command("6", new("history.redo"))); Assert.Equal(final.LyricPresentation, f.Workspace.Project.LyricPresentation);
        Assert.Same(beforeSplit.Lyrics, f.Workspace.Project.Lyrics);
    }

    [Fact]
    public async Task 片段非法完整范围重叠与调度上下文扩展原子拒绝()
    {
        await using Fixture f = await Fixture.Create();
        Ok(await f.Command("0", new CreateOrchestrationClipCommandDto("primary", "clip/a", null, ["line/0", "line/1"], f.Container)));
        Ok(await f.Command("1", new CreateOrchestrationClipCommandDto("primary", "clip/c", null, ["line/3"], f.Container)));
        Project before = f.Workspace.Project;
        foreach (string[] ids in new[] { new[] { "line/0", "line/2" }, new[] { "line/1", "line/0" }, new[] { "line/0", "line/1", "line/2", "line/3" }, new[] { "absent" } })
        {
            Assert.False((await f.Command("2", new SetOrchestrationClipMembersCommandDto("primary", "clip/a", ids))).Accepted);
            Assert.Same(before, f.Workspace.Project); Assert.Equal(2, f.Workspace.History.UndoCount);
        }
        Ok(await f.Command("2", new SplitOrchestrationClipCommandDto("primary", "clip/a", "line/1", "clip/b")));
        Assert.Equal("ScheduleContextConflict", (await f.Command("3", new SetOrchestrationClipMembersCommandDto("primary", "clip/b", ["line/1", "line/2"]))).Error!.FailureCode);
        Assert.Equal("ScheduleContextConflict", (await f.Command("3", new MergeOrchestrationClipsCommandDto("primary", ["clip/b", "clip/c"], "clip/b"))).Error!.FailureCode);
        Assert.Equal("ClipExists", (await f.Command("3", new SplitOrchestrationClipCommandDto("primary", "clip/a", "line/0", "clip/b"))).Error!.FailureCode);
        Assert.Equal(BackendErrorCode.RevisionConflict, (await f.Command("1", new SetOrchestrationClipMembersCommandDto("primary", "clip/a", ["line/0"]))).Error!.Code);
    }

    [Fact]
    public async Task 静态片段预设拆合保留目标身份反馈消耗身份撤销完整恢复()
    {
        await using Fixture f = await Fixture.Create();
        Ok(await f.Command("0", new CreateOrchestrationClipCommandDto("primary", "clip/a", null, ["line/0", "line/1", "line/2"], f.Container)));
        var template = StyleTemplate.Create("style/fixed", "固定", StyleTemplateKind.Composite, f.Workspace.Project.LyricStyle with { Opacity = .4f }, [LyricStyleProperty.Opacity]);
        Assert.True(PresetApplicationsEditor.TryApply(f.Workspace.Project, template, [new(ProjectEffectBindingKind.Clips, ids: ["clip/a"])], null, out Project styled, out string reason), reason);
        f.Workspace.Replace(styled);
        string retained = Assert.Single(styled.PresetApplications.Items).Id;
        var split = Ok(await f.Command("1", new SplitOrchestrationClipCommandDto("primary", "clip/a", "line/1", "clip/b")));
        string consumed = Assert.Single(split.ClipMembersResult!.CreatedApplicationIds);
        Assert.Equal(retained, f.Workspace.Project.PresetApplications.Items[0].Id);
        var beforeMerge = f.Workspace.Project;
        var merged = Ok(await f.Command("2", new MergeOrchestrationClipsCommandDto("primary", ["clip/a", "clip/b"], "clip/a")));
        Assert.Equal([consumed], merged.ClipMembersResult!.RemovedApplicationIds);
        Assert.Equal(retained, Assert.Single(f.Workspace.Project.PresetApplications.Items).Id);
        Assert.Same(template, f.Workspace.Project.PresetApplications.Items[0].Snapshot);
        Ok(await f.Command("3", new("history.undo")));
        Assert.Equal(beforeMerge.PresetApplications, f.Workspace.Project.PresetApplications);
        Assert.Equal(beforeMerge.LyricPresentation, f.Workspace.Project.LyricPresentation);
        var partial = PresetApplicationsEditor.Remove(f.Workspace.Project, [new(ProjectEffectBindingKind.Clips, ids: ["clip/b"])]);
        f.Workspace.Replace(partial);
        Assert.Equal("ReferenceConflict", (await f.Command("4", new MergeOrchestrationClipsCommandDto("primary", ["clip/a", "clip/b"], "clip/a"))).Error!.FailureCode);
        Assert.Same(partial, f.Workspace.Project);
    }

    [Fact]
    public async Task 静态最终解释使用真实槽位路径完整来源并保留旧四层读模型()
    {
        await using Fixture f = await Fixture.Create();
        Project source = f.Workspace.Project;
        Assert.True(BasicLayoutOperation.TryPreview(source, "primary", BasicLayoutPreset.KaraokeDouble, null,
            new("双行", ["一", "二"], "选", "留"), out var layout, out var layoutError), layoutError?.ToString());
        source = layout.Result;
        var tree = source.LyricPresentation; var program = tree.FindProgram("primary")!;
        string container = program.DefaultContainerId;
        var branch = source.Lyrics.Branches[0];
        var occurrence = LyricDisplayScheduleBuilder.Build(branch, program, tree).Single(item => item.Line.Id == "line/1");
        string slot = tree.FindNode(container)!.Layout!.SlotIds[occurrence.SlotIndex];
        Assert.True(SceneTreeEditor.TrySetClip(source, "primary", new() { Id = "clip/style", ContainerId = container, LineIds = ["line/0", "line/1", "line/2", "line/3"] }, out source, out _));
        var targets = new[] { new ProjectEffectBinding(ProjectEffectBindingKind.Project), new(ProjectEffectBindingKind.Branch, id: "primary"),
            new(ProjectEffectBindingKind.Node, id: container, includeDescendants: true), new(ProjectEffectBindingKind.Node, id: slot),
            new(ProjectEffectBindingKind.Clips, ids: ["clip/style"]), new(ProjectEffectBindingKind.Lines, ids: ["line/1"]), new(ProjectEffectBindingKind.Tokens, ids: ["token/1"]) };
        for (int index = 0; index < targets.Length; index++)
        {
            var template = StyleTemplate.Create("style/" + index, "层" + index, StyleTemplateKind.Composite,
                source.LyricStyle with { Opacity = .1f * (index + 1), FontSizePx = 40 + index },
                targets[index].Kind == ProjectEffectBindingKind.Tokens ? [LyricStyleProperty.Opacity]
                    : [LyricStyleProperty.Opacity, LyricStyleProperty.FontSizePx]);
            Assert.True(PresetApplicationsEditor.TryApply(source, template, [targets[index]], null, out source, out string reason), reason);
        }
        f.Workspace.Replace(source);
        var result = await f.Registry.ReadResolvedStyleAsync("client", f.Id, "0", "primary", "line/1", "token/1");
        Assert.True(result.Accepted, result.Error?.ToString()); var model = result.Response!;
        Assert.Equal("scheduledSlot", model.Context.ContextKind); Assert.Equal(slot, model.Context.NodeId);
        Assert.Equal("clip/style", model.Context.ClipId); Assert.False(model.FinalPixelAppearance);
        var expected = new PresetStyleResolver(source.LyricStyle, source.Lyrics, source.PresetApplications)
            .ResolveLyrics("line/1", "token/1", "clip/style", model.Context.NodePath);
        Assert.Equal(expected.Style.Opacity, model.Style.Opacity); Assert.Equal(45, model.Style.Typography.FontSizePx);
        var snapshot = ProjectSnapshot.FromProject(source);
        var frame = LyricSceneSampler.SampleFrame(snapshot, 1500000);
        var actual = Assert.Single(frame.Lines, item => item.Frame.Line!.Id == "line/1");
        Assert.Equal(actual.ParentId, model.Context.NodeId); Assert.Equal(actual.ElementId, model.Context.TextNodeId);
        Assert.Equal(PresetSceneStyleProjection.NodePath(snapshot, actual.ElementId, frame), model.Context.NodePath);
        Assert.Equal(PresetSceneStyleProjection.Lyrics(snapshot, "line/1", "token/1", frame).Style, expected.Style);
        // 过渡期（P3 第 2～6 步）来源仍按旧合同的 26 项输出，每项取主属性的来源。
        Assert.Equal(Enum.GetValues<TemplateProperty>().Length, model.Sources.Length);
        Assert.All(model.Sources, entry => Assert.Equal(expected.Sources[LegacyStylePropertyMap.Primary(Enum.Parse<TemplateProperty>(entry.Property, true))], entry.SourcePath));
        Assert.EndsWith(source.PresetApplications.Items[^1].Id + ".snapshot.style", model.Sources.Single(item => item.Property == "opacity").SourcePath);
        var old = (await f.Registry.ReadStyleAsync("client", f.Id, "0", new TokenStyleTargetDto("primary", "line/1", "token/1"))).Response!;
        Assert.Equal("staticHierarchy", old.Resolution); Assert.Equal(source.LyricStyle.Opacity, old.StaticHierarchy.Opacity);
        Assert.Same(source, f.Workspace.Project); Assert.Equal(0, f.Workspace.History.UndoCount);
        Assert.Equal(BackendErrorCode.InvalidRequest, (await f.Registry.ReadResolvedStyleAsync("client", f.Id, "0", "primary", "line/1", nodeId: container)).Error!.Code);
        Assert.Equal(BackendErrorCode.InvalidRequest, (await f.Registry.ReadResolvedStyleAsync("client", f.Id, "0", "primary", "line/1", clipId: "wrong")).Error!.Code);
        Assert.Equal(BackendErrorCode.ResourceNotFound, (await f.Registry.ReadResolvedStyleAsync("client", f.Id, "0", "primary", "line/1", tokenId: "token/0")).Error!.Code);
        Assert.False((await f.Registry.ReadResolvedStyleAsync("other", f.Id, "0", "primary", "line/1")).Accepted);
    }

    [Fact]
    public async Task 翻译跟随仅继承主行时钟且样式路径匹配真实Runtime自身容器()
    {
        await using Fixture f = await Fixture.Create();
        var source = f.Workspace.Project;
        var translation = LyricBranch.Create("translation", "译", LyricBranchRole.Translation,
            [LyricLine.Create("translated", "译文") with { PrimaryLineId = "line/1" }], timingMode: LyricBranchTimingMode.FollowPrimary);
        source = source with { Lyrics = source.Lyrics with { Branches = source.Lyrics.Branches.Add(translation) } };
        source = source with { LyricPresentation = source.LyricPresentation.WithDefaultPrograms(source.Lyrics) };
        Assert.True(BasicLayoutOperation.TryPreview(source, "primary", BasicLayoutPreset.KaraokeDouble, null,
            new("双行", ["一", "二"], "选", "留"), out var layout, out var layoutError), layoutError?.ToString());
        source = layout.Result;
        string primaryContainer = source.LyricPresentation.FindProgram("primary")!.DefaultContainerId;
        string ownContainer = source.LyricPresentation.FindProgram("translation")!.DefaultContainerId;
        foreach (var (target, value, descendants) in new[] { (primaryContainer, .2f, true), (ownContainer, .3f, true) })
        {
            var template = StyleTemplate.Create("node/" + value, "节点", StyleTemplateKind.Composite, source.LyricStyle with { Opacity = value }, [LyricStyleProperty.Opacity]);
            Assert.True(PresetApplicationsEditor.TryApply(source, template, [new(ProjectEffectBindingKind.Node, id: target, includeDescendants: descendants)], null, out source, out string reason), reason);
        }
        f.Workspace.Replace(source);
        var response = (await f.Registry.ReadResolvedStyleAsync("client", f.Id, "0", "translation", "translated")).Response!;
        Assert.Equal(ownContainer, response.Context.ContainerId); Assert.Equal(ownContainer, response.Context.NodeId);
        Assert.Equal(.3f, response.Style.Opacity); Assert.DoesNotContain(primaryContainer, response.Context.NodePath);
        var snapshot = ProjectSnapshot.FromProject(source); var frame = LyricSceneSampler.SampleFrame(snapshot, 1500000);
        var sample = Assert.Single(frame.Lines, item => item.Frame.Line!.Id == "translated");
        Assert.Equal(sample.ParentId, response.Context.NodeId);
        Assert.Equal(PresetSceneStyleProjection.NodePath(snapshot, sample.ElementId, frame), response.Context.NodePath);
        Assert.Equal(PresetSceneStyleProjection.Lyrics(snapshot, "translated", frame: frame).Style.Opacity, response.Style.Opacity);
        // A non-descendant node application applies to the group itself, not the lyric text child.
        var template2 = StyleTemplate.Create("node/direct", "仅容器", StyleTemplateKind.Composite, source.LyricStyle with { Opacity = .1f }, [LyricStyleProperty.Opacity]);
        Assert.True(PresetApplicationsEditor.TryApply(source, template2, [new(ProjectEffectBindingKind.Node, id: ownContainer)], null, out source, out string reason2), reason2);
        f.Workspace.Replace(source);
        response = (await f.Registry.ReadResolvedStyleAsync("client", f.Id, "0", "translation", "translated")).Response!;
        Assert.Equal(source.LyricStyle.Opacity, response.Style.Opacity);
        snapshot = ProjectSnapshot.FromProject(source); frame = LyricSceneSampler.SampleFrame(snapshot, 1500000);
        Assert.Equal(PresetSceneStyleProjection.Lyrics(snapshot, "translated", frame: frame).Style.Opacity, response.Style.Opacity);
    }

    [Fact]
    public async Task 预提交完整读投影与回执预算拒绝且读修订严格()
    {
        await using Fixture f = await Fixture.Create();
        Ok(await f.Command("0", new CreateOrchestrationClipCommandDto("primary", "clip/a", null, ["line/0"], f.Container)));
        Project source = f.Workspace.Project;
        typeof(ProjectSession).GetField("_branchReceiptMaxBytes", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(f.Session, 100);
        var result = await f.Command("1", new SetOrchestrationClipMembersCommandDto("primary", "clip/a", ["line/0", "line/1"]));
        Assert.Equal(BackendErrorCode.ResourceTooLarge, result.Error!.Code); Assert.Same(source, f.Workspace.Project); Assert.Equal(1, f.Workspace.History.UndoCount);
        Assert.Equal(BackendErrorCode.RevisionConflict, (await f.Registry.ReadResolvedStyleAsync("client", f.Id, "0", "primary", "line/0")).Error!.Code);
        Assert.Equal(BackendErrorCode.InvalidRequest, (await f.Registry.ReadResolvedStyleAsync("client", f.Id, "x", "primary", "line/0")).Error!.Code);
        f.Workspace.Replace(source with { LyricPresentation = source.LyricPresentation with { Nodes = source.LyricPresentation.Nodes.SetItem(0,
            source.LyricPresentation.Nodes[0] with { Name = new string('x', ProjectReadModelLimits.MaxResponseBytes + 1) }) } });
        Assert.Equal(BackendErrorCode.ResourceTooLarge, (await f.Registry.ReadResolvedStyleAsync("client", f.Id, "1", "primary", "line/0")).Error!.Code);
    }

    [Theory]
    [InlineData("{\"type\":\"orchestration.setClipMembers\",\"branchId\":\"primary\",\"clipId\":\"clip\"}")]
    [InlineData("{\"type\":\"orchestration.setClipMembers\",\"branchId\":\"primary\",\"clipId\":\"clip\",\"lineIds\":[\"a\",\"a\"]}")]
    [InlineData("{\"type\":\"orchestration.splitClip\",\"branchId\":\"primary\",\"clipId\":\"clip\",\"splitBeforeLineId\":\"b\",\"newClipId\":\"new\",\"target\":null}")]
    [InlineData("{\"type\":\"orchestration.mergeClips\",\"branchId\":\"primary\",\"clipIds\":[\"a\",\"b\"],\"targetClipId\":\"a\",\"branchId\":\"other\"}")]
    public void 成员命令严格JSON拒绝缺失重复未知字段(string raw) => Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ProjectCommandDto>(raw, Json));

    private static ProjectCommandResponse Ok(ProjectCommandExecutionResult result)
    { Assert.True(result.Accepted, result.Error?.ToString()); return result.Response!; }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly TemporaryDirectory _directory = new();
        internal string Directory => _directory.Path;
        internal ProjectSessionRegistry Registry = null!;
        internal ProjectSession Session = null!;
        internal string Id => Session.ProjectSessionId;
        internal ProjectWorkspace Workspace => (ProjectWorkspace)typeof(ProjectSession).GetProperty("Workspace", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(Session)!;
        internal string Container => Workspace.Project.LyricPresentation.WithDefaultPrograms(Workspace.Project.Lyrics).FindProgram("primary")!.DefaultContainerId;
        internal ProjectCommandRequest Request(string revision, ProjectCommandDto command) => new(revision, command, Guid.NewGuid().ToString("N"), Session.HostInstanceId, DateTimeOffset.UtcNow.AddMinutes(1));
        internal Task<ProjectCommandExecutionResult> Command(string revision, ProjectCommandDto command) => Registry.ExecuteCommandAsync("client", Id, Request(revision, command));
        internal static async Task<Fixture> Create()
        {
            Fixture f = new(); ResourceStore store = new(new() { ResourceRoot = f.Directory });
            var audio = await store.UploadAsync("client", "song.wav", "audio/wav", new MemoryStream([1]));
            var lyrics = await store.UploadAsync("client", "song.lrc", "text/plain", new MemoryStream("[00:00.00]甲\n"u8.ToArray()));
            WaveformPeakBuilder peaks = new(); peaks.Append(new float[24000]);
            LoadedWaveform waveform = new(new AudioStreamInfo(3000000, 8000, 1, "pcm", "wav"), peaks.Build(8000));
            f.Registry = new("host", store, new((_, _) => Task.FromResult<LoadedWaveform?>(waveform)));
            f.Session = (await f.Registry.CreateAsync("client", new("styles-clips", audio.AssetId, lyrics.AssetId))).Session!;
            var lines = Enumerable.Range(0, 4).Select(index => LyricLine.Create("line/" + index, "字", new TimeRange(index * 1000000L, (index + 1) * 1000000L), TimingStatus.Imported,
                [LyricToken.Create("token/" + index, "字", new TimeRange(index * 1000000L, (index + 1) * 1000000L), TimingStatus.Imported)]));
            var document = LyricsDocument.Create(lines);
            var source = f.Workspace.Project with { Lyrics = document };
            f.Workspace.Replace(source with { LyricPresentation = source.LyricPresentation.WithDefaultPrograms(document) });
            return f;
        }
        public async ValueTask DisposeAsync() { await Registry.DisposeAsync(); _directory.Dispose(); }
    }
}
