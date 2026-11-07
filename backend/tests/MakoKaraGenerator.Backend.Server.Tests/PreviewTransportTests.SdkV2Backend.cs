using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MakoKaraGenerator.Application;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Hosting;
using MakoKaraGenerator.Backend.Rendering;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.TestSupport;

namespace MakoKaraGenerator.Backend.Server.Tests;

public sealed partial class PreviewTransportTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SDK2工程预览采用解析后GPU选择并保留默认GPU(bool explicitGpu)
    {
        // This seam proves admission/retention only. It does not claim GPU rendering.
        Factory rendering = new();
        SdkV2BackendFactory factory = new(rendering);
        using BackendRenderBackendConfiguration devices = SdkV2GpuDevices();
        await using Fixture fixture = await Fixture.Create(injectedFactory: factory, packageProfile: true, renderBackends: devices);
        EffectPackageContents package = SdkV2PreviewPackage();
        Project project = ProjectWorkspace.CreateNew("sdk2-project").Project with
        {
            Effects = new([package], [ScriptConfiguration.Create("config", "sdk2", package, "scale")
                .CreateInstance("sdk2-effect", new(ProjectEffectBindingKind.Project), 1)]),
        };
        ProjectSession opened = await fixture.OpenNoAudioProject(project, "sdk2-project");
        using HttpResponseMessage response = await fixture.Http.PostAsJsonAsync("/api/v1/preview-sessions",
            new PreviewSessionRequest(new("project", opened.ProjectSessionId, "0"), new(1, 1), Metadata: fixture.Metadata(),
                RenderBackend: explicitGpu ? "gpu" : null), Json);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        PreviewSessionResponse preview = (await response.Content.ReadFromJsonAsync<PreviewSessionResponse>(Json))!;
        Assert.Equal("gpu", preview.RenderBackend);
        // C#-only packages keep the static package profile; only private media inputs need Bind.
        Assert.Equal(0, factory.State.Bindings);
        Assert.Equal(PackageProjectSnapshotCodec.Profile, Assert.Single(factory.State.Profiles));
        Assert.Equal(0, rendering.Created);
        await AssertSdkV2CpuSwitchPreservesAsync(fixture, preview);
        var closed = await fixture.Service.CloseAsync(fixture.Client.ClientId, preview.PreviewId,
            new(preview.PreviewGeneration, preview.Source.SourceRevision, fixture.Metadata()));
        Assert.True(closed.Accepted, closed.Error?.Message);
        Assert.Equal(BackendErrorCode.PreviewSessionNotFound,
            fixture.Previews.Read(fixture.Client.ClientId, preview.PreviewId).Error!.Code);
    }

    [Fact]
    public async Task SDK2作者预览CPU创建及换输入在准备前拒绝且默认GPU保持固定来源()
    {
        SdkV2BackendFactory cpuFactory = new(new Factory());
        await using Fixture cpuFixture = await Fixture.Create(injectedFactory: cpuFactory);
        PreviewSourceDto cpuSource = await SdkV2AuthorSourceAsync(cpuFixture);
        PreviewSessionRequest cpuRequest = new(cpuSource, new(1, 1), Metadata: cpuFixture.Metadata(),
            PreviewKind: "authorDraft", Committable: false);
        using HttpResponseMessage refused = await cpuFixture.Http.PostAsJsonAsync("/api/v1/preview-sessions", cpuRequest, Json);
        BackendErrorResponse cpuError = await Error(refused, HttpStatusCode.UnprocessableEntity, BackendErrorCode.CommandNotSupported);
        Assert.Equal("PackageBackendUnsupported", cpuError.FailureCode);
        Assert.Equal(0, cpuFactory.State.Bindings);
        Assert.Equal(0, cpuFactory.State.ProfileChecks);
        Assert.Equal(0, cpuFixture.Previews.Count);
        // A trusted legacy CPU session must also fail closed when its input is refreshed.
        ProjectSnapshot legacySnapshot = ProjectSnapshot.FromProject(ProjectWorkspace.CreateNew("legacy-author").Project);
        BackendPreviewSessionResult legacySession = cpuFixture.Previews.Create(cpuFixture.Client.ClientId, cpuRequest,
            new(cpuSource, legacySnapshot, cpuFactory));
        Assert.True(legacySession.Accepted, legacySession.Error?.Message);
        PreviewSessionResponse legacyPreview = legacySession.Status!.Session;
        using HttpResponseMessage refusedInput = await cpuFixture.Http.PutAsJsonAsync($"/api/v1/preview-sessions/{legacyPreview.PreviewId}/input",
            new PreviewProjectInputRequest(legacyPreview.PreviewGeneration, cpuSource.SourceRevision, cpuSource,
                legacyPreview.Size, legacyPreview.AlphaMode, cpuFixture.Metadata()), Json);
        Assert.Equal("PackageBackendUnsupported", (await Error(refusedInput, HttpStatusCode.UnprocessableEntity,
            BackendErrorCode.CommandNotSupported)).FailureCode);
        AssertSdkV2SessionWireEqual(legacyPreview,
            cpuFixture.Previews.Read(cpuFixture.Client.ClientId, legacyPreview.PreviewId).Status!.Session);
        Assert.Equal(0, cpuFactory.State.Bindings);

        Factory rendering = new();
        SdkV2BackendFactory factory = new(rendering);
        using BackendRenderBackendConfiguration devices = SdkV2GpuDevices();
        await using Fixture fixture = await Fixture.Create(injectedFactory: factory, renderBackends: devices);
        PreviewSourceDto source = await SdkV2AuthorSourceAsync(fixture);
        PreviewSessionRequest request = new(source, new(1, 1), Metadata: fixture.Metadata(),
            PreviewKind: "authorDraft", Committable: false);
        // Legacy composition without a supplied choice resolves CPU and must fail closed.
        BackendOperationResult<AuthorPreviewCapture> legacy = await fixture.Projects.CaptureAuthorPreviewAsync(fixture.Client.ClientId, source);
        Assert.Equal("PackageBackendUnsupported", legacy.Error!.FailureCode);
        BackendOperationResult<AuthorPreviewCapture> cpu = await fixture.Projects.CaptureAuthorPreviewAsync(fixture.Client.ClientId, source, "cpu");
        Assert.Equal(BackendErrorCode.CommandNotSupported, cpu.Error!.Code);
        Assert.Equal("PackageBackendUnsupported", cpu.Error.FailureCode);
        Assert.Equal(0, factory.State.Bindings);
        Assert.Equal(0, factory.State.ProfileChecks);
        Assert.Equal(0, fixture.Previews.Count);

        using HttpResponseMessage accepted = await fixture.Http.PostAsJsonAsync("/api/v1/preview-sessions",
            request with { Metadata = fixture.Metadata() }, Json);
        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
        PreviewSessionResponse preview = (await accepted.Content.ReadFromJsonAsync<PreviewSessionResponse>(Json))!;
        Assert.Equal("gpu", preview.RenderBackend);
        Assert.Equal(1, factory.State.Bindings);
        await AssertSdkV2CpuSwitchPreservesAsync(fixture, preview);
        // Keep current GPU when omitted on input replacement; the original source must stay exact.
        using HttpResponseMessage updated = await fixture.Http.PutAsJsonAsync($"/api/v1/preview-sessions/{preview.PreviewId}/input",
            new PreviewProjectInputRequest(preview.PreviewGeneration, preview.Source.SourceRevision, source,
                preview.Size, preview.AlphaMode, fixture.Metadata()), Json);
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        PreviewSessionResponse next = (await updated.Content.ReadFromJsonAsync<PreviewSessionResponse>(Json))!;
        Assert.Equal("gpu", next.RenderBackend);
        Assert.Equal(source, next.Source);
        await fixture.Service.CloseAsync(fixture.Client.ClientId, next.PreviewId,
            new(next.PreviewGeneration, next.Source.SourceRevision, fixture.Metadata()));
        Assert.All(factory.State.Prepared, input => Assert.Throws<ObjectDisposedException>(() => input.AcquireReference()));
    }

    [Fact]
    public async Task SDK2CPU公共捕获拒绝释放输入租约和资源预留且不执行准备()
    {
        await using Fixture fixture = await Fixture.Create();
        EffectPackageContents package = SdkV2PreviewPackage();
        Project project = ProjectWorkspace.CreateNew("sdk2-reject").Project with
        {
            Effects = new([package], [ScriptConfiguration.Create("config", "sdk2", package, "scale")
                .CreateInstance("sdk2-effect", new(ProjectEffectBindingKind.Project), 1)]),
        };
        ProjectSnapshot snapshot = ProjectSnapshot.FromProject(project);
        BackendProjectRuntimeInputLease runtime = BackendProjectRuntimeInputLease.CreateDetached(snapshot);
        ControlledRetention reservation = new(false);
        BackendPreviewInputCaptureResult captured = new(new(new("project", "source", "0"), snapshot, fixture.Factory), null, RuntimeInput: runtime);
        MethodInfo complete = typeof(PreviewBackendService).GetMethod("CompleteCaptureAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        BackendPreviewInputCaptureResult result = await (Task<BackendPreviewInputCaptureResult>)complete.Invoke(fixture.Service, [captured, reservation, false])!;
        Assert.Equal("PackageBackendUnsupported", result.Error!.FailureCode);
        Assert.Equal(1, reservation.Count);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => runtime.PrepareAsync());
        Assert.Equal(0, fixture.Factory.ProfileChecks);
        Assert.Equal(0, fixture.Previews.Count);
    }

    private static async Task AssertSdkV2CpuSwitchPreservesAsync(Fixture fixture, PreviewSessionResponse preview)
    {
        using HttpResponseMessage switched = await fixture.Http.PutAsJsonAsync($"/api/v1/preview-sessions/{preview.PreviewId}/render-backend",
            new PreviewRenderBackendRequest(preview.PreviewGeneration, "cpu", fixture.Metadata()), Json);
        BackendErrorResponse error = await Error(switched, HttpStatusCode.UnprocessableEntity, BackendErrorCode.CommandNotSupported);
        Assert.Equal("PackageBackendUnsupported", error.FailureCode);
        Assert.Equal("renderBackend", error.Field);
        PreviewSessionStateResponse current = (await fixture.Http.GetFromJsonAsync<PreviewSessionStateResponse>(
            $"/api/v1/preview-sessions/{preview.PreviewId}", Json))!;
        AssertSdkV2SessionWireEqual(preview, current.Session);
    }

    private static void AssertSdkV2SessionWireEqual(PreviewSessionResponse expected, PreviewSessionResponse actual)
    {
        // Record equality compares closure arrays by reference; compare every wire field and nested value.
        Assert.Equal(JsonSerializer.Serialize(expected, Json), JsonSerializer.Serialize(actual, Json));
    }

    private static BackendRenderBackendConfiguration SdkV2GpuDevices()
    {
        BackendRenderBackendConfiguration configuration = new(new WorkerProcessOptions("missing-worker"), "vulkan");
        configuration.ConfigureGpuStreamDevice(_ => Task.FromResult(new RenderBackendDeviceInfoDto("fixture", "device", "driver", "1.3")));
        return configuration;
    }

    private static async Task<PreviewSourceDto> SdkV2AuthorSourceAsync(Fixture fixture)
    {
        fixture.Projects.ConfigureAuthorWorkflows(null);
        string archive = fixture._directory.FilePath("sdk2-author.mkfx");
        await EffectPackageTransfer.WriteArchiveAsync(archive, SdkV2PreviewPackage());
        await using FileStream stream = File.OpenRead(archive);
        StoredAsset asset = await fixture.Resources.UploadAsync(fixture.Client.ClientId, "sdk2-author.mkfx", "application/octet-stream", stream);
        BackendOperationResult<AuthorDraftResponse> created = await fixture.Projects.CreateAuthorDraftAsync(fixture.Client.ClientId,
            new(asset.AssetId, "scale", "sdk2-author", fixture.Metadata()));
        Assert.True(created.Accepted, created.Error?.Message);
        AuthorDraftResponse draft = created.Response!;
        return new("authorDraft", draft.DraftId, draft.DraftRevision,
            new(TemplatePreviewLimits.SingleLineSampleId, "token"), draft.Configuration!.SourceRevision);
    }

    private static EffectPackageContents SdkV2PreviewPackage()
    {
        Dictionary<string, byte[]> files = EffectPackageFixture.Files();
        files["src/PackEffect.cs"] = Encoding.UTF8.GetBytes("""
            using MakoKaraGenerator.Scripting.V2;
            public sealed class PackEffect : IPureKaraokeEffect
            {
                public PreparedEffect Declare(DeclareContext source) => PreparedEffect.Empty;
                public void Evaluate(in TextFrame frame, PreparedEffect data, TextOutput output) { }
            }
            """);
        JsonObject manifest = EffectPackageFixture.Manifest(files);
        manifest["packageVersion"] = EffectPackageProfiles.V2.PackageVersion;
        manifest["sdkVersion"] = EffectPackageProfiles.V2.SdkVersion;
        manifest["shaderProfile"] = EffectPackageProfiles.V2.ShaderProfile;
        manifest["requires"] = new JsonArray("token.affine");
        manifest["lineExit"] = new JsonObject { ["durationUs"] = 0L };
        manifest["emitters"] = new JsonArray();
        manifest["exports"]![0]!["bindingKinds"] = new JsonArray("project", "tokens");
        manifest["exports"]![0]!["outputs"]![0]!["requires"] = new JsonArray("token.affine");
        return EffectPackageContents.CreateSupported(EffectPackageFixture.Bytes(manifest), EffectPackageFixture.Inputs(files));
    }

    private sealed class SdkV2BackendFactory(Factory inner, string renderBackend = "cpu", SdkV2BackendState? state = null) :
        IRenderFrameSessionFactory, IBackendRuntimePreviewFactory, IBackendPreviewProfileCapability, IBackendPreviewRenderBackendFactory
    {
        internal SdkV2BackendState State { get; } = state ?? new();
        public string RenderBackend => renderBackend;
        public IRenderFrameSessionFactory WithRenderBackend(string backend) => new SdkV2BackendFactory(inner, backend, State);
        public IRenderFrameSessionFactory Bind(BackendPreparedProjectRuntimeInput input)
        { State.Bindings++; State.Prepared.Add(input); return this; }
        public ValueTask<BackendErrorResponse?> EnsureProfileAsync(string profile, CancellationToken cancellationToken = default)
        { State.ProfileChecks++; State.Profiles.Add(profile); return ValueTask.FromResult<BackendErrorResponse?>(null); }
        public ValueTask<IRenderFrameSession> CreateAsync(ProjectSnapshot snapshot, CancellationToken cancellationToken = default) =>
            inner.CreateAsync(snapshot, cancellationToken);
    }

    private sealed class SdkV2BackendState
    {
        internal int Bindings;
        internal int ProfileChecks;
        internal readonly List<string> Profiles = [];
        internal readonly List<BackendPreparedProjectRuntimeInput> Prepared = [];
    }
}
