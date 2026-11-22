using System.Text.Json;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Application;
using System.Reflection;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

public sealed partial class BackendPresetApplicationCommandTests
{
    [Theory]
    [InlineData("{\"kind\":\"node\"}")]
    [InlineData("{\"kind\":\"node\",\"nodeId\":\"mark\"}")]
    [InlineData("{\"kind\":\"node\",\"nodeId\":\"mark\",\"includeDescendants\":null}")]
    [InlineData("{\"kind\":\"node\",\"nodeId\":\"mark\",\"includeDescendants\":1}")]
    [InlineData("{\"kind\":\"node\",\"nodeId\":\"mark\",\"includeDescendants\":\"true\"}")]
    [InlineData("{\"kind\":\"node\",\"nodeId\":\" \",\"includeDescendants\":false}")]
    [InlineData("{\"kind\":\"node\",\"nodeId\":\"mark\",\"includeDescendants\":false,\"branchId\":\"primary\"}")]
    [InlineData("{\"kind\":\"node\",\"nodeId\":\"mark\",\"includeDescendants\":false,\"lineId\":\"line\"}")]
    [InlineData("{\"kind\":\"node\",\"nodeId\":\"mark\",\"includeDescendants\":false,\"tokenId\":\"token\"}")]
    [InlineData("{\"kind\":\"node\",\"nodeId\":\"mark\",\"nodeId\":\"other\",\"includeDescendants\":false}")]
    [InlineData("{\"kind\":\"project\",\"nodeId\":\"mark\"}")]
    [InlineData("{\"kind\":\"project\",\"includeDescendants\":false}")]
    [InlineData("{\"kind\":\"node\",\"nodeId\":\"mark\",\"includeDescendants\":false,\"extra\":false}")]
    public void Node目标在命令与候选试看共用严格union(string target)
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<PresetApplicationTargetDto>(target, Json));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ProjectCommandDto>(
            "{\"type\":\"presetApplications.apply\",\"templateLibraryId\":\"builtin\",\"templateId\":\"x\",\"templateRevision\":\"r\",\"targets\":[" + target + "]}", Json));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Node目标完整保留真实身份和后代策略(bool descendants)
    {
        PresetApplicationTargetDto target = new("node", NodeId: "真实/标记", IncludeDescendants: descendants);
        Assert.Equal(target, JsonSerializer.Deserialize<PresetApplicationTargetDto>(JsonSerializer.Serialize(target, Json), Json));
        Assert.Throws<JsonException>(() => JsonSerializer.Serialize(target with { NodeId = new string('x', 129) }, Json));
    }

    [Fact]
    public async Task Node和mark静态分配读模型更新撤销无变化保redo及过期目标拒绝原子()
    {
        await using Fixture f = await Fixture.CreateAsync();
        string root = f.SessionProject.LyricPresentation.FindProgram("primary")!.RootContainerId;
        ProjectCommandResponse countdown = AssertAccepted(await f.Command("0", new CreateOrchestrationCountdownCommandDto(root, null, 3)));
        string mark = countdown.LeadInResult!.MarkNodeIds![0];
        TemplateListResponse builtin = (await f.Registry.ReadTemplatesAsync("builtin", null)).Response!;
        TemplateSummaryDto template = builtin.Templates.First(item => item.Kind != TemplateKind.Token);
        ApplyPresetApplicationCommandDto apply = new("builtin", template.TemplateId, builtin.Revision,
            [new("node", NodeId: mark, IncludeDescendants: false)], null);
        ProjectCommandResponse applied = AssertAccepted(await f.Command("1", apply));
        var assignment = Assert.Single(f.SessionProject.PresetApplications.Items);
        Assert.Equal(ProjectEffectBindingKind.Node, assignment.Target.Kind);
        Assert.Equal(mark, assignment.Target.Id);
        var model = (await f.Registry.ReadPresetApplicationsAsync("client", f.Session.ProjectSessionId, "2")).Response!;
        var projection = Assert.Single(model.Applications);
        Assert.True(projection.TargetValid);
        Assert.Equal("node", projection.Target.Kind); Assert.Equal(mark, projection.Target.NodeId);
        Assert.False(projection.Target.IncludeDescendants);
        Assert.Equal(applied.PresetApplicationResult!.ApplicationIds[0], projection.ApplicationId);

        ProjectCommandResponse disabled = AssertAccepted(await f.Command("2", new SetPresetApplicationEnabledCommandDto(assignment.Id, false)));
        ProjectCommandResponse undone = AssertAccepted(await f.Command(disabled.ProjectRevision, new("history.undo")));
        ProjectCommandResponse noop = AssertAccepted(await f.Command(undone.ProjectRevision, new UpdatePresetApplicationCommandDto(
            assignment.Id, "builtin", template.TemplateId, builtin.Revision, null)));
        Assert.False(noop.Changed); Assert.True(noop.CanRedo);
        Assert.Equal(assignment.Id, Assert.Single(f.SessionProject.PresetApplications.Items).Id);

        var rejected = await f.Command(noop.ProjectRevision, apply with { Targets = [new("node", NodeId: mark, IncludeDescendants: false), new("node", NodeId: "stale-mark", IncludeDescendants: false)] });
        Assert.Equal(BackendErrorCode.CommandRejected, rejected.Error!.Code);
        Assert.Equal(noop.ProjectRevision, f.SessionRevision);
        Assert.Single(f.SessionProject.PresetApplications.Items);
        var inputMapping = await f.Command(noop.ProjectRevision, apply with { BranchInputs = new() { ["lyrics"] = "primary" } });
        Assert.Equal(BackendErrorCode.CommandRejected, inputMapping.Error!.Code);
        AssertAccepted(await f.Command(noop.ProjectRevision, new("history.redo")));
        Assert.False(Assert.Single(f.SessionProject.PresetApplications.Items).Enabled);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Node完整新读投影或回执超限在提交前拒绝(bool readProjection)
    {
        await using Fixture f = await Fixture.CreateAsync(readProjection ? 65536 : 1024);
        string root = f.SessionProject.LyricPresentation.FindProgram("primary")!.RootContainerId;
        TemplateListResponse builtin = (await f.Registry.ReadTemplatesAsync("builtin", null)).Response!;
        TemplateSummaryDto template = builtin.Templates.First(item => item.Kind != TemplateKind.Token);
        if (readProjection)
        {
            ProjectWorkspace workspace = f.Session.Workspace;
            StyleTemplate snapshot = StyleTemplate.Create("budget", "budget", StyleTemplateKind.Line,
                f.SessionProject.LyricStyle, [LyricStyleProperty.Opacity]);
            workspace.Replace(f.SessionProject with { PresetApplications = new(Enumerable.Range(0, 10001).Select(index =>
                new PresetApplication("budget/" + index, new(ProjectEffectBindingKind.Node, "missing/" + index), snapshot))) });
        }
        PresetApplicationTargetDto[] targets = [new("node", NodeId: root, IncludeDescendants: true)];
        if (!readProjection)
        {
            ProjectWorkspace workspace = f.Session.Workspace;
            string[] ids = [.. Enumerable.Range(0, 32).Select(index => "receipt/node/" + index)];
            workspace.Replace(f.SessionProject with { LyricPresentation = f.SessionProject.LyricPresentation with {
                Nodes = f.SessionProject.LyricPresentation.Nodes.AddRange(ids.Select(id => new SceneContainerDefinition { Id = id, ParentId = root })) } });
            targets = [.. ids.Select(id => new PresetApplicationTargetDto("node", NodeId: id, IncludeDescendants: false))];
        }
        Project source = f.SessionProject;
        var result = await f.Command("0", new ApplyPresetApplicationCommandDto("builtin", template.TemplateId, builtin.Revision,
            targets, null));
        Assert.Equal(BackendErrorCode.ResourceTooLarge, result.Error!.Code);
        Assert.Same(source, f.SessionProject);
        Assert.Equal("0", f.SessionRevision);
    }

    [Fact]
    public async Task 直接Hosting严格拒绝派生类型篡改继承字段和超限意图且不写入()
    {
        await using Fixture f = await Fixture.CreateAsync();
        string root = f.SessionProject.LyricPresentation.FindProgram("primary")!.RootContainerId;
        TemplateListResponse builtin = (await f.Registry.ReadTemplatesAsync("builtin", null)).Response!;
        TemplateSummaryDto template = builtin.Templates.First(item => item.Kind != TemplateKind.Token);
        ApplyPresetApplicationCommandDto apply = new("builtin", template.TemplateId, builtin.Revision,
            [new("node", NodeId: root, IncludeDescendants: true)], null);
        Project source = f.SessionProject;
        ProjectCommandDto[] invalid =
        [
            apply with { Type = "history.undo" },
            apply with { Target = new("primary", "line") },
            apply with { StartUs = "1" },
            apply with { AnalysisJobId = "job" },
            apply with { Targets = [new("node", NodeId: root)] },
            apply with { Targets = [null!] },
            new("presetApplications.apply"),
        ];
        foreach (ProjectCommandDto command in invalid)
        {
            Assert.Equal(BackendErrorCode.InvalidRequest, (await f.Command("0", command)).Error!.Code);
            Assert.Same(source, f.SessionProject);
        }
        Assert.Equal(BackendErrorCode.ResourceTooLarge, (await f.Command("0", apply with {
            BranchInputs = new() { ["input"] = new string('x', PresetApplicationLimits.MaxIntentBytes) } })).Error!.Code);
        Assert.Same(source, f.SessionProject);
        Assert.Equal("0", f.SessionRevision);
        Assert.Equal(new PresetApplicationTargetDto("project"), JsonSerializer.Deserialize<PresetApplicationTargetDto>(
            "{\"kind\":\"project\",\"branchId\":null,\"lineId\":null,\"tokenId\":null}", Json));
    }

}
