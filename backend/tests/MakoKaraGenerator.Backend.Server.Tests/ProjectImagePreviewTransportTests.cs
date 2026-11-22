using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using MakoKaraGenerator.Application;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Hosting;
using MakoKaraGenerator.Backend.Rendering;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering;

namespace MakoKaraGenerator.Backend.Server.Tests;

public sealed partial class PreviewTransportTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task 图片公共预览固定字节后原文件改变和项目关闭仍可首次渲染(bool package)
    {
        ImageRouteFactory image = new();
        await using Fixture f = await Fixture.Create(injectedFactory:
            new ProjectPreviewFrameSessionFactory(new PreviewRouteFactory(), image), imageProfile: true);
        byte[] bytes = [1, 2, 3, 4];
        ProjectSession project = await ImageProject(f, bytes, "image-fixed", package);
        ProjectSnapshot snapshot = (await project.CapturePreviewInputAsync(f.Client.ClientId, "0", f.Factory)).Input!.Snapshot;
        PreviewSessionResponse preview = await CreateImagePreview(f, project);
        Assert.Equal(ProjectRuntimeInputWorkerContract.Profile, Assert.Single(image.Profiles));
        Assert.Equal(bytes, image.Bytes); Assert.Equal(0, image.Render.Created);
        string fixedPath = Assert.IsType<string>(image.FixedPath);
        Assert.NotEqual(snapshot.Background.Path, fixedPath);
        File.WriteAllBytes(snapshot.Background.Path!, [9]);
        BackendOperationResult<JobResponse> close = await f.Projects.StartCloseAsync(f.Client.ClientId,
            project.ProjectSessionId, new("0", "discard", Metadata: f.Metadata()));
        Assert.True(close.Accepted);
        await WaitUntil(() => f.Projects.ReadJob(f.Client.ClientId, close.Response!.JobId).Response!.State == "succeeded");
        Assert.False(File.Exists(snapshot.Background.Path));
        Assert.Equal(bytes, File.ReadAllBytes(fixedPath));
        await RequestRouteFrame(f, preview, 1);
        Assert.Equal(bytes, image.Bytes); Assert.Equal(1, image.Render.Created);
        Assert.Equal(BackgroundKind.Image, image.Render.Snapshot!.Background.Kind);
        Assert.Equal(package ? 1 : 0, image.Render.Snapshot.Effects.Packages.Length);
        Assert.NotNull(image.Render.Snapshot.Background.Media);
        using HttpResponseMessage closed = await f.Http.PostAsJsonAsync($"/api/v1/preview-sessions/{preview.PreviewId}/close",
            new ClosePreviewSessionRequest(preview.PreviewGeneration, "0", f.Metadata()), Json);
        Assert.Equal(HttpStatusCode.OK, closed.StatusCode); Assert.Equal(1, image.Render.Disposed);
        Assert.False(File.Exists(fixedPath));
    }

    [Fact]
    public async Task 图片输入支持静态图片静态换代且保留独立能力路由()
    {
        PreviewRouteFactory plain = new(); ImageRouteFactory image = new();
        await using Fixture f = await Fixture.Create(injectedFactory: new ProjectPreviewFrameSessionFactory(plain, image), imageProfile: true);
        ProjectSession project = await ImageProject(f, [10, 20, 30], "image-update");
        PreviewSessionResponse preview = await f.CreatePreview(); await RequestRouteFrame(f, preview, 1);
        using HttpResponseMessage response = await f.Http.PutAsJsonAsync($"/api/v1/preview-sessions/{preview.PreviewId}/input",
            new PreviewProjectInputRequest(preview.PreviewGeneration, "0", new("project", project.ProjectSessionId, "0"),
                new(3, 2), PreviewAlphaMode.Straight, f.Metadata()), Json);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        PreviewSessionResponse changed = (await response.Content.ReadFromJsonAsync<PreviewSessionResponse>(Json))!;
        Assert.NotEqual(preview.PreviewGeneration, changed.PreviewGeneration); await RequestRouteFrame(f, changed, 2);
        Assert.Equal(1, image.Render.Created); Assert.Equal(1, plain.Disposed);
        using HttpResponseMessage restored = await f.Http.PutAsJsonAsync($"/api/v1/preview-sessions/{preview.PreviewId}/input",
            new PreviewProjectInputRequest(changed.PreviewGeneration, "0", f.Source,
                preview.Size, preview.AlphaMode, f.Metadata()), Json);
        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
        PreviewSessionResponse last = (await restored.Content.ReadFromJsonAsync<PreviewSessionResponse>(Json))!;
        await RequestRouteFrame(f, last, 3);
        Assert.Equal(2, plain.Created); Assert.Equal(1, image.Render.Disposed);
    }

    [Fact]
    public async Task 图片创建精确修订ACL和能力拒绝不读取或登记新会话()
    {
        ImageRouteFactory image = new();
        await using Fixture f = await Fixture.Create(injectedFactory:
            new LegacyPreviewFactory(new ProjectPreviewFrameSessionFactory(new PreviewRouteFactory(), image)), imageProfile: true);
        ProjectSession project = await ImageProject(f, [1, 2, 3], "image-gates");
        using HttpResponseMessage stale = await f.Http.PostAsJsonAsync("/api/v1/preview-sessions",
            new PreviewSessionRequest(new("project", project.ProjectSessionId, "1"), new(1, 1), Metadata: f.Metadata()), Json);
        await Error(stale, HttpStatusCode.Conflict, BackendErrorCode.RevisionConflict);
        ClientRegistrationResponse other = f.Clients.Register(f._security.BootstrapToken, new()).Response!;
        using HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/preview-sessions")
        { Content = JsonContent.Create(new PreviewSessionRequest(new("project", project.ProjectSessionId, "0"), new(1, 1), Metadata: f.Metadata()), options: Json) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", other.AccessToken);
        request.Headers.Add("X-Mako-Client-Id", other.ClientId);
        using HttpResponseMessage foreign = await f.Http.SendAsync(request);
        await Error(foreign, HttpStatusCode.NotFound, BackendErrorCode.SessionNotFound);
        using HttpResponseMessage disabled = await f.Http.PostAsJsonAsync("/api/v1/preview-sessions",
            new PreviewSessionRequest(new("project", project.ProjectSessionId, "0"), new(1, 1), Metadata: f.Metadata()), Json);
        BackendErrorResponse error = (await disabled.Content.ReadFromJsonAsync<BackendErrorResponse>(Json))!;
        Assert.Equal(HttpStatusCode.ServiceUnavailable, disabled.StatusCode);
        Assert.Equal("WorkerRuntimeInputProfileMismatch", error.FailureCode);
        Assert.Equal("worker", error.Field);
        Assert.Equal(BackendErrorCode.PreviewUnavailable, error.Code); Assert.Equal(0, f.Previews.Count);
        Assert.Empty(image.Profiles); Assert.Null(image.Bytes);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task 图片源大小或同长度篡改创建和更新都拒绝且不替换旧预览(bool size)
    {
        ImageRouteFactory image = new();
        await using Fixture f = await Fixture.Create(injectedFactory: new ProjectPreviewFrameSessionFactory(new PreviewRouteFactory(), image), imageProfile: true);
        ProjectSession project = await ImageProject(f, [1, 2, 3], "image-tamper");
        ProjectSnapshot snapshot = (await project.CapturePreviewInputAsync(f.Client.ClientId, "0", f.Factory)).Input!.Snapshot;
        File.WriteAllBytes(snapshot.Background.Path!, size ? [1] : [3, 2, 1]);
        PreviewSessionResponse original = await f.CreatePreview();
        using HttpResponseMessage response = await f.Http.PostAsJsonAsync("/api/v1/preview-sessions",
            new PreviewSessionRequest(new("project", project.ProjectSessionId, "0"), new(1, 1), Metadata: f.Metadata()), Json);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        AssertImageChanged((await response.Content.ReadFromJsonAsync<BackendErrorResponse>(Json))!);
        using HttpResponseMessage update = await f.Http.PutAsJsonAsync($"/api/v1/preview-sessions/{original.PreviewId}/input",
            new PreviewProjectInputRequest(original.PreviewGeneration, "0", new("project", project.ProjectSessionId, "0"),
                original.Size, original.AlphaMode, f.Metadata()), Json);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, update.StatusCode);
        AssertImageChanged((await update.Content.ReadFromJsonAsync<BackendErrorResponse>(Json))!);
        PreviewSessionStateResponse current = (await f.Http.GetFromJsonAsync<PreviewSessionStateResponse>($"/api/v1/preview-sessions/{original.PreviewId}", Json))!;
        Assert.Equal(original.PreviewGeneration, current.Session.PreviewGeneration); Assert.Equal(f.Source, current.Session.Source);
        Assert.Equal(1, f.Previews.Count); Assert.Null(image.Bytes);
    }

    [Fact]
    public async Task 项目关闭等短期图片读取排水再删除闭包且读取在Writer之外()
    {
        await using Fixture f = await Fixture.Create();
        byte[] bytes = [4, 5, 6]; ProjectSession project = await ImageProject(f, bytes, "image-close-race");
        BackendPreviewInputCaptureResult captured = await project.CapturePreviewInputAsync(f.Client.ClientId, "0", f.Factory,
            null, captureRuntimeSource: true);
        using BackendProjectRuntimeInputLease lease = captured.RuntimeInput!;
        string path = captured.Input!.Snapshot.Background.Path!;
        BackendOperationResult<JobResponse> close = await f.Projects.StartCloseAsync(f.Client.ClientId,
            project.ProjectSessionId, new("0", "discard", Metadata: f.Metadata()));
        Assert.True(close.Accepted);
        Assert.Equal(BackendErrorCode.SessionClosing, (await project.CapturePreviewInputAsync(f.Client.ClientId, "0", f.Factory)).Error!.Code);
        await Task.Delay(60);
        Assert.NotEqual("succeeded", f.Projects.ReadJob(f.Client.ClientId, close.Response!.JobId).Response!.State);
        Assert.True(File.Exists(path));
        BackendOperationResult<BackendPreparedProjectRuntimeInput> prepared = await lease.PrepareAsync();
        Assert.True(prepared.Accepted);
        using (var owned = prepared.Response!)
        {
            await WaitUntil(() => f.Projects.ReadJob(f.Client.ClientId, close.Response!.JobId).Response!.State == "succeeded");
            Assert.False(File.Exists(path));
            Assert.Equal(bytes, File.ReadAllBytes(owned.Grant!.FilePath));
        }
    }

    [Fact]
    public async Task 宿主关闭等短期图片读取排水再删除闭包()
    {
        await using Fixture f = await Fixture.Create();
        byte[] bytes = [7, 8, 9]; ProjectSession project = await ImageProject(f, bytes, "image-shutdown-race");
        BackendPreviewInputCaptureResult captured = await project.CapturePreviewInputAsync(f.Client.ClientId, "0", f.Factory,
            null, captureRuntimeSource: true);
        using BackendProjectRuntimeInputLease lease = captured.RuntimeInput!;
        string path = captured.Input!.Snapshot.Background.Path!;
        Task shutdown = f.Projects.ShutdownAsync().AsTask();
        try
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
            while ((await project.CapturePreviewInputAsync(f.Client.ClientId, "0", f.Factory)).Error?.Code != BackendErrorCode.SessionClosing)
                await Task.Delay(10, timeout.Token);
            Assert.False(shutdown.IsCompleted); Assert.True(File.Exists(path));
            BackendOperationResult<BackendPreparedProjectRuntimeInput> prepared = await lease.PrepareAsync();
            Assert.True(prepared.Accepted);
            using (var owned = prepared.Response!)
            {
                await shutdown.WaitAsync(timeout.Token);
                Assert.False(File.Exists(path));
                Assert.Equal(bytes, File.ReadAllBytes(owned.Grant!.FilePath));
            }
        }
        finally
        {
            lease.Dispose();
            await shutdown.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private static void AssertImageChanged(BackendErrorResponse error)
    { Assert.Equal(BackendErrorCode.CommandNotSupported, error.Code); Assert.Equal("ProjectRuntimeSourceChanged", error.FailureCode); Assert.Equal("background", error.Field); }

    private static async Task<ProjectSession> ImageProject(Fixture f, byte[] bytes, string name, bool package = false)
    {
        string path = Path.Combine(f._directory.Path, name + ".png"); File.WriteAllBytes(path, bytes);
        Project basis = package ? PackageProject() : ProjectWorkspace.CreateNew(name).Project;
        return await f.OpenNoAudioProject(basis with { Background = BackgroundSource.ForImage(path, BackgroundFit.Cover) }, name);
    }

    private static async Task<PreviewSessionResponse> CreateImagePreview(Fixture f, ProjectSession source)
    {
        using HttpResponseMessage response = await f.Http.PostAsJsonAsync("/api/v1/preview-sessions",
            new PreviewSessionRequest(new("project", source.ProjectSessionId, "0"), new(2, 2), Metadata: f.Metadata()), Json);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<PreviewSessionResponse>(Json))!;
    }

    private sealed class ImageRouteFactory : IRenderFrameSessionFactory, IBackendPreviewProfileCapability,
        IBackendRuntimePreviewFactory
    {
        internal PreviewRouteFactory Render { get; } = new();
        internal List<string> Profiles { get; } = [];
        internal byte[]? Bytes;
        internal string? FixedPath;
        public ValueTask<BackendErrorResponse?> EnsureProfileAsync(string profile, CancellationToken cancellationToken = default)
        { Profiles.Add(profile); return ValueTask.FromResult<BackendErrorResponse?>(null); }
        public IRenderFrameSessionFactory Bind(BackendPreparedProjectRuntimeInput input)
        {
            Assert.Equal(ProjectRuntimeInputWorkerContract.Profile, input.SourceProfile);
            ProjectRuntimeInputCodec.ValidateSnapshot(input.SourceBytes, input.Snapshot);
            var decoded = ProjectRuntimeInputCodec.Decode(input.SourceBytes);
            Assert.Equal(ProjectRuntimeInputWorkerContract.Profile, decoded.SourceProfile);
            ProjectRuntimeBackgroundGrant grant = Assert.IsType<ProjectRuntimeBackgroundGrant>(input.Grant);
            Assert.Equal(grant, decoded.Grant);
            Assert.Equal(BackgroundKind.Image, grant.Kind);
            FixedPath = grant.FilePath; Bytes = File.ReadAllBytes(grant.FilePath);
            Assert.Equal(grant.ByteLength, Bytes.LongLength);
            Assert.Equal(grant.Sha256, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Bytes)));
            return new FixedImageFactory(this, input.Snapshot, grant.FilePath, Bytes);
        }
        public ValueTask<IRenderFrameSession> CreateAsync(ProjectSnapshot snapshot, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Image preview must bind its fixed input first.");
        private sealed class FixedImageFactory(ImageRouteFactory owner, ProjectSnapshot source, string path, byte[] bytes)
            : IRenderFrameSessionFactory
        {
            public ValueTask<IRenderFrameSession> CreateAsync(ProjectSnapshot snapshot, CancellationToken cancellationToken = default)
            {
                Assert.Same(source, snapshot);
                Assert.Equal(bytes, File.ReadAllBytes(path));
                return owner.Render.CreateAsync(snapshot, cancellationToken);
            }
        }
    }
}
