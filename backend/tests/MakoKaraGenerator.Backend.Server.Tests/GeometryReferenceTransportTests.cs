using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using MakoKaraGenerator.Application;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Hosting;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Backend.Server.Tests;

public sealed partial class PreviewTransportTests
{
    [Fact]
    public async Task G22真实HTTP固定几何参考精确修订认证与只读历史()
    {
        await using Fixture f = await Fixture.Create();
        string path = $"/api/v1/project-sessions/{f.Project.ProjectSessionId}";
        var caps = (await f.Http.GetFromJsonAsync<BackendCapabilitiesResponse>("/api/v1/capabilities", Json))!;
        Assert.Contains("project-sessions.orchestration.geometry-reference.read", caps.Features);
        var workspace = GeometryWorkspace(f);
        var parent = new SceneContainerDefinition { Id = "geometry/root", Placement = ScenePlacement.Create(new(.25, .25), ScenePlacementAnchor.TopLeft, 90, 2), ReferenceRect = new(.5, .5) };
        var child = new SceneContainerDefinition { Id = "geometry/child", ParentId = parent.Id, Placement = ScenePlacement.Create(new(.5, .5), ScenePlacementAnchor.TopLeft), ReferenceRect = new(.5, .5) };
        workspace.Replace(workspace.Project with { LyricPresentation = workspace.Project.LyricPresentation with { Nodes = [parent, child] } });
        Project before = workspace.Project;
        string query = path + "/orchestration/geometry?expectedRevision=0&nodeId=" + Uri.EscapeDataString(child.Id);
        using HttpResponseMessage response = await f.Http.GetAsync(query);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        var actual = (await response.Content.ReadFromJsonAsync<ProjectGeometryReferenceResponse>(Json))!;
        Assert.False(actual.FinalPixelGeometry); Assert.Equal("fixedContainerReferenceV1", actual.ReferenceKind);
        Assert.Equal("0", actual.ProjectRevision); Assert.Equal(child.Id, actual.NodeId);
        Assert.Equal(160, actual.Reference.Width); Assert.Equal(90, actual.Reference.Height);
        // Independently calculated: canvas (640,360), parent's origin (160,90),
        // rotated/scaled child origin = (-20,410). Ordered corners retain rotation.
        AssertPoint(-20, 410, actual.Reference.WorldCorners[0]);
        AssertPoint(-20, 730, actual.Reference.WorldCorners[1]);
        AssertPoint(-200, 730, actual.Reference.WorldCorners[2]);
        AssertPoint(-200, 410, actual.Reference.WorldCorners[3]);
        Assert.Same(before, workspace.Project); Assert.Equal(0, workspace.History.UndoCount); Assert.Equal(0, f.Factory.Created);
        using HttpResponseMessage stale = await f.Http.GetAsync(query.Replace("expectedRevision=0", "expectedRevision=1", StringComparison.Ordinal));
        await Error(stale, HttpStatusCode.Conflict, BackendErrorCode.RevisionConflict);
        using HttpResponseMessage missing = await f.Http.GetAsync(path + "/orchestration/geometry?expectedRevision=0&nodeId=missing");
        await Error(missing, HttpStatusCode.NotFound, BackendErrorCode.ResourceNotFound);
        var other = f.Clients.Register(f._security.BootstrapToken, new()).Response!;
        using HttpRequestMessage unauthorized = new(HttpMethod.Get, query);
        unauthorized.Headers.Authorization = new AuthenticationHeaderValue("Bearer", other.AccessToken);
        unauthorized.Headers.Add("X-Mako-Client-Id", other.ClientId);
        using HttpResponseMessage isolated = await f.Http.SendAsync(unauthorized);
        await Error(isolated, HttpStatusCode.NotFound, BackendErrorCode.SessionNotFound);
        f.Http.DefaultRequestHeaders.Authorization = null;
        using HttpResponseMessage unauthenticated = await f.Http.GetAsync(query);
        await Error(unauthenticated, HttpStatusCode.Unauthorized, BackendErrorCode.UnauthorizedClient);
    }

    private static ProjectWorkspace GeometryWorkspace(Fixture f) => (ProjectWorkspace)typeof(ProjectSession)
        .GetProperty("Workspace", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(f.Project)!;

    private static void AssertPoint(double x, double y, ProjectGeometryPointDto actual)
    { Assert.Equal(x, actual.X, 8); Assert.Equal(y, actual.Y, 8); }

    [Theory]
    [InlineData("")]
    [InlineData("?expectedRevision=0")]
    [InlineData("?expectedRevision=0&expectedRevision=0&nodeId=n")]
    [InlineData("?expectedRevision=0&nodeId=n&nodeId=n")]
    [InlineData("?expectedRevision=0&nodeId=n&timeUs=0")]
    [InlineData("?expectedRevision=-1&nodeId=n")]
    [InlineData("?expectedRevision=0&nodeId=")]
    public async Task G22真实HTTP拒绝非法查询形状(string query)
    {
        await using Fixture f = await Fixture.Create();
        using HttpResponseMessage response = await f.Http.GetAsync($"/api/v1/project-sessions/{f.Project.ProjectSessionId}/orchestration/geometry" + query);
        await Error(response, HttpStatusCode.BadRequest, BackendErrorCode.InvalidRequest);
        Assert.Equal(0, GeometryWorkspace(f).History.UndoCount);
    }

    [Theory]
    [InlineData("budget")]
    [InlineData("cycle")]
    [InlineData("overflow")]
    public async Task G22真实HTTP预算和非法几何返回稳定失败码(string shape)
    {
        await using Fixture f = await Fixture.Create();
        var workspace = GeometryWorkspace(f);
        SceneContainerDefinition[] nodes = shape == "budget"
            ? [.. Enumerable.Range(0, ProjectGeometryReferenceLimits.MaxNodes + 1).Select(i => new SceneContainerDefinition { Id = "g" + i.ToString(CultureInfo.InvariantCulture) })]
            : [new SceneContainerDefinition { Id = "g0", ParentId = shape == "cycle" ? "g0" : null,
                ReferenceRect = new(shape == "overflow" ? double.MaxValue : 1, 1) }];
        workspace.Replace(workspace.Project with { LyricPresentation = workspace.Project.LyricPresentation with { Nodes = [.. nodes] } });
        using HttpResponseMessage response = await f.Http.GetAsync($"/api/v1/project-sessions/{f.Project.ProjectSessionId}/orchestration/geometry?expectedRevision=0&nodeId=g0");
        var expected = shape == "budget" ? BackendErrorCode.ResourceTooLarge : BackendErrorCode.CommandRejected;
        Assert.Equal(shape == "budget" ? HttpStatusCode.RequestEntityTooLarge : HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        var error = (await response.Content.ReadFromJsonAsync<BackendErrorResponse>(Json))!;
        Assert.Equal(expected, error.Code);
        Assert.Equal(shape == "budget" ? "GeometryReferenceBudgetExceeded" : "GeometryReferenceInvalid", error.FailureCode);
        Assert.Equal(0, workspace.History.UndoCount);
    }
}
