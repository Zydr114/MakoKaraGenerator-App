using System.Reflection;
using System.Text.Json;
using MakoKaraGenerator.Application;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Media;
using MakoKaraGenerator.TestSupport;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

public sealed class BackendBranchCommandTests
{
    [Fact]
    public async Task 来源四命令复用业务并按一次历史撤销重做且S1可读()
    {
        await using Fixture f = await Fixture.Create(); Project original = f.Workspace.Project;
        var added = Accepted(await f.Command("0", new AddTranslationBranchCommandDto(" 新翻译 ", "ja-JP")));
        string id = added.BranchResult!.SelectedBranchId;
        var branch = f.Workspace.Project.Lyrics.Branches.Single(item => item.Id == id);
        Assert.Equal("新翻译", branch.Name); Assert.Equal("ja-JP", branch.Language);
        Assert.Equal(LyricBranchTimingMode.FollowPrimary, branch.TimingMode); Assert.Empty(branch.Lines);
        Assert.Equal("branch.add", added.UndoName); Assert.Equal("1", added.ProjectRevision);
        Assert.True(added.ChangeSet.RequiresFullRefresh); Assert.Null(added.TimingResult);
        var listed = await f.Registry.ReadLyricBranchesAsync("client", f.Id, "1");
        Assert.Contains(listed.Response!.Branches, item => item.Id == id);
        var configured = Accepted(await f.Command("1", new ConfigureBranchCommandDto(id, " 原名保空白 ", null,
            ProjectLyricBranchRole.Annotation, ProjectLyricTimingMode.None)));
        Assert.Null(configured.BranchResult); branch = f.Workspace.Project.Lyrics.Branches.Single(item => item.Id == id);
        Assert.Equal(" 原名保空白 ", branch.Name); Assert.Null(branch.Language); Assert.Equal("branch.configure", configured.UndoName);
        var linked = Accepted(await f.Command("2", new LinkPrimaryLineCommandDto("translation", "translated", "main")));
        Assert.Null(linked.BranchResult); Assert.Equal("main", f.Translation.Lines[0].PrimaryLineId);
        Assert.Same(original.Lyrics.Branches[1].StyleOverride, f.Translation.StyleOverride);
        Assert.Same(original.Lyrics.Branches[1].Lines[0].RubyAnnotations[0], f.Translation.Lines[0].RubyAnnotations[0]);
        var removed = Accepted(await f.Command("3", new RemoveBranchCommandDto(id)));
        Assert.Equal("primary", removed.BranchResult!.SelectedBranchId); Assert.Equal("branch.remove", removed.UndoName);
        Assert.Equal(4, f.Workspace.History.UndoCount);
        for (int revision = 4; revision < 8; revision++) Accepted(await f.Command(revision.ToString(System.Globalization.CultureInfo.InvariantCulture), new("history.undo")));
        Assert.Same(original, f.Workspace.Project);
        for (int revision = 8; revision < 12; revision++) Accepted(await f.Command(revision.ToString(System.Globalization.CultureInfo.InvariantCulture), new("history.redo")));
        Assert.Equal("main", f.Translation.Lines[0].PrimaryLineId); Assert.DoesNotContain(f.Workspace.Project.Lyrics.Branches, item => item.Id == id);
    }

    [Fact]
    public async Task 相同配置和关联保redo游标及可选反馈省略()
    {
        await using Fixture f = await Fixture.Create();
        Accepted(await f.Command("0", new LinkPrimaryLineCommandDto("translation", "translated", "main")));
        Accepted(await f.Command("1", new("history.undo")));
        var before = await f.Registry.ReadHistoryAsync("client", f.Id);
        var noop = Accepted(await f.Command("2", new LinkPrimaryLineCommandDto("translation", "translated", null)));
        Assert.False(noop.Changed); Assert.Null(noop.BranchResult); Assert.True(noop.CanRedo);
        var configure = Accepted(await f.Command("2", new ConfigureBranchCommandDto("translation", "翻译", "zh-CN",
            ProjectLyricBranchRole.Translation, ProjectLyricTimingMode.FollowPrimary)));
        Assert.False(configure.Changed); Assert.Null(configure.BranchResult);
        Assert.Equal(before, await f.Registry.ReadHistoryAsync("client", f.Id)); Assert.Equal(noop.EventCursor, configure.EventCursor);
    }

    [Fact]
    public async Task 新增来源立即获得隐含编排程序并可设置默认容器()
    {
        await using Fixture f = await Fixture.Create();
        var added = Accepted(await f.Command("0", new AddTranslationBranchCommandDto("新增", "ja")));
        string branchId = added.BranchResult!.SelectedBranchId;
        var model = (await f.Registry.ReadOrchestrationAsync("client", f.Id, "1"))!.Response!;
        var program = Assert.Single(model.Programs, item => item.BranchId == branchId);
        Assert.Equal("branch/" + Uri.EscapeDataString(branchId) + "/display/regular", program.DefaultContainerId);
        var changed = Accepted(await f.Command("1", new SetOrchestrationDefaultContainerCommandDto(branchId, program.DefaultContainerId)));
        Assert.False(changed.Changed); Assert.Equal("1", changed.ProjectRevision);

        const string primaryRegular = "branch/primary/display/regular";
        var rejected = await f.Command("1", new SetOrchestrationDefaultContainerCommandDto(branchId, primaryRegular));
        Assert.False(rejected.Accepted); Assert.Equal(BackendErrorCode.CommandRejected, rejected.Error!.Code);
        Assert.Equal("PresentationInvalid", rejected.Error.FailureCode);
        var unchanged = (await f.Registry.ReadOrchestrationAsync("client", f.Id, "1"))!.Response!;
        Assert.Equal(program.DefaultContainerId, Assert.Single(unchanged.Programs, item => item.BranchId == branchId).DefaultContainerId);
    }

    [Fact]
    public async Task 编排默认容器实际变更写入一次历史并支持撤销重做()
    {
        await using Fixture f = await Fixture.Create();
        Project source = f.Workspace.Project;
        LyricDisplayProgram program = source.LyricPresentation.FindProgram("primary")!;
        string alternateId = "branch/primary/display/alternate";
        var alternate = new SceneContainerDefinition { Id = alternateId, ParentId = program.RootContainerId, Layout = new() };
        f.Replace(source with { LyricPresentation = source.LyricPresentation with { Nodes = source.LyricPresentation.Nodes.Add(alternate) } });
        var changed = Accepted(await f.Command("0", new SetOrchestrationDefaultContainerCommandDto("primary", alternateId)));
        Assert.True(changed.Changed); Assert.Equal("1", changed.ProjectRevision); Assert.True(changed.CanUndo);
        Assert.Equal(alternateId, f.Workspace.Project.LyricPresentation.FindProgram("primary")!.DefaultContainerId);
        var undone = Accepted(await f.Command("1", new("history.undo")));
        Assert.Equal("2", undone.ProjectRevision); Assert.Equal(program.DefaultContainerId, f.Workspace.Project.LyricPresentation.FindProgram("primary")!.DefaultContainerId);
        var redone = Accepted(await f.Command("2", new("history.redo")));
        Assert.Equal("3", redone.ProjectRevision); Assert.Equal(alternateId, f.Workspace.Project.LyricPresentation.FindProgram("primary")!.DefaultContainerId);
    }

    [Theory]
    [InlineData(0, "PrimaryBranchProtected", "command.branchId")]
    [InlineData(1, "PrimaryBranchProtected", "command.role")]
    [InlineData(2, "InvalidPrimaryLine", "command.primaryLineId")]
    [InlineData(3, "TargetStale", "command.lineId")]
    [InlineData(4, "InvalidConfiguration", "command.name")]
    public async Task 领域拒绝有真实字段和稳定原因且不改工程(int kind, string failure, string field)
    {
        await using Fixture f = await Fixture.Create(); Project source = f.Workspace.Project;
        ProjectCommandDto command = kind switch
        {
            0 => new RemoveBranchCommandDto("primary"),
            1 => new ConfigureBranchCommandDto("translation", "翻译", null, ProjectLyricBranchRole.Primary, ProjectLyricTimingMode.FollowPrimary),
            2 => new LinkPrimaryLineCommandDto("translation", "translated", "translated"),
            3 => new LinkPrimaryLineCommandDto("translation", "missing", null),
            _ => new AddTranslationBranchCommandDto(" "),
        };
        var rejected = await f.Command("0", command);
        Assert.False(rejected.Accepted); Assert.Equal(BackendErrorCode.CommandRejected, rejected.Error!.Code);
        Assert.Equal(failure, rejected.Error.FailureCode); Assert.Equal(field, rejected.Error.Field);
        Assert.Same(source, f.Workspace.Project); Assert.Equal(0, f.Workspace.History.UndoCount);
    }

    [Fact]
    public async Task 新增及配置的完整来源单项响应超预算拒绝于历史之前()
    {
        await using Fixture f = await Fixture.Create(); Project source = f.Workspace.Project;
        string huge = new('汉', 180000);
        foreach (ProjectCommandDto command in new ProjectCommandDto[]
        {
            new AddTranslationBranchCommandDto(huge),
            new ConfigureBranchCommandDto("translation", huge, null, ProjectLyricBranchRole.Translation, ProjectLyricTimingMode.FollowPrimary),
            new ConfigureBranchCommandDto("translation", "翻译", huge, ProjectLyricBranchRole.Translation, ProjectLyricTimingMode.FollowPrimary),
        })
        {
            Assert.Equal(BackendErrorCode.ResourceTooLarge, (await f.Command("0", command)).Error!.Code);
            Assert.Same(source, f.Workspace.Project); Assert.Equal(0, f.Workspace.History.UndoCount);
        }
    }

    [Fact]
    public async Task 删除可修复原超大来源但巨大主来源选择反馈受回执预算保护()
    {
        await using Fixture f = await Fixture.Create();
        f.Replace(f.Workspace.Project with { Lyrics = f.Workspace.Project.Lyrics.ReplaceBranch(f.Translation with { Name = new string('汉', 180000) }) });
        Assert.True((await f.Command("0", new RemoveBranchCommandDto("translation"))).Accepted);
        Assert.Single(f.Workspace.Project.Lyrics.Branches);
        Accepted(await f.Command("1", new("history.undo")));
        string hugeId = new('x', 65536);
        var lyrics = f.Workspace.Project.Lyrics;
        f.Replace(f.Workspace.Project with { LyricPresentation = LyricPresentationDefinition.Empty,
            Lyrics = lyrics with { PrimaryBranchId = hugeId, Branches = lyrics.Branches.SetItem(0, lyrics.Branches[0] with { Id = hugeId }) } });
        Project before = f.Workspace.Project;
        var rejected = await f.Command("2", new RemoveBranchCommandDto("translation"));
        Assert.Equal(BackendErrorCode.ResourceTooLarge, rejected.Error!.Code); Assert.Same(before, f.Workspace.Project);
    }

    [Fact]
    public async Task 关联造成完整单行读模型越限时先拒绝并保留重做()
    {
        await using Fixture f = await Fixture.Create();
        Accepted(await f.Command("0", new LinkPrimaryLineCommandDto("translation", "translated", "main")));
        Accepted(await f.Command("1", new("history.undo")));
        string primaryLineId = new('汉', 180000);
        var source = f.Workspace.Project;
        f.Replace(source with { Lyrics = source.Lyrics.ReplaceBranch(source.Lyrics.Branches[0] with
        {
            Lines = [source.Lyrics.Lines[0] with { Id = primaryLineId }],
        }) });
        Project before = f.Workspace.Project;
        var rejected = await f.Command("2", new LinkPrimaryLineCommandDto("translation", "translated", primaryLineId));
        Assert.Equal(BackendErrorCode.ResourceTooLarge, rejected.Error!.Code);
        Assert.Same(before, f.Workspace.Project); Assert.Equal(1, f.Workspace.History.RedoCount); Assert.Equal(0, f.Workspace.History.UndoCount);
        Assert.Null(f.Translation.Lines[0].PrimaryLineId);
    }

    [Fact]
    public async Task 同ID重试回原新增选择且并发精确修订只接受一个新来源()
    {
        await using Fixture f = await Fixture.Create();
        ProjectCommandRequest request = f.Request("0", new AddTranslationBranchCommandDto("新增"));
        var first = Accepted(await f.Registry.ExecuteCommandAsync("client", f.Id, request));
        var retry = Accepted(await f.Registry.ExecuteCommandAsync("client", f.Id, request));
        Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(retry)); Assert.Equal(3, f.Workspace.Project.Lyrics.Branches.Length);
        Task<ProjectCommandExecutionResult>[] competing = [f.Command("1", new AddTranslationBranchCommandDto("a")), f.Command("1", new AddTranslationBranchCommandDto("b"))];
        var results = await Task.WhenAll(competing);
        Assert.Single(results, item => item.Accepted); Assert.Single(results, item => item.Error?.Code == BackendErrorCode.RevisionConflict);
        Assert.Equal(4, f.Workspace.Project.Lyrics.Branches.Length); Assert.Equal(2, f.Workspace.History.UndoCount);
    }

    private static ProjectCommandResponse Accepted(ProjectCommandExecutionResult result)
    { Assert.True(result.Accepted, result.Error?.Message); return result.Response!; }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly TemporaryDirectory _directory = new();
        internal ProjectSessionRegistry Registry = null!; internal ProjectSession Session = null!;
        internal string Id => Session.ProjectSessionId;
        internal ProjectWorkspace Workspace => Session.Workspace;
        internal LyricBranch Translation => Workspace.Project.Lyrics.Branches.Single(branch => branch.Id == "translation");
        internal void Replace(Project project) => typeof(ProjectWorkspace).GetProperty("Project")!.SetValue(Workspace, project);
        internal ProjectCommandRequest Request(string revision, ProjectCommandDto command) => new(revision, command, Guid.NewGuid().ToString("N"), Session.HostInstanceId, DateTimeOffset.UtcNow.AddMinutes(1));
        internal Task<ProjectCommandExecutionResult> Command(string revision, ProjectCommandDto command) => Registry.ExecuteCommandAsync("client", Id, Request(revision, command));
        internal static async Task<Fixture> Create()
        {
            Fixture f = new(); ResourceStore resources = new(new() { ResourceRoot = f._directory.Path });
            var audio = await resources.UploadAsync("client", "song.wav", "audio/wav", new MemoryStream([1]));
            var lyrics = await resources.UploadAsync("client", "song.lrc", "text/plain", new MemoryStream("[00:00.00]主\n"u8.ToArray()));
            WaveformPeakBuilder peaks = new(); peaks.Append(new float[24000]);
            LoadedWaveform waveform = new(new AudioStreamInfo(3000000, 8000, 1, "pcm", "wav"), peaks.Build(8000));
            f.Registry = new("host", resources, new((_, _) => Task.FromResult<LoadedWaveform?>(waveform)));
            f.Session = (await f.Registry.CreateAsync("client", new("branches", audio.AssetId, lyrics.AssetId))).Session!;
            var source = f.Workspace.Project;
            f.Replace(source with { Lyrics = source.Lyrics with { Branches = [source.Lyrics.Branches[0] with
            {
                Lines = [LyricLine.Create("main", "主")],
            }, LyricBranch.Create("translation", "翻译", LyricBranchRole.Translation,
                [LyricLine.Create("translated", "译", tokens: [LyricToken.Create("translated-token", "译")]) with
                { RubyAnnotations = [RubyAnnotation.Create("ruby", "translated-token", "yi")] }],
                "zh-CN", LyricBranchTimingMode.FollowPrimary, new LyricStyleOverride { FontSizePx = 25 })] } });
            return f;
        }
        public async ValueTask DisposeAsync() { await Registry.DisposeAsync(); _directory.Dispose(); }
    }
}
