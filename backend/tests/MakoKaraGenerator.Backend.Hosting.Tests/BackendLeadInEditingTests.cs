using System.Reflection;
using System.Globalization;
using System.Text.Json;
using MakoKaraGenerator.Application;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Media;
using MakoKaraGenerator.TestSupport;
namespace MakoKaraGenerator.Backend.Hosting.Tests;
public sealed class BackendLeadInEditingTests
{
    private static LeadInGenerationSettingsDto Settings(string indicator) => new(true, true, "8000000", "3000000", indicator, []);
    [Fact]
    public async Task 生成完整预览与Core逐字段一致且不动历史_选中应用与重复无变化()
    {
        await using Fixture f = await Fixture.Create();
        var created = Accepted(await f.Command("0", new CreateOrchestrationCountdownCommandDto(f.Root, null, 3)));
        string indicator = created.LeadInResult!.IndicatorContainerId!;
        Project source = f.Workspace.Project;
        var program = source.LyricPresentation.FindProgram("primary")!;
        var settings = Settings(indicator);
        var preview = Accepted(await f.Command("1", new PreviewLeadInGenerationCommandDto("primary", settings)));
        Assert.False(preview.Changed); Assert.Same(source, f.Workspace.Project); Assert.Equal(1, f.Workspace.History.UndoCount);
        var expected = LeadInGeneration.Analyze(source, program.Id, new() { IndicatorContainerId = indicator });
        var actual = preview.LeadInGenerationResult!;
        Assert.Equal(expected.Candidates.Length, actual.Candidates.Length);
        for (int i = 0; i < expected.Candidates.Length; i++)
        {
            var e = expected.Candidates[i]; var a = actual.Candidates[i];
            Assert.Equal(e.TargetLineId, a.TargetLineId); Assert.Equal(e.Status.ToString(), a.Status); Assert.Equal(e.Reason.ToString(), a.Reason);
            Assert.Equal(e.Proposed?.Id, a.Proposed?.Id); Assert.Equal(e.Range?.StartUs.ToString(CultureInfo.InvariantCulture), a.StartUs);
            Assert.Equal(e.Anchor?.TimeUs.ToString(CultureInfo.InvariantCulture), a.Anchor?.TimeUs);
        }
        string selected = actual.Candidates[0].TargetLineId;
        var apply = new ApplyLeadInGenerationCommandDto("primary", settings, [selected]);
        var applied = Accepted(await f.Command("1", apply));
        Assert.True(applied.Changed); Assert.Equal(2, f.Workspace.History.UndoCount);
        Assert.Single(f.Workspace.Project.LyricPresentation.FindProgram("primary")!.LeadIns);
        Assert.False(Accepted(await f.Command("2", apply)).Changed); Assert.Equal(2, f.Workspace.History.UndoCount);
        var read = (await f.Registry.ReadOrchestrationAsync("client", f.Id, "2")).Response!;
        Assert.Equal("3000000", read.Programs.Single().LeadInGeneration!.DurationUs);
    }
    [Fact]
    public async Task 生成设置清除保留前奏_删除并排除后预览报告Excluded()
    {
        await using Fixture f = await Fixture.Create();
        var created = Accepted(await f.Command("0", new CreateOrchestrationCountdownCommandDto(f.Root, null, 3)));
        string indicator = created.LeadInResult!.IndicatorContainerId!; var settings = Settings(indicator);
        string line = f.Workspace.Project.Lyrics.PrimaryBranch!.Lines[0].Id;
        Accepted(await f.Command("1", new ApplyLeadInGenerationCommandDto("primary", settings, [line])));
        string cue = f.Workspace.Project.LyricPresentation.FindProgram("primary")!.LeadIns[0].Id;
        Accepted(await f.Command("2", new SetLeadInGenerationCommandDto("primary", null)));
        Assert.Single(f.Workspace.Project.LyricPresentation.FindProgram("primary")!.LeadIns);
        var deleted = Accepted(await f.Command("3", new DeleteLeadInAndExcludeCommandDto("primary", cue)));
        Assert.True(deleted.Changed); var program = f.Workspace.Project.LyricPresentation.FindProgram("primary")!;
        Assert.Empty(program.LeadIns); Assert.Contains(line, program.LeadInGeneration!.ExcludedLineIds);
        var preview = Accepted(await f.Command("4", new PreviewLeadInGenerationCommandDto("primary", settings with { ExcludedLineIds = [line] })));
        Assert.Equal("Excluded", preview.LeadInGenerationResult!.Candidates[0].Reason);
    }
    [Fact]
    public async Task 预览失败诊断完整只读_完整报告预算失败不动工程()
    {
        await using Fixture f = await Fixture.Create(receiptBytes: 1024);
        // Three-mark creation fits the small receipt; a full two-line generation report does not.
        var created = Accepted(await f.Command("0", new CreateOrchestrationCountdownCommandDto(f.Root, null, 3)));
        var before = f.Workspace.Project;
        var preview = await f.Command("1", new PreviewLeadInGenerationCommandDto("primary", Settings(created.LeadInResult!.IndicatorContainerId!)));
        Assert.Equal(BackendErrorCode.ResourceTooLarge, preview.Error!.Code); Assert.Same(before, f.Workspace.Project);
        Assert.Equal(1, f.Workspace.History.UndoCount);
        await using Fixture g = await Fixture.Create();
        var invalid = Accepted(await g.Command("0", new PreviewLeadInGenerationCommandDto("primary", Settings("missing"))));
        Assert.False(invalid.Changed); Assert.NotEmpty(invalid.LeadInGenerationResult!.Diagnostics); Assert.Empty(invalid.LeadInGenerationResult.Candidates);
    }
    [Fact]
    public async Task 标记追加不重排旧位置_重排不改几何_等距显式且重复无变化()
    {
        await using Fixture f = await Fixture.Create();
        var created = Accepted(await f.Command("0", new CreateOrchestrationCountdownCommandDto(f.Root, null, 3)));
        string group = created.LeadInResult!.IndicatorContainerId!; string[] old = created.LeadInResult.MarkNodeIds!;
        var placements = old.Select(id => f.Workspace.Project.LyricPresentation.FindNode(id)!.Placement).ToArray();
        var added = Accepted(await f.Command("1", new AddOrchestrationMarkCommandDto(group, "四")));
        string mark = Assert.Single(added.LeadInResult!.MarkNodeIds!);
        for (int i = 0; i < old.Length; i++) Assert.Equal(placements[i], f.Workspace.Project.LyricPresentation.FindNode(old[i])!.Placement);
        string[] order = [mark, .. old.Reverse()];
        Accepted(await f.Command("2", new ReorderOrchestrationMarksCommandDto(group, order)));
        Assert.Equal(order, ((CountdownSequenceContent)f.Workspace.Project.LyricPresentation.FindNode(group)!.Content!).MarkNodeIds);
        for (int i = 0; i < old.Length; i++) Assert.Equal(placements[i], f.Workspace.Project.LyricPresentation.FindNode(old[i])!.Placement);
        Assert.False(Accepted(await f.Command("3", new ReorderOrchestrationMarksCommandDto(group, order))).Changed);
        Accepted(await f.Command("3", new SpaceOrchestrationMarksCommandDto(group)));
        for (int i = 0; i < order.Length; i++) Assert.Equal((i + .5) / order.Length, f.Workspace.Project.LyricPresentation.FindNode(order[i])!.Placement.Position.X);
        Assert.False(Accepted(await f.Command("4", new SpaceOrchestrationMarksCommandDto(group))).Changed);
        Accepted(await f.Command("4", new SetOrchestrationMarkTextCommandDto(mark, "终")));
        Assert.False(Accepted(await f.Command("5", new SetOrchestrationMarkTextCommandDto(mark, "终"))).Changed);
        Assert.Equal("终", ((TextMarkContent)f.Workspace.Project.LyricPresentation.FindNode(mark)!.Content!).Text);
    }
    [Fact]
    public async Task 标记覆盖区分继承与显式关闭_基线完整读写_禁止布局属性()
    {
        await using Fixture f = await Fixture.Create();
        var created = Accepted(await f.Command("0", new CreateOrchestrationCountdownCommandDto(f.Root, null, 3)));
        string group = created.LeadInResult!.IndicatorContainerId!; string mark = created.LeadInResult.MarkNodeIds![0];
        var model = (await f.Registry.ReadOrchestrationAsync("client", f.Id, "1")).Response!;
        var baseline = model.Nodes.Single(n => n.Id == group).Content!.Appearance!;
        var stroke = new StyleStrokeDto(new SolidStyleMaterialDto(new(1,2,3,255)), 2, StyleStrokeJoin.Round);
        var glow = new StyleGlowDto(new(255, 255, 255, 255), .7f, 8, 2);
        Accepted(await f.Command("1", new SetCountdownAppearanceCommandDto(group, baseline with { Pending = baseline.Pending with { Strokes = [stroke], Glow = glow }, Opacity = .8f })));
        var set = new SetMarkAppearanceCommandDto(mark, [new StyleUnsungStrokesChangeDto([]), new StyleFontSizeChangeDto(48), new StyleUnsungGlowChangeDto(null)]);
        Accepted(await f.Command("2", set));
        Assert.False(Accepted(await f.Command("3", set)).Changed);
        var updated = (await f.Registry.ReadOrchestrationAsync("client", f.Id, "3")).Response!;
        Assert.Equal(glow, updated.Nodes.Single(n => n.Id == group).Content!.Appearance!.Pending.Glow);
        Assert.Contains(updated.Nodes.Single(n => n.Id == mark).Content!.AppearanceOverride!,
            change => change is StyleUnsungGlowChangeDto { Value: null });
        var sequence = (CountdownSequenceContent)f.Workspace.Project.LyricPresentation.FindNode(group)!.Content!;
        var text = (TextMarkContent)f.Workspace.Project.LyricPresentation.FindNode(mark)!.Content!;
        Assert.NotNull(text.AppearanceOverride!.Pending!.Strokes); Assert.Null(sequence.Appearance.Resolve(text.AppearanceOverride).Stroke);
        Assert.True(text.AppearanceOverride.Pending.Glow.IsSet); Assert.Null(sequence.Appearance.Resolve(text.AppearanceOverride).Glow);
        Assert.Equal(2, sequence.Appearance.Glow!.SpreadPx);
        Assert.Equal(48, sequence.Appearance.Resolve(text.AppearanceOverride).Typography.FontSizePx);
        Assert.Equal(BackendErrorCode.InvalidRequest, (await f.Command("3", new SetMarkAppearanceCommandDto(mark, [new StyleScaleChangeDto(2)]))).Error!.Code);
        Accepted(await f.Command("3", new SetMarkAppearanceCommandDto(mark, [])));
        Assert.Null(((TextMarkContent)f.Workspace.Project.LyricPresentation.FindNode(mark)!.Content!).AppearanceOverride);
        Assert.NotNull(sequence.Appearance.Resolve(null).Stroke);
        var cleared = (await f.Registry.ReadOrchestrationAsync("client", f.Id, "4")).Response!;
        Assert.DoesNotContain(cleared.Nodes.Single(n => n.Id == mark).Content!.AppearanceOverride!, change => change.Property == "unsung.glow");
    }
    [Fact]
    public async Task 分析回执数组隔离与规范时间重试_并发及陈旧目标拒绝()
    {
        await using Fixture f = await Fixture.Create();
        var created = Accepted(await f.Command("0", new CreateOrchestrationCountdownCommandDto(f.Root, null, 3)));
        var settings = Settings(created.LeadInResult!.IndicatorContainerId!);
        var request = Fixture.Request("1", new PreviewLeadInGenerationCommandDto("primary", settings));
        var first = Accepted(await f.Registry.ExecuteCommandAsync("client", f.Id, request));
        string target = first.LeadInGenerationResult!.Candidates[0].TargetLineId;
        first.LeadInGenerationResult.Candidates[0] = first.LeadInGenerationResult.Candidates[0] with { TargetLineId = "polluted" };
        var replay = Accepted(await f.Registry.ExecuteCommandAsync("client", f.Id, request with { Command = new PreviewLeadInGenerationCommandDto("primary", settings with { DurationUs = "+03000000" }) }));
        Assert.Equal(target, replay.LeadInGenerationResult!.Candidates[0].TargetLineId);
        Assert.Equal("TargetStale", (await f.Command("1", new ApplyLeadInGenerationCommandDto("primary", settings, ["missing"]))).Error!.FailureCode);
        var writes = await Task.WhenAll(f.Command("1", new SetLeadInGenerationCommandDto("primary", settings)), f.Command("1", new SetLeadInGenerationCommandDto("primary", settings)));
        Assert.Single(writes, w => w.Accepted); Assert.Equal(BackendErrorCode.RevisionConflict, Assert.Single(writes, w => !w.Accepted).Error!.Code);
        Assert.Equal(BackendErrorCode.SessionNotFound, (await f.Registry.ExecuteCommandAsync("other", f.Id, Fixture.Request("2", new PreviewLeadInGenerationCommandDto("primary", settings)))).Error!.Code);
    }
    [Fact]
    public async Task 非空排除设置重复应用及完整外观等距无变化均保留重做()
    {
        await using Fixture f = await Fixture.Create();
        var created = Accepted(await f.Command("0", new CreateOrchestrationCountdownCommandDto(f.Root, null, 3)));
        string group = created.LeadInResult!.IndicatorContainerId!; string mark = created.LeadInResult.MarkNodeIds![0];
        var settings = Settings(group) with { ExcludedLineIds = [f.Workspace.Project.Lyrics.PrimaryBranch!.Lines[1].Id] };
        Accepted(await f.Command("1", new SetLeadInGenerationCommandDto("primary", settings)));
        Accepted(await f.Command("2", new SetOrchestrationMarkTextCommandDto(mark, "改")));
        Accepted(await f.Command("3", new ProjectCommandDto("history.undo")));
        Project source = f.Workspace.Project;
        var model = (await f.Registry.ReadOrchestrationAsync("client", f.Id, "4")).Response!;
        ProjectCommandDto[] noops = [new SetLeadInGenerationCommandDto("primary", settings),
            new ApplyLeadInGenerationCommandDto("primary", settings, []), new SpaceOrchestrationMarksCommandDto(group),
            new ReorderOrchestrationMarksCommandDto(group, created.LeadInResult.MarkNodeIds!.ToArray()),
            new SetCountdownAppearanceCommandDto(group, model.Nodes.Single(n => n.Id == group).Content!.Appearance)];
        foreach (var command in noops)
        {
            var response = Accepted(await f.Command("4", command));
            Assert.False(response.Changed); Assert.True(response.CanRedo); Assert.Same(source, f.Workspace.Project);
            Assert.Equal(2, f.Workspace.History.UndoCount); Assert.Equal(1, f.Workspace.History.RedoCount);
        }
        var preview = Accepted(await f.Command("4", new PreviewLeadInGenerationCommandDto("primary", settings)));
        Assert.Equal("Excluded", preview.LeadInGenerationResult!.Candidates[1].Reason); Assert.True(preview.CanRedo);
    }
    [Fact]
    public async Task 手工和禁用生成保护_只读会话可分析但拒绝写入()
    {
        await using Fixture f = await Fixture.Create();
        var created = Accepted(await f.Command("0", new CreateOrchestrationCountdownCommandDto(f.Root, null, 3)));
        string group = created.LeadInResult!.IndicatorContainerId!, line = f.Workspace.Project.Lyrics.PrimaryBranch!.Lines[0].Id;
        Accepted(await f.Command("1", new CreateOrchestrationLeadInCommandDto("primary", "cue/manual", null, line, group, "3000000", true)));
        var settings = Settings(group);
        var preview = Accepted(await f.Command("2", new PreviewLeadInGenerationCommandDto("primary", settings)));
        Assert.Equal("Preserved", preview.LeadInGenerationResult!.Candidates[0].Status);
        Assert.Equal("ExistingManual", preview.LeadInGenerationResult.Candidates[0].Reason);
        Accepted(await f.Command("2", new ApplyLeadInGenerationCommandDto("primary", settings, [line])));
        Assert.Equal(LeadInOrigin.Manual, f.Workspace.Project.LyricPresentation.FindProgram("primary")!.LeadIns[0].Origin);
        Accepted(await f.Command("3", new UpdateOrchestrationLeadInCommandDto("primary", "cue/manual", null, line, group, "3000000", false)));
        typeof(ProjectSession).GetProperty("IsReadOnly", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(f.Session, true);
        preview = Accepted(await f.Command("4", new PreviewLeadInGenerationCommandDto("primary", settings)));
        Assert.Equal("Disabled", preview.LeadInGenerationResult!.Candidates[0].Reason); Assert.False(preview.Changed);
        Assert.Equal(BackendErrorCode.ReadOnlySession, (await f.Command("4", new SetLeadInGenerationCommandDto("primary", null))).Error!.Code);
    }
    [Fact]
    public async Task 生成长确定性cue身份仍能使用旧更新删除命令_畸形输入失败无写()
    {
        await using Fixture f = await Fixture.Create();
        var source = f.Workspace.Project; var branch = source.Lyrics.PrimaryBranch!;
        string longLine = new('词', 100);
        branch = branch with { Lines = branch.Lines.SetItem(0, branch.Lines[0] with { Id = longLine }) };
        f.Workspace.Replace(source with { Lyrics = source.Lyrics with { Branches = source.Lyrics.Branches.SetItem(0, branch) } });
        var created = Accepted(await f.Command("0", new CreateOrchestrationCountdownCommandDto(f.Root, null, 3)));
        string group = created.LeadInResult!.IndicatorContainerId!;
        Accepted(await f.Command("1", new ApplyLeadInGenerationCommandDto("primary", Settings(group), [longLine])));
        string cue = f.Workspace.Project.LyricPresentation.FindProgram("primary")!.LeadIns[0].Id;
        Assert.True(cue.Length > 128);
        Accepted(await f.Command("2", new UpdateOrchestrationLeadInCommandDto("primary", cue, "改名", longLine, group, "3000000", true)));
        Accepted(await f.Command("3", new DeleteOrchestrationLeadInCommandDto("primary", cue)));
        Assert.Empty(f.Workspace.Project.LyricPresentation.FindProgram("primary")!.LeadIns);
        Project before = f.Workspace.Project;
        foreach (var bad in new ProjectCommandDto[] { new AddOrchestrationMarkCommandDto(group, " "),
            new SetMarkAppearanceCommandDto(created.LeadInResult.MarkNodeIds![0], [new StyleOpacityChangeDto(float.NaN)]),
            new SetLeadInGenerationCommandDto("primary", Settings(group) with { ExcludedLineIds = [longLine, longLine] }),
            new SetLeadInGenerationCommandDto("primary", Settings(group) with { DurationUs = "-1" }) })
        { Assert.False((await f.Command("4", bad)).Accepted); Assert.Same(before, f.Workspace.Project); }
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
            var lyrics = await resources.UploadAsync("client", "song.lrc", "text/plain", new MemoryStream("[00:05.00]甲\n[00:20.00]乙\n"u8.ToArray()));
            var audio = await resources.UploadAsync("client", "song.wav", "audio/wav", new MemoryStream([1, 2, 3]));
            WaveformPeakBuilder peaks = new(); peaks.Append(new float[240000]);
            LoadedWaveform waveform = new(new AudioStreamInfo(30000000, 8000, 1, "pcm", "wav"), peaks.Build(8000));
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
