using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using MakoKaraGenerator.Application;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Hosting;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Backend.Server.Tests;

public sealed partial class PreviewTransportTests
{
    [Fact]
    public async Task HTTP辉光叶值显式关闭和继承可往返()
    {
        await using var f = await Fixture.Create();
        string path = $"/api/v1/project-sessions/{f.Project.ProjectSessionId}";
        var lyrics = (await f.Http.GetFromJsonAsync<ProjectLyricsWindowResponse>(path + "/lyrics/window?branchId=primary&expectedRevision=0", Json))!;
        string target = JsonSerializer.Serialize(new TokenStyleTargetDto("primary", lyrics.Lines[0].Id, lyrics.Lines[0].Tokens[0].Id), Json);
        using var set = await PostRawEditing(f, "0", $$"""{"type":"styles.patch","targets":[{{target}}],"changes":[{"property":"glow.blurRadiusPx","value":6},{"property":"glow.spreadPx","value":2}]}""");
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        string route = path + "/style?expectedRevision=1&kind=token&branchId=primary&lineId=" + Uri.EscapeDataString(lyrics.Lines[0].Id) + "&tokenId=" + Uri.EscapeDataString(lyrics.Lines[0].Tokens[0].Id);
        using var read = await f.Http.GetAsync(route); Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        using var document = JsonDocument.Parse(await read.Content.ReadAsStringAsync());
        Assert.Equal(6, document.RootElement.GetProperty("staticHierarchy").GetProperty("glow").GetProperty("blurRadiusPx").GetSingle());
        using var close = await PostRawEditing(f, "1", $$"""{"type":"styles.patch","targets":[{{target}}],"changes":[{"property":"glow","value":null}]}""");
        Assert.Equal(HttpStatusCode.OK, close.StatusCode);
        using var restore = await PostRawEditing(f, "2", $$"""{"type":"styles.restore","targets":[{{target}}],"properties":["glow"]}""");
        Assert.Equal(HttpStatusCode.OK, restore.StatusCode);
        using var invalid = await PostRawEditing(f, "3", $$"""{"type":"styles.patch","targets":[{{target}}],"changes":[{"property":"glow.opacity","value":null}]}""");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }
    [Fact]
    public async Task HTTP样式二命令四层实际JSON显式空值和历史刷新保持一致()
    {
        await using var f = await Fixture.Create();
        string path = $"/api/v1/project-sessions/{f.Project.ProjectSessionId}";
        var lyrics = (await f.Http.GetFromJsonAsync<ProjectLyricsWindowResponse>(path + "/lyrics/window?branchId=primary&expectedRevision=0", Json))!;
        var line = lyrics.Lines[0]; string target = JsonSerializer.Serialize(new LineStyleTargetDto("primary", line.Id), Json);
        string command = $$"""{"type":"styles.patch","targets":[{{target}}],"changes":[{"property":"typography.fontFamilyName","value":null},{"property":"stroke","value":null},{"property":"shadow","value":null},{"property":"opacity","value":0.5}]}""";
        using var applied = await PostRawEditing(f, "0", command); Assert.Equal(HttpStatusCode.OK, applied.StatusCode);
        var response = (await applied.Content.ReadFromJsonAsync<ProjectCommandResponse>(Json))!;
        Assert.Equal(1, response.StyleResult!.ChangedTargetCount); Assert.True(response.ChangeSet.RequiresFullRefresh);
        Assert.Equal(["styles", "renderInputs"], response.ChangeSet.Areas); Assert.Equal([line.Id], response.ChangeSet.LineIds);
        string route = path + "/style?expectedRevision=1&kind=line&branchId=primary&lineId=" + Uri.EscapeDataString(line.Id);
        using var read = await f.Http.GetAsync(route); Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        string text = await read.Content.ReadAsStringAsync(); using var document = JsonDocument.Parse(text); var root = document.RootElement;
        Assert.Equal("staticHierarchy", root.GetProperty("resolution").GetString()); Assert.Equal("1", root.GetProperty("protocolVersion").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("staticHierarchy").GetProperty("typography").GetProperty("fontFamilyName").ValueKind);
        foreach (string effect in new[] { "stroke", "shadow" }) Assert.Equal(JsonValueKind.Null, root.GetProperty("staticHierarchy").GetProperty(effect).ValueKind);
        foreach (string property in new[] { "typography.fontFamilyName", "stroke", "shadow" })
            Assert.Equal(JsonValueKind.Null, root.GetProperty("local").EnumerateArray().Single(v => v.GetProperty("property").GetString() == property).GetProperty("value").ValueKind);
        foreach (string query in new[] { "kind=project", "kind=branch&branchId=primary", "kind=token&branchId=primary&lineId=" + line.Id + "&tokenId=" + line.Tokens[0].Id })
        {
            using var layer = await f.Http.GetAsync(path + "/style?expectedRevision=1&" + query); Assert.Equal(HttpStatusCode.OK, layer.StatusCode);
        }
        using var restored = await PostRawEditing(f, "1", $$"""{"type":"styles.restore","targets":[{{target}}],"properties":["typography.fontFamilyName","stroke","shadow","opacity"]}""");
        Assert.Equal(HttpStatusCode.OK, restored.StatusCode); Assert.Equal("styles.restore", (await restored.Content.ReadFromJsonAsync<ProjectCommandResponse>(Json))!.UndoName);
        using var undo = await PostRawEditing(f, "2", "{\"type\":\"history.undo\"}"); Assert.Equal(HttpStatusCode.OK, undo.StatusCode);
        var undone = (await undo.Content.ReadFromJsonAsync<ProjectCommandResponse>(Json))!; Assert.Null(undone.StyleResult); Assert.Equal(["project"], undone.ChangeSet.Areas); Assert.True(undone.ChangeSet.RequiresFullRefresh);
        using var redo = await PostRawEditing(f, "3", "{\"type\":\"history.redo\"}"); Assert.Equal(HttpStatusCode.OK, redo.StatusCode);
        Assert.True((await redo.Content.ReadFromJsonAsync<ProjectCommandResponse>(Json))!.ChangeSet.RequiresFullRefresh);
        var capabilities = (await f.Http.GetFromJsonAsync<BackendCapabilitiesResponse>("/api/v1/capabilities", Json))!;
        foreach (string feature in new[] { "project-sessions.style.read", "project-sessions.commands.styles.patch", "project-sessions.commands.styles.restore" }) Assert.Contains(feature, capabilities.Features);
    }

    [Fact]
    public async Task HTTP二十四属性完整typedwire保渐变几何且新枚举准确大小写()
    {
        await using var f = await Fixture.Create();
        var line = (await f.Http.GetFromJsonAsync<ProjectLyricsWindowResponse>($"/api/v1/project-sessions/{f.Project.ProjectSessionId}/lyrics/window?branchId=primary&expectedRevision=0", Json))!.Lines[0];
        string target = JsonSerializer.Serialize(new LineStyleTargetDto("primary", line.Id), Json);
        const string material = "{\"kind\":\"gradient\",\"gradient\":{\"kind\":\"linear\",\"angleDegrees\":123,\"centerX\":-3,\"centerY\":4,\"radiusX\":-5,\"radiusY\":-6,\"coordinateSpace\":\"glyph\",\"colorSpace\":\"oklch\",\"huePath\":\"decreasing\",\"stops\":[{\"offset\":0,\"color\":{\"red\":1,\"green\":2,\"blue\":3,\"alpha\":4}},{\"offset\":0,\"color\":{\"red\":5,\"green\":6,\"blue\":7,\"alpha\":8}},{\"offset\":1,\"color\":{\"red\":9,\"green\":10,\"blue\":11,\"alpha\":12}}]}}";
        string[] properties = ["typography.fontFamilyName", "typography.fallbackFamilyNames", "typography.fontSizePx", "typography.fontWeight", "typography.isBold", "typography.isItalic", "typography.letterSpacingPx", "unsungFill.material", "sungFill.material", "stroke.material", "stroke.widthPx", "stroke.join", "stroke.position", "shadow.color", "shadow.opacity", "shadow.angleDegrees", "shadow.distancePx", "shadow.blurRadiusPx", "verticalCenterRatio", "horizontalAlignment", "opacity", "scale"];
        string[] values = ["\"   \"", "[\"A\",\"B\"]", "88.5", "425", "true", "true", "-1.5", material, material, material, "3.25", "\"bevel\"", "\"outside\"", "{\"red\":20,\"green\":30,\"blue\":40,\"alpha\":50}", "0.75", "321.5", "4.5", "9.25", "0.6", "\"right\"", "0.8", "1.25"];
        string changes = string.Join(',', properties.Zip(values, (property, value) => "{\"property\":\"" + property + "\",\"value\":" + value + "}"));
        string command = $$"""{"type":"styles.patch","targets":[{{target}}],"changes":[{{changes}}]}""";
        using var first = await PostRawEditing(f, "0", command); Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        using var second = await PostRawEditing(f, "1", command); Assert.Equal(HttpStatusCode.OK, second.StatusCode); Assert.False((await second.Content.ReadFromJsonAsync<ProjectCommandResponse>(Json))!.Changed);
        string whole = $$$"""{"type":"styles.patch","targets":[{{{target}}}],"changes":[{"property":"stroke","value":{"material":{{{material}}},"widthPx":7,"join":"round","position":"outside"}},{"property":"shadow","value":{"color":{"red":1,"green":2,"blue":3,"alpha":4},"opacity":0.5,"angleDegrees":45,"distancePx":2,"blurRadiusPx":3}}]}""";
        using var full = await PostRawEditing(f, "1", whole); Assert.Equal(HttpStatusCode.OK, full.StatusCode);
        using var read = await f.Http.GetAsync($"/api/v1/project-sessions/{f.Project.ProjectSessionId}/style?kind=line&branchId=primary&lineId={line.Id}&expectedRevision=2");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode); var style = (await read.Content.ReadFromJsonAsync<ProjectStyleResponse>(Json))!;
        var gradient = Assert.IsType<GradientStyleMaterialDto>(style.StaticHierarchy.UnsungFill.Material).Gradient;
        Assert.Equal(-5, gradient.RadiusX); Assert.Equal(-3, gradient.CenterX); Assert.Equal(0, gradient.Stops[1].Offset); Assert.Equal(StyleGradientColorSpace.Oklch, gradient.ColorSpace);
        Assert.Equal(425, style.StaticHierarchy.Typography.FontWeight); Assert.Equal("   ", style.StaticHierarchy.Typography.FontFamilyName);
        Assert.DoesNotContain(style.Local, value => value.Property is "stroke" or "shadow"); Assert.Equal(7, style.StaticHierarchy.Stroke!.WidthPx);
    }

    [Theory]
    [InlineData("")]
    [InlineData("?kind=project")]
    [InlineData("?expectedRevision=0")]
    [InlineData("?expectedRevision=0&kind=Project")]
    [InlineData("?expectedRevision=0&kind=project&branchId=primary")]
    [InlineData("?expectedRevision=0&kind=project&kind=project")]
    [InlineData("?expectedRevision=0&kind=branch")]
    [InlineData("?expectedRevision=0&kind=line&branchId=primary")]
    [InlineData("?expectedRevision=0&kind=token&branchId=primary&lineId=l")]
    [InlineData("?expectedRevision=0&kind=project&unknown=1")]
    [InlineData("?expectedRevision=0&Kind=project")]
    public async Task HTTP样式读取严格唯一查询与目标联合(string query)
    {
        await using var f = await Fixture.Create();
        using var response = await f.Http.GetAsync($"/api/v1/project-sessions/{f.Project.ProjectSessionId}/style" + query);
        await Error(response, HttpStatusCode.BadRequest, BackendErrorCode.InvalidRequest);
    }

    public static IEnumerable<object[]> InvalidStyleChanges()
    {
        string[] values = [
            "{\"property\":\"opacity\"}", "{\"property\":\"opacity\",\"value\":null}", "{\"property\":\"opacity\",\"value\":\"0.5\"}",
            "{\"property\":\"opacity\",\"value\":0.5,\"value\":0.5}", "{\"property\":\"Opacity\",\"value\":0.5}", "{\"property\":\"opacity\",\"value\":0.5,\"x\":1}",
            "{\"property\":\"horizontalAlignment\",\"value\":\"Right\"}", "{\"property\":\"horizontalAlignment\",\"value\":2}",
            "{\"property\":\"stroke\",\"value\":{\"material\":{\"kind\":\"solid\",\"color\":{\"red\":0,\"green\":0,\"blue\":0,\"alpha\":255}},\"widthPx\":1,\"join\":\"ROUND\",\"position\":\"outside\"}}",
            "{\"property\":\"shadow.color\",\"value\":{\"red\":0,\"green\":0,\"blue\":0}}", "{\"property\":\"shadow.color\",\"value\":{\"red\":0,\"green\":0,\"blue\":0,\"alpha\":255,\"extra\":1}}",
            "{\"property\":\"shadow.color\",\"value\":{\"red\":\"0\",\"green\":0,\"blue\":0,\"alpha\":255}}", "{\"property\":\"shadow.color\",\"value\":{\"red\":0,\"red\":1,\"green\":0,\"blue\":0,\"alpha\":255}}",
            "{\"property\":\"unsungFill.material\",\"value\":null}", "{\"property\":\"unsungFill.material\",\"value\":{\"kind\":\"package\",\"packageId\":\"p\",\"revision\":\"r\",\"exportId\":\"e\"}}",
            "{\"property\":\"typography.fallbackFamilyNames\",\"value\":[null]}", "{\"property\":\"typography.isBold\",\"value\":1}", "{\"property\":\"typography.fontWeight\",\"value\":425.5}",
        ];
        return values.Select(v => new object[] { v });
    }
    [Theory]
    [MemberData(nameof(InvalidStyleChanges))]
    public async Task HTTP新样式全部嵌套strict且stale目标不掩盖形状400(string change)
    {
        await using var f = await Fixture.Create();
        using var response = await PostRawEditing(f, "0", $$"""{"type":"styles.patch","targets":[{"kind":"line","branchId":"missing","lineId":"missing"}],"changes":[{{change}}]}""");
        await Error(response, HttpStatusCode.BadRequest, BackendErrorCode.InvalidRequest);
        Assert.Equal("0", (await f.Projects.ReadHistoryAsync(f.Client.ClientId, f.Project.ProjectSessionId))!.ProjectRevision);
    }

    [Theory]
    [InlineData("{\"type\":\"styles.patch\",\"targets\":[{\"kind\":\"project\",\"lineId\":null}],\"changes\":[{\"property\":\"opacity\",\"value\":0.5}]}")]
    [InlineData("{\"type\":\"styles.patch\",\"targets\":[{\"kind\":\"project\"},{\"kind\":\"project\"}],\"changes\":[{\"property\":\"opacity\",\"value\":0.5}]}")]
    [InlineData("{\"type\":\"styles.patch\",\"targets\":[{\"kind\":\"project\"},{\"kind\":\"branch\",\"branchId\":\"primary\"}],\"changes\":[{\"property\":\"opacity\",\"value\":0.5}]}")]
    [InlineData("{\"type\":\"styles.patch\",\"targets\":[{\"kind\":\"project\"}],\"changes\":[{\"property\":\"opacity\",\"value\":0.5},{\"property\":\"opacity\",\"value\":0.5}]}")]
    [InlineData("{\"type\":\"styles.patch\",\"targets\":[{\"kind\":\"project\"}],\"changes\":[{\"property\":\"stroke\",\"value\":null},{\"property\":\"stroke.widthPx\",\"value\":3}]}")]
    [InlineData("{\"type\":\"styles.restore\",\"targets\":[{\"kind\":\"branch\",\"branchId\":\"primary\"}],\"properties\":[\"shadow\",\"shadow.color\"]}")]
    [InlineData("{\"type\":\"styles.restore\",\"targets\":[{\"kind\":\"branch\",\"branchId\":\"primary\"}],\"properties\":[\"opacity\",\"opacity\"]}")]
    [InlineData("{\"type\":\"styles.patch\",\"targets\":[],\"changes\":[]}")]
    [InlineData("{\"type\":\"styles.restore\",\"targets\":[{\"kind\":\"project\"}],\"properties\":[]}")]
    [InlineData("{\"type\":\"styles.patch\",\"targets\":[{\"kind\":\"project\"}],\"changes\":[{\"property\":\"opacity\",\"value\":0.5}],\"startUs\":null}")]
    public async Task HTTP样式目标重复混种父子冲突和跨type字段400(string command)
    {
        await using var f = await Fixture.Create(); using var response = await PostRawEditing(f, "0", command);
        await Error(response, HttpStatusCode.BadRequest, BackendErrorCode.InvalidRequest);
    }

    [Fact]
    public async Task HTTP样式新值与意图资源门禁413且拒绝无修订历史变化()
    {
        await using var f = await Fixture.Create();
        string largeFont = JsonSerializer.Serialize(new string('f', 257));
        string[] commands = [
            $$"""{"type":"styles.patch","targets":[{"kind":"project"}],"changes":[{"property":"typography.fontFamilyName","value":{{largeFont}}}]}""",
            "{\"type\":\"styles.patch\",\"targets\":[{\"kind\":\"branch\",\"branchId\":\"" + new string('i', 66000) + "\"}],\"changes\":[{\"property\":\"opacity\",\"value\":0.5}]}",
        ];
        foreach (string command in commands) { using var response = await PostRawEditing(f, "0", command); await Error(response, HttpStatusCode.RequestEntityTooLarge, BackendErrorCode.ResourceTooLarge); }
        Assert.Equal(0, (await f.Projects.ReadHistoryAsync(f.Client.ClientId, f.Project.ProjectSessionId))!.UndoCount);
    }

    [Fact]
    public async Task HTTP样式认证归属只读关闭与长精确ID保合法入口()
    {
        await using var f = await Fixture.Create(); string path = $"/api/v1/project-sessions/{f.Project.ProjectSessionId}";
        var workspace = (ProjectWorkspace)typeof(ProjectSession).GetProperty("Workspace", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(f.Project)!;
        string branch = " " + new string('b', 129) + " "; string line = " " + new string('l', 129) + " "; string token = " " + new string('t', 129) + " ";
        var secondary = LyricBranch.Create(branch, "secondary", lines: [LyricLine.Create(line, "字", tokens: [LyricToken.Create(token, "字")])]);
        workspace.Replace(workspace.Project with { Lyrics = workspace.Project.Lyrics with { Branches = [workspace.Project.Lyrics.Branches[0], secondary] } });
        string target = JsonSerializer.Serialize<StyleTargetDto>(new TokenStyleTargetDto(branch, line, token), Json);
        using var patch = await PostRawEditing(f, "0", $$"""{"type":"styles.patch","targets":[{{target}}],"changes":[{"property":"opacity","value":0.5}]}"""); Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
        string query = "/style?expectedRevision=1&kind=token&branchId=" + Uri.EscapeDataString(branch) + "&lineId=" + Uri.EscapeDataString(line) + "&tokenId=" + Uri.EscapeDataString(token);
        using var read = await f.Http.GetAsync(path + query); Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal(new TokenStyleTargetDto(branch, line, token), (await read.Content.ReadFromJsonAsync<ProjectStyleResponse>(Json))!.Target);
        using var missing = await f.Http.GetAsync(path + "/style?expectedRevision=1&kind=branch&branchId=missing"); await Error(missing, HttpStatusCode.NotFound, BackendErrorCode.CommandRejected);
        using var stale = await f.Http.GetAsync(path + "/style?expectedRevision=0&kind=project"); await Error(stale, HttpStatusCode.Conflict, BackendErrorCode.RevisionConflict);
        var other = f.Clients.Register(f._security.BootstrapToken, new()).Response!;
        using var request = new HttpRequestMessage(HttpMethod.Get, path + query); request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", other.AccessToken); request.Headers.Add("X-Mako-Client-Id", other.ClientId);
        using var denied = await f.Http.SendAsync(request); await Error(denied, HttpStatusCode.NotFound, BackendErrorCode.SessionNotFound);
        typeof(ProjectSession).GetProperty("IsReadOnly", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(f.Project, true);
        using var ro = await PostRawEditing(f, "1", "{\"type\":\"styles.patch\",\"targets\":[{\"kind\":\"project\"}],\"changes\":[{\"property\":\"opacity\",\"value\":0.3}]}"); await Error(ro, HttpStatusCode.Forbidden, BackendErrorCode.ReadOnlySession);
        typeof(ProjectSession).GetProperty("IsClosed", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(f.Project, true);
        using var closed = await f.Http.GetAsync(path + query); await Error(closed, HttpStatusCode.NotFound, BackendErrorCode.SessionNotFound);
        f.Http.DefaultRequestHeaders.Authorization = null; using var unauthenticated = await f.Http.GetAsync(path + query); await Error(unauthenticated, HttpStatusCode.Unauthorized, BackendErrorCode.UnauthorizedClient);
    }
    [Fact]
    public async Task HTTP已载入非法样式为有界422而失效目标仍404()
    {
        await using var f = await Fixture.Create();
        var workspace = (ProjectWorkspace)typeof(ProjectSession).GetProperty("Workspace", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(f.Project)!;
        workspace.Replace(workspace.Project with { LyricStyle = workspace.Project.LyricStyle with { FontSizePx = 0 } });
        string path = $"/api/v1/project-sessions/{f.Project.ProjectSessionId}/style?expectedRevision=0&";
        using var invalid = await f.Http.GetAsync(path + "kind=project"); Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);
        var detail = (await invalid.Content.ReadFromJsonAsync<BackendErrorResponse>(Json))!; Assert.Equal(BackendErrorCode.CommandRejected, detail.Code); Assert.Equal("InvalidValue", detail.FailureCode); Assert.Equal("style", detail.Field);
        using var missing = await f.Http.GetAsync(path + "kind=branch&branchId=missing"); await Error(missing, HttpStatusCode.NotFound, BackendErrorCode.CommandRejected);
        using var patch = await PostRawEditing(f, "0", "{\"type\":\"styles.patch\",\"targets\":[{\"kind\":\"project\"}],\"changes\":[{\"property\":\"opacity\",\"value\":0.4}]}");
        await Error(patch, HttpStatusCode.UnprocessableEntity, BackendErrorCode.CommandRejected);
        Assert.Equal("0", (await f.Projects.ReadHistoryAsync(f.Client.ClientId, f.Project.ProjectSessionId))!.ProjectRevision);
    }

}
