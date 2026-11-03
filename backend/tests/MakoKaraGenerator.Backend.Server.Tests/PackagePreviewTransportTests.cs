using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Threading.Channels;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Hosting;
using MakoKaraGenerator.Backend.Rendering;
using MakoKaraGenerator.Application;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering;

namespace MakoKaraGenerator.Backend.Server.Tests;

public sealed partial class PreviewTransportTests
{
    [Fact]
    public async Task 包预览租约到期跨客户端ACL与恢复重建保持项目和包输入()
    {
        PreviewRouteFactory plain = new(), package = new();
        await using Fixture f = await Fixture.Create(leaseSeconds: 2,
            injectedFactory: new ProjectPreviewFrameSessionFactory(plain, package), packageProfile: true);
        ProjectSession source = await f.OpenNoAudioProject(PackageProject(), "package-lease");
        PreviewSessionResponse preview = await CreatePackagePreview(f, source, new(2, 2));
        await RequestRouteFrame(f, preview, 1);

        ClientRegistrationResponse other = f.Clients.Register(f._security.BootstrapToken, new()).Response!;
        using HttpRequestMessage foreignState = new(HttpMethod.Get,
            $"/api/v1/preview-sessions/{preview.PreviewId}");
        foreignState.Headers.Authorization = new AuthenticationHeaderValue("Bearer", other.AccessToken);
        foreignState.Headers.Add("X-Mako-Client-Id", other.ClientId);
        using HttpResponseMessage state = await f.Http.SendAsync(foreignState);
        await Error(state, HttpStatusCode.NotFound, BackendErrorCode.PreviewSessionNotFound);

        using HttpRequestMessage foreignFrame = new(HttpMethod.Get,
            preview.LatestFrame?.Content.ContentUrl ?? (await f.WaitReady(preview.PreviewId)).Session.LatestFrame!.Content.ContentUrl);
        foreignFrame.Headers.Authorization = new AuthenticationHeaderValue("Bearer", other.AccessToken);
        foreignFrame.Headers.Add("X-Mako-Client-Id", other.ClientId);
        using HttpResponseMessage frame = await f.Http.SendAsync(foreignFrame);
        Assert.Equal(HttpStatusCode.Unauthorized, frame.StatusCode);

        f.Clock.Advance(3);
        await f.Service.DrainExpiredClientsAsync();
        Assert.Equal(0, f.Previews.Count);
        Assert.NotNull(await f.Projects.ReadSummaryAsync(f.Client.ClientId, source.ProjectSessionId));
        using HttpResponseMessage expired = await f.Http.PostAsJsonAsync("/api/v1/preview-sessions",
            new PreviewSessionRequest(new("project", source.ProjectSessionId, "0"), new(2, 2), Metadata: f.Metadata()), Json);
        await Error(expired, HttpStatusCode.Unauthorized, BackendErrorCode.ClientTokenExpired);

        f.Resume();
        PreviewSessionResponse resumed = await CreatePackagePreview(f, source, new(2, 2));
        await RequestRouteFrame(f, resumed, 1);
        Assert.Equal(2, package.Created);
        using HttpResponseMessage close = await f.Http.PostAsJsonAsync(
            $"/api/v1/preview-sessions/{resumed.PreviewId}/close",
            new ClosePreviewSessionRequest(resumed.PreviewGeneration, resumed.Source.SourceRevision, f.Metadata()), Json);
        Assert.Equal(HttpStatusCode.OK, close.StatusCode);
    }

    [Fact]
    public async Task 包预览输入换代时迟到Worker帧不发布且新代次可继续渲染()
    {
        PreviewRouteFactory plain = new(), package = new() { BlockRender = true, IgnoreRenderCancellation = true };
        await using Fixture f = await Fixture.Create(
            injectedFactory: new ProjectPreviewFrameSessionFactory(plain, package), packageProfile: true);
        ProjectSession source = await f.OpenNoAudioProject(PackageProject(), "package-late-frame");
        PreviewSessionResponse preview = await CreatePackagePreview(f, source, new(2, 2));
        using HttpResponseMessage accepted = await f.Http.PostAsJsonAsync(
            $"/api/v1/preview-sessions/{preview.PreviewId}/frame-requests",
            f.FrameRequest(preview, 1, 0), Json);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        PackageRenderCall old = await package.NextRender();
        string oldGeneration = preview.PreviewGeneration;

        using HttpResponseMessage changed = await f.Http.PutAsJsonAsync(
            $"/api/v1/preview-sessions/{preview.PreviewId}/input",
            new PreviewProjectInputRequest(preview.PreviewGeneration, "0",
                new("project", source.ProjectSessionId, "0"), new(3, 2), PreviewAlphaMode.Straight,
                    f.Metadata()), Json);
        PreviewSessionResponse current = (await changed.Content.ReadFromJsonAsync<PreviewSessionResponse>(Json))!;
        Assert.NotEqual(oldGeneration, current.PreviewGeneration);
        old.Release.SetResult();
        await WaitUntil(() => package.Disposed >= 1);

        using HttpResponseMessage nextAccepted = await f.Http.PostAsJsonAsync(
            $"/api/v1/preview-sessions/{current.PreviewId}/frame-requests",
            f.FrameRequest(current, 2, 100000), Json);
        Assert.Equal(HttpStatusCode.Accepted, nextAccepted.StatusCode);
        PackageRenderCall next = await package.NextRender();
        next.Release.SetResult();
        PreviewSessionStateResponse ready = await f.WaitReady(current.PreviewId);
        Assert.Equal(current.PreviewGeneration, ready.Session.PreviewGeneration);
        Assert.Equal("2", ready.Session.LatestFrame!.RequestSeq);
        Assert.DoesNotContain(f.Projects.ReadEvents(f.Client.ClientId, null).Events,
            e => e.Type == "preview.frameReady" && e.Preview?.PreviewGeneration == oldGeneration);
        await f.Service.CloseAsync(f.Client.ClientId, current.PreviewId,
            new(current.PreviewGeneration, current.Source.SourceRevision, f.Metadata()));
    }

    [Fact]
    public async Task 包预览连续播放换尺寸透明度回退和旧帧句柄按代次排水()
    {
        PreviewRouteFactory plain = new(), package = new();
        await using Fixture f = await Fixture.Create(
            injectedFactory: new ProjectPreviewFrameSessionFactory(plain, package), packageProfile: true);
        ProjectSession source = await f.OpenNoAudioProject(PackageProject(), "package-continuous");

        using HttpResponseMessage created = await f.Http.PostAsJsonAsync("/api/v1/preview-sessions",
            new PreviewSessionRequest(new("project", source.ProjectSessionId, "0"), new(2, 2),
                PreviewAlphaMode.Opaque, Metadata: f.Metadata()), Json);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        PreviewSessionResponse preview = (await created.Content.ReadFromJsonAsync<PreviewSessionResponse>(Json))!;
        Assert.NotNull(preview.PackageClosure);
        Assert.Equal(PackageProject().Effects.Packages.Length, preview.PackageClosure!.Packages.Length);
        Assert.Equal(PackageProject().Effects.Packages.Sum(package => package.Files.Length), preview.PackageClosure.FileCount);
        Assert.StartsWith("sha256:", preview.PackageClosure.ClosureRevision, StringComparison.Ordinal);

        // Two forward requests stay in one package Worker session and preserve the
        // same generation while the coordinator reuses its latest pending slot.
        await RequestRouteFrame(f, preview, 1);
        await RequestRouteFrame(f, preview, 2);
        Assert.Equal(1, package.Created);
        Assert.Equal(2, package.Frames.Count);
        Assert.All(package.Frames, frame =>
        {
            Assert.Equal((2, 2), (frame.Width, frame.Height));
            Assert.False(frame.Transparent);
        });
        PreviewSessionStateResponse firstState = await f.WaitReady(preview.PreviewId);
        string oldFrameUrl = firstState.Session.LatestFrame!.Content.ContentUrl;
        using (HttpResponseMessage oldFrame = await f.Http.GetAsync(oldFrameUrl))
            Assert.Equal(HttpStatusCode.OK, oldFrame.StatusCode);

        // Input replacement is an atomic generation switch. The old frame store and
        // diagnostics are invalidated before the new package session is created.
        using HttpResponseMessage changed = await f.Http.PutAsJsonAsync(
            $"/api/v1/preview-sessions/{preview.PreviewId}/input",
            new PreviewProjectInputRequest(preview.PreviewGeneration, "0",
                new("project", source.ProjectSessionId, "0"), new(3, 2), PreviewAlphaMode.Straight,
                    f.Metadata()), Json);
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        PreviewSessionResponse resized = (await changed.Content.ReadFromJsonAsync<PreviewSessionResponse>(Json))!;
        Assert.NotEqual(preview.PreviewGeneration, resized.PreviewGeneration);
        using (HttpResponseMessage staleFrame = await f.Http.GetAsync(oldFrameUrl))
            Assert.Equal(HttpStatusCode.NotFound, staleFrame.StatusCode);

        await RequestRouteFrame(f, resized, 3);
        await WaitUntil(() => package.Created == 2 && package.Disposed >= 1);
        Assert.Equal(2, package.Created);
        Assert.Contains(package.Frames, frame => frame.Width == 3 && frame.Height == 2 && frame.Transparent);

        // A backward seek is a second generation transition. The old generation
        // cannot accept a late request, even though its request sequence is lower.
        using HttpResponseMessage seekResponse = await f.Http.PostAsJsonAsync(
            $"/api/v1/preview-sessions/{resized.PreviewId}/frame-requests",
            new PreviewFrameCommandRequest(resized.Source.SourceRevision, resized.PreviewGeneration,
                "4", "0", "seek", f.Metadata()), Json);
        Assert.Equal(HttpStatusCode.Accepted, seekResponse.StatusCode);
        PreviewFrameAcceptedResponse seek = (await seekResponse.Content.ReadFromJsonAsync<PreviewFrameAcceptedResponse>(Json))!;
        Assert.NotEqual(resized.PreviewGeneration, seek.PreviewGeneration);
        using HttpResponseMessage oldGeneration = await f.Http.PostAsJsonAsync(
            $"/api/v1/preview-sessions/{resized.PreviewId}/frame-requests",
            new PreviewFrameCommandRequest(resized.Source.SourceRevision, resized.PreviewGeneration,
                "5", "100000", "playback", f.Metadata()), Json);
        Assert.Equal(HttpStatusCode.Conflict, oldGeneration.StatusCode);

        PreviewSessionStateResponse seekReady = await f.WaitReady(resized.PreviewId);
        Assert.Equal(seek.PreviewGeneration, seekReady.Session.PreviewGeneration);
        Assert.Equal("4", seekReady.Session.LatestFrame!.RequestSeq);
        Assert.Equal(2, package.Created);
        Assert.Equal(1, package.Disposed);

        using HttpResponseMessage close = await f.Http.PostAsJsonAsync(
            $"/api/v1/preview-sessions/{resized.PreviewId}/close",
            new ClosePreviewSessionRequest(seekReady.Session.PreviewGeneration,
                seekReady.Session.Source.SourceRevision, f.Metadata()), Json);
        Assert.Equal(HttpStatusCode.OK, close.StatusCode);
        await WaitUntil(() => package.Disposed == 2);
        using HttpResponseMessage closedFrame = await f.Http.GetAsync(
            seekReady.Session.LatestFrame!.Content.ContentUrl);
        Assert.Equal(HttpStatusCode.NotFound, closedFrame.StatusCode);
    }

    [Fact]
    public async Task 公共项目输入可静态包静态切换且未污染原静态工厂()
    {
        PreviewRouteFactory plain = new(), package = new();
        await using Fixture f = await Fixture.Create(injectedFactory: new ProjectPreviewFrameSessionFactory(plain, package), packageProfile: true);
        ProjectSession source = await f.OpenNoAudioProject(PackageProject(), "package");
        PreviewSessionResponse preview = await f.CreatePreview();
        await RequestRouteFrame(f, preview, 1);
        Assert.Equal(1, plain.Created); Assert.Equal(0, package.Created);
        using HttpResponseMessage changed = await f.Http.PutAsJsonAsync($"/api/v1/preview-sessions/{preview.PreviewId}/input",
            new PreviewProjectInputRequest(preview.PreviewGeneration, "0", new("project", source.ProjectSessionId, "0"),
                preview.Size, preview.AlphaMode, f.Metadata()), Json);
        Assert.True(changed.StatusCode == HttpStatusCode.OK, await changed.Content.ReadAsStringAsync());
        PreviewSessionResponse packagePreview = (await changed.Content.ReadFromJsonAsync<PreviewSessionResponse>(Json))!;
        Assert.NotEqual(preview.PreviewGeneration, packagePreview.PreviewGeneration);
        await RequestRouteFrame(f, packagePreview, 2);
        Assert.Equal(1, package.Created);
        Assert.Contains(PackageProjectSnapshotCodec.Profile, package.Profiles);
        Assert.NotNull(package.Snapshot); Assert.Single(package.Snapshot.Effects.Packages);
        using HttpResponseMessage restored = await f.Http.PutAsJsonAsync($"/api/v1/preview-sessions/{preview.PreviewId}/input",
            new PreviewProjectInputRequest(packagePreview.PreviewGeneration, "0", f.Source,
                preview.Size, preview.AlphaMode, f.Metadata()), Json);
        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
        PreviewSessionResponse last = (await restored.Content.ReadFromJsonAsync<PreviewSessionResponse>(Json))!;
        await RequestRouteFrame(f, last, 3);
        Assert.Equal(2, plain.Created); Assert.Equal(1, package.Created);
    }

    [Fact]
    public async Task 未公布包能力被统一输入准入拒绝且不静态回退也不登记新会话()
    {
        PreviewRouteFactory plain = new(), package = new();
        await using Fixture f = await Fixture.Create(injectedFactory: new LegacyPreviewFactory(new ProjectPreviewFrameSessionFactory(plain, package)));
        ProjectSession source = await f.OpenNoAudioProject(PackageProject(), "package");
        using HttpResponseMessage response = await f.Http.PostAsJsonAsync("/api/v1/preview-sessions",
            new PreviewSessionRequest(new("project", source.ProjectSessionId, "0"), new(1, 1), Metadata: f.Metadata()), Json);
        BackendErrorResponse error = await response.Content.ReadFromJsonAsync<BackendErrorResponse>(Json) ?? throw new InvalidOperationException();
        Assert.Equal(BackendErrorCode.PreviewUnavailable, error.Code);
        Assert.Equal("WorkerRuntimeInputProfileMismatch", error.FailureCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("worker", error.Field);
        Assert.Equal(0, f.Previews.Count); Assert.Equal(0, plain.Created); Assert.Equal(0, package.Created);
        Assert.Empty(package.Profiles);
    }

    [Fact]
    public async Task 宣布包来源但工厂没有实际能力接口仍拒绝且不执行()
    {
        NoCapabilityFactory factory = new();
        await using Fixture f = await Fixture.Create(injectedFactory: factory, packageProfile: true);
        ProjectSession source = await f.OpenNoAudioProject(PackageProject(), "package");
        using HttpResponseMessage response = await f.Http.PostAsJsonAsync("/api/v1/preview-sessions",
            new PreviewSessionRequest(new("project", source.ProjectSessionId, "0"), new(1, 1), Metadata: f.Metadata()), Json);
        BackendErrorResponse error = (await response.Content.ReadFromJsonAsync<BackendErrorResponse>(Json))!;
        Assert.Equal(BackendErrorCode.PreviewUnavailable, error.Code);
        Assert.Equal("WorkerPackageProfileMismatch", error.FailureCode);
        Assert.False(factory.Called); Assert.Equal(0, f.Previews.Count);
    }

    [Fact]
    public async Task 包输入创建后关闭项目仍保固定包字节并可首次渲染()
    {
        PreviewRouteFactory package = new();
        await using Fixture f = await Fixture.Create(injectedFactory: new ProjectPreviewFrameSessionFactory(new PreviewRouteFactory(), package), packageProfile: true);
        Project project = PackageProject();
        ProjectSession source = await f.OpenNoAudioProject(project, "package");
        using HttpResponseMessage created = await f.Http.PostAsJsonAsync("/api/v1/preview-sessions",
            new PreviewSessionRequest(new("project", source.ProjectSessionId, "0"), new(1, 1), Metadata: f.Metadata()), Json);
        Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        PreviewSessionResponse preview = (await created.Content.ReadFromJsonAsync<PreviewSessionResponse>(Json))!;
        BackendOperationResult<JobResponse> close = await f.Projects.StartCloseAsync(f.Client.ClientId,
            source.ProjectSessionId, new("0", "discard", Metadata: f.Metadata()));
        Assert.True(close.Accepted);
        await WaitUntil(() => f.Projects.ReadJob(f.Client.ClientId, close.Response!.JobId).Response!.State == "succeeded");
        await RequestRouteFrame(f, preview, 1);
        Assert.Equal(project.Effects.Packages[0].Manifest.ToArray(), package.Snapshot!.Effects.Packages[0].Manifest.ToArray());
        Assert.Equal(project.Effects.Packages[0].Files.Select(x => x.Path), package.Snapshot.Effects.Packages[0].Files.Select(x => x.Path));
    }

    [Fact]
    public async Task HTTP资源清单公开效果包闭包身份role和有限摘要()
    {
        await using Fixture f = await Fixture.Create();
        Project project = PackageProject();
        ProjectSession source = await f.OpenNoAudioProject(project, "package-resources");
        using HttpResponseMessage response = await f.Http.GetAsync(
            $"/api/v1/project-sessions/{source.ProjectSessionId}/resources?expectedRevision=0");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        ProjectResourcesResponse resources = (await response.Content.ReadFromJsonAsync<ProjectResourcesResponse>(Json))!;
        EffectPackageContents package = project.Effects.Packages[0];
        ProjectResourceDto manifest = Assert.Single(resources.Resources,
            item => item.Kind == "effectPackageManifest" && item.PackageRevision == package.Revision);
        Assert.Equal("validated", manifest.Status);
        Assert.Equal("manifest", manifest.PackageRole);
        Assert.Equal(package.Manifest.Length.ToString(CultureInfo.InvariantCulture), manifest.SizeBytes);
        Assert.Equal(package.Files.Length, manifest.PackageClosure!.FileCount);
        Assert.Equal(package.Files.Length, manifest.PackageClosure.ValidatedFileCount);
        Assert.Contains(resources.Resources, item => item.Kind == "effectPackageFile"
            && item.PackagePath == "src/PackEffect.cs" && item.PackageRole == "csharp"
            && item.Status == "validated" && item.PackageId == package.PackageId);
    }

    [Fact]
    public async Task 资源捕获回调和快照共享写者且过期修订不调用回调并异常后释放写者()
    {
        await using Fixture f = await Fixture.Create();
        Task<ProjectSessionSummaryResponse?>? blocked = null;
        int called = 0;
        IRenderFrameSessionFactory Capture(string audioId)
        {
            called++;
            Assert.Equal(f.Project.AudioAssetId, audioId);
            blocked = f.Projects.ReadSummaryAsync(f.Client.ClientId, f.Project.ProjectSessionId);
            Assert.False(blocked.IsCompleted);
            return f.Factory;
        }
        Assert.NotNull((await f.Project.CapturePreviewInputAsync(f.Client.ClientId, "1", f.Factory, Capture)).Error);
        Assert.Equal(0, called);
        BackendPreviewInputCaptureResult result = await f.Project.CapturePreviewInputAsync(f.Client.ClientId, "0", f.Factory, Capture);
        Assert.Null(result.Error); Assert.Equal(1, called); Assert.NotNull(await blocked!);
        Assert.Same(f.Factory, result.Input!.Factory);
        await Assert.ThrowsAsync<IOException>(() => f.Project.CapturePreviewInputAsync(f.Client.ClientId, "0", f.Factory,
            _ => throw new IOException("reservation failed")));
        Assert.NotNull(await f.Projects.ReadSummaryAsync(f.Client.ClientId, f.Project.ProjectSessionId));
    }

    private static Project PackageProject()
    {
        Project project = ProjectWorkspace.CreateNew("package-preview").Project;
        var contents = MakoKaraGenerator.TestSupport.EffectPackageFixture.Contents();
        return project with { Effects = new([contents]) };
    }

    private static async Task<PreviewSessionResponse> CreatePackagePreview(Fixture f, ProjectSession source,
        PreviewSizeDto size)
    {
        using HttpResponseMessage response = await f.Http.PostAsJsonAsync("/api/v1/preview-sessions",
            new PreviewSessionRequest(new("project", source.ProjectSessionId, "0"), size,
                Metadata: f.Metadata()), Json);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<PreviewSessionResponse>(Json))!;
    }

    private static async Task RequestRouteFrame(Fixture f, PreviewSessionResponse preview, long sequence)
    {
        using HttpResponseMessage accepted = await f.Http.PostAsJsonAsync($"/api/v1/preview-sessions/{preview.PreviewId}/frame-requests",
            f.FrameRequest(preview, sequence, sequence), Json);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        Assert.NotNull((await f.WaitReady(preview.PreviewId)).Session.LatestFrame);
    }

    private sealed class NoCapabilityFactory : IRenderFrameSessionFactory
    {
        internal bool Called;
        public ValueTask<IRenderFrameSession> CreateAsync(ProjectSnapshot snapshot, CancellationToken cancellationToken = default)
        { Called = true; throw new InvalidOperationException("Must not execute without capability verification."); }
    }

    private sealed class PreviewRouteFactory : IRenderFrameSessionFactory, IBackendPreviewProfileCapability
    {
        internal int Created;
        internal int Disposed;
        internal List<string> Profiles { get; } = [];
        internal List<(int Width, int Height, bool Transparent, long TimeUs)> Frames { get; } = [];
        internal bool BlockRender;
        internal bool IgnoreRenderCancellation;
        internal Channel<PackageRenderCall> BlockedRenders { get; } = Channel.CreateUnbounded<PackageRenderCall>();
        internal ProjectSnapshot? Snapshot;
        internal Task<PackageRenderCall> NextRender() => BlockedRenders.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        public ValueTask<BackendErrorResponse?> EnsureProfileAsync(string profile, CancellationToken cancellationToken = default)
        { Profiles.Add(profile); return ValueTask.FromResult<BackendErrorResponse?>(null); }
        public ValueTask<IRenderFrameSession> CreateAsync(ProjectSnapshot snapshot, CancellationToken cancellationToken = default)
        { Created++; Snapshot = snapshot; return ValueTask.FromResult<IRenderFrameSession>(new Session(this)); }
        private sealed class Session(PreviewRouteFactory owner) : IRenderFrameSession
        {
            public async ValueTask<RenderFrame> RenderAsync(RenderRequest request, CancellationToken cancellationToken = default)
            {
                RenderTarget target = request.ResolveTarget();
                lock (owner.Frames)
                    owner.Frames.Add((target.PixelWidth, target.PixelHeight, request.TransparentBackground, request.TimeUs));
                if (owner.BlockRender)
                {
                    TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    owner.BlockedRenders.Writer.TryWrite(new(release));
                    if (owner.IgnoreRenderCancellation) await release.Task.ConfigureAwait(false);
                    else await release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                byte[] pixels = new byte[target.PixelWidth * target.PixelHeight * 4];
                return new RenderFrame(pixels, target, PixelFormat.Bgra8888Premultiplied, request.TimeUs);
            }
            public ValueTask DisposeAsync() { Interlocked.Increment(ref owner.Disposed); return ValueTask.CompletedTask; }
        }
    }

    private sealed record PackageRenderCall(TaskCompletionSource Release);
}
