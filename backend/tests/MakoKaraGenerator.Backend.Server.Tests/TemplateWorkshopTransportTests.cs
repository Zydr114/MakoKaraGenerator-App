using System.Net;
using System.Net.Http.Json;
using MakoKaraGenerator.Backend.Contracts;

namespace MakoKaraGenerator.Backend.Server.Tests;

public sealed partial class PreviewTransportTests
{
    [Fact]
    public async Task P3工坊选项提供原单双行目标绑定和三材质槽且不创建会话()
    {
        await using Fixture f = await Fixture.Create();
        f.Projects.ConfigureTemplateLibrary();
        var library = (await f.Http.GetFromJsonAsync<TemplateListResponse>(TemplateRoot + "/builtin/templates", Json))!;
        var template = Assert.Single(library.Templates, item => item.TemplateId == "builtin:line-classic");
        TemplatePreviewSourceDto source = new("savedTemplate", "builtin", library.Revision, template.TemplateId, template.TemplateRevision);
        using var response = await f.Http.PostAsJsonAsync("/api/v1/template-preview-options", new TemplatePreviewOptionsRequest(source), Json);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var options = (await response.Content.ReadFromJsonAsync<TemplatePreviewOptionsResponse>(Json))!;
        Assert.Equal(TemplatePreviewLimits.SingleLineSampleId, options.DefaultSampleId);
        var single = Assert.Single(options.Samples, item => !item.MultiLine);
        var multi = Assert.Single(options.Samples, item => item.MultiLine);
        Assert.All(options.Samples, sample => Assert.Equal("12000000", sample.DurationUs));
        Assert.DoesNotContain(single.Targets, target => target.Key == "mark");
        var mark = Assert.Single(multi.Targets, target => target.Key == "mark");
        Assert.Equal("node", mark.Binding.Kind);
        Assert.Equal("sample-mark", mark.Binding.Id);
        Assert.Empty(mark.Binding.Ids);
        Assert.False(mark.Binding.IncludeDescendants);
        Assert.DoesNotContain(single.Targets, target => target.Key == "token");
        Assert.Collection(options.MaterialSlots,
            slot => Assert.Equal("unsungFill", slot.Slot),
            slot => Assert.Equal("sungFill", slot.Slot),
            slot => Assert.Equal("stroke", slot.Slot));
        Assert.All(options.MaterialSlots, slot => Assert.True(slot.Editable));
        Assert.All(options.MaterialSlots.Where(slot => slot.Present), slot => Assert.NotNull(slot.Material));
        Assert.Equal($"{library.Revision}:{template.TemplateRevision}", options.Source.SourceRevision);
        Assert.Equal(0, f.Previews.Count);
        Assert.Equal("0", (await f.Projects.ReadSummaryAsync(f.Client.ClientId, f.Project.ProjectSessionId))!.ProjectRevision);

        using var stale = await f.Http.PostAsJsonAsync("/api/v1/template-preview-options",
            new TemplatePreviewOptionsRequest(source with { TemplateRevision = "stale" }), Json);
        await Error(stale, HttpStatusCode.Conflict, BackendErrorCode.RevisionConflict);
    }

    [Fact]
    public async Task P3工坊拒绝不可用目标和非法槽位且旧样本不接受焦点()
    {
        await using Fixture f = await Fixture.Create();
        f.Projects.ConfigureTemplateLibrary();
        var library = (await f.Http.GetFromJsonAsync<TemplateListResponse>(TemplateRoot + "/builtin/templates", Json))!;
        var template = Assert.Single(library.Templates, item => item.TemplateId == "builtin:line-classic");
        TemplatePreviewSourceDto source = new("savedTemplate", "builtin", library.Revision, template.TemplateId, template.TemplateRevision);
        TemplatePreviewSessionRequest[] requests =
        [
            new(source, TemplatePreviewLimits.SingleLineSampleId, new(320, 180), Metadata: f.Metadata(), TargetKey: "mark"),
            new(source, TemplatePreviewLimits.MultiLineSampleId, new(320, 180), Metadata: f.Metadata(), MaterialSlot: "shadow"),
            new(source, TemplatePreviewLimits.DefaultSampleId, new(320, 180), Metadata: f.Metadata(), TargetKey: "line"),
        ];
        foreach (var request in requests)
        {
            using var response = await f.Http.PostAsJsonAsync("/api/v1/template-preview-sessions", request, Json);
            Assert.Contains(response.StatusCode, new[] { HttpStatusCode.BadRequest, HttpStatusCode.UnprocessableEntity });
            Assert.Equal(0, f.Previews.Count);
        }
        using var mixed = await f.Http.PostAsJsonAsync(
            $"/api/v1/project-sessions/{f.Project.ProjectSessionId}/preset-application-previews",
            new PresetApplicationPreviewRequest("0", "builtin", library.Revision, template.TemplateId,
                template.TemplateRevision, [new("project")], null, new(320, 180), Metadata: f.Metadata(), Source: source), Json);
        await Error(mixed, HttpStatusCode.BadRequest, BackendErrorCode.InvalidRequest);
        Assert.Equal(0, f.Previews.Count);
    }
}
