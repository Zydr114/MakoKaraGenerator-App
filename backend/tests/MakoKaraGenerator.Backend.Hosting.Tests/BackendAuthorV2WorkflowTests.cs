using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using MakoKaraGenerator.Application;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Hosting;
using MakoKaraGenerator.Backend.Rendering;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.TestSupport;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

public sealed partial class BackendAuthorV2WorkflowTests
{
    private const string PureSource = """
        using MakoKaraGenerator.Scripting.V2;
        public sealed class PackEffect : IPureKaraokeEffect
        {
            public PreparedEffect Declare(DeclareContext source) => PreparedEffect.Empty;
            public void Evaluate(in TextFrame frame, PreparedEffect data, TextOutput output) { }
        }
        """;

    [Fact]
    public async Task SDK2保存和失败编译不采用且配置写入不能绕过编译门禁()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        AuthorDraftResponse original = fixture.Read();
        Assert.Equal(3, original.PackageVersion);
        Assert.Equal("2.0.0", original.SdkVersion);
        AuthorSourceRevisionResponse saved = await fixture.SaveSourceAsync("public class Broken { invalid !!! }");
        AuthorDraftResponse current = fixture.Read();
        Assert.Equal(saved.SourceRevision, current.CurrentSourceRevision);
        Assert.Equal(original.CurrentSourceRevision, current.Configuration!.SourceRevision);
        Assert.Null(current.AdoptedSourceRevision);

        BackendOperationResult<AuthorAdoptSourceRevisionResponse> rejected = await fixture.AdoptAsync(saved.SourceRevision);
        Assert.False(rejected.Accepted);
        Assert.Equal("AuthorSourceCompilationRequired", rejected.Error!.FailureCode);
        BackendOperationResult<AuthorDraftResponse> bypass = await fixture.Registry.SaveAuthorConfigurationAsync("client", current.DraftId,
            new(current.DraftRevision, Configuration(current.Configuration!, saved.SourceRevision), fixture.Metadata()));
        Assert.False(bypass.Accepted);
        Assert.Equal("AuthorSourceCompilationRequired", bypass.Error!.FailureCode);
        Assert.Equal(current.DraftRevision, fixture.Read().DraftRevision);

        AuthorCompilationResponse failed = await fixture.CompileAsync();
        Assert.False(failed.Succeeded);
        Assert.Equal(saved.SourceRevision, failed.SourceRevision);
        Assert.Equal(original.CurrentSourceRevision, fixture.Read().Configuration!.SourceRevision);
        Assert.False((await fixture.AdoptAsync(saved.SourceRevision)).Accepted);
    }

    [Fact]
    public async Task SDK2编译成功后显式采用且撤销到原包也必须先编译()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        string baseline = fixture.Read().CurrentSourceRevision;
        AuthorSourceRevisionResponse saved = await fixture.SaveSourceAsync(PureSource + "\n// edited");
        AuthorCompilationResponse compilation = await fixture.CompileAsync();
        Assert.True(compilation.Succeeded, Diagnostics(compilation));
        Assert.Equal(baseline, fixture.Read().Configuration!.SourceRevision);
        BackendOperationResult<AuthorAdoptSourceRevisionResponse> adopted = await fixture.AdoptAsync(saved.SourceRevision);
        Assert.True(adopted.Accepted, adopted.Error?.Message);
        Assert.Equal(baseline, adopted.Response!.PreviousSourceRevision);
        Assert.Equal(saved.SourceRevision, fixture.Read().Configuration!.SourceRevision);

        BackendOperationResult<AuthorAdoptSourceRevisionResponse> uncompiledUndo = await fixture.AdoptAsync(baseline);
        Assert.False(uncompiledUndo.Accepted);
        Assert.Equal("AuthorSourceCompilationRequired", uncompiledUndo.Error!.FailureCode);
        AuthorCompilationResponse baselineCompilation = await fixture.CompileAsync(baseline);
        Assert.True(baselineCompilation.Succeeded, Diagnostics(baselineCompilation));
        Assert.True(baselineCompilation.Stale);
        Assert.Equal(saved.SourceRevision, fixture.Read().Configuration!.SourceRevision);
        Assert.Equal(baseline, fixture.Read().LastCompilation!.SourceRevision);
        Assert.True(fixture.Read().LastCompilation!.Stale);
        Assert.True((await fixture.AdoptAsync(baseline)).Accepted);
        Assert.True((await fixture.AdoptAsync(saved.SourceRevision)).Accepted);
        Assert.Equal(saved.SourceRevision, fixture.Read().Configuration!.SourceRevision);

        AuthorDraftResponse beforeConflict = fixture.Read();
        Assert.True((await fixture.AdoptAsync(baseline)).Accepted);
        BackendOperationResult<AuthorAdoptSourceRevisionResponse> stale = await fixture.Registry.AdoptAuthorSourceRevisionAsync("client",
            beforeConflict.DraftId, new(beforeConflict.DraftRevision, saved.SourceRevision, fixture.Metadata()));
        Assert.Equal(BackendErrorCode.RevisionConflict, stale.Error!.Code);
        Assert.Equal(baseline, fixture.Read().Configuration!.SourceRevision);
    }

    [Fact]
    public async Task SDK2包文本保存不推进在用来源并保留旧编译身份()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        AuthorCompilationResponse compiled = await fixture.CompileAsync();
        Assert.True(compiled.Succeeded, Diagnostics(compiled));
        AuthorDraftResponse before = fixture.Read();
        AuthorPackageFilesResponse files = fixture.Registry.ReadAuthorPackageFiles("client", before.DraftId,
            before.DraftRevision, before.CurrentSourceRevision).Response!;
        BackendOperationResult<AuthorDraftResponse> saved = await fixture.Registry.SaveAuthorPackageFilesAsync("client", before.DraftId,
            new(before.DraftRevision, files.ManifestJson,
                [.. files.Files.Where(file => file.Editable).Select(file => new AuthorPackageTextFileDto(file.Path, file.Text! + "\n// package edit"))],
                Metadata: fixture.Metadata()));
        Assert.True(saved.Accepted, saved.Error?.Message);
        Assert.NotEqual(before.CurrentSourceRevision, saved.Response!.CurrentSourceRevision);
        Assert.Equal(before.Configuration!.SourceRevision, saved.Response.Configuration!.SourceRevision);
        Assert.Equal(compiled.SourceRevision, saved.Response.LastCompilation!.SourceRevision);
        Assert.True(saved.Response.LastCompilation.Stale);
        Assert.False((await fixture.AdoptAsync(saved.Response.CurrentSourceRevision)).Accepted);
    }

    [Fact]
    public async Task SDK2异步编译旧目标不验证新源码且后续保存不改变固定输入()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        AuthorDraftResponse original = fixture.Read();
        await fixture.SaveSourceAsync(PureSource + "\n// latest before queue");
        AuthorDraftResponse acceptedDraft = fixture.Read();
        BackendTaskScheduler scheduler = new(new(MaxRunning: 1));
        typeof(ProjectSessionRegistry).GetField("_taskScheduler", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(fixture.Registry, scheduler);
        using BackendTaskScheduler.Reservation blocker = scheduler.TryReserve("blocker", "blocker")!;
        BackendOperationResult<JobResponse> accepted = await fixture.Registry.StartAuthorCompilationAsync("client", original.DraftId,
            new(acceptedDraft.DraftRevision, fixture.Metadata(), "async", original.CurrentSourceRevision));
        Assert.True(accepted.Accepted, accepted.Error?.Message);
        Assert.Equal(original.CurrentSourceRevision, accepted.Response!.AuthorCompilationInput!.SourceRevision);
        AuthorSourceRevisionResponse latest = await fixture.SaveSourceAsync(PureSource + "\n// latest after queue");
        blocker.Dispose();
        await WaitForCompilationAsync(fixture.Registry, accepted.Response.JobId);
        BackendOperationResult<AuthorCompilationResponse> result = fixture.Registry.ReadAuthorCompilationResult("client", accepted.Response.JobId);
        Assert.True(result.Accepted, result.Error?.Message);
        Assert.True(result.Response!.Succeeded, Diagnostics(result.Response));
        Assert.True(result.Response.Stale);
        Assert.Equal(original.CurrentSourceRevision, result.Response.SourceRevision);
        Assert.Equal(acceptedDraft.DraftRevision, result.Response.DraftRevision);
        AuthorDraftResponse current = fixture.Read();
        Assert.Equal(latest.SourceRevision, current.CurrentSourceRevision);
        Assert.Null(current.LastCompilation);
        AuthorSourceRevisionsResponse history = (await fixture.Registry.ReadAuthorSourceRevisionsAsync("client", current.DraftId, current.DraftRevision)).Response!;
        Assert.True(history.Revisions.Single(revision => revision.SourceRevision == original.CurrentSourceRevision).Compilation!.Succeeded);
        Assert.Null(history.Revisions.Single(revision => revision.SourceRevision == latest.SourceRevision).Compilation);
        Assert.False((await fixture.AdoptAsync(latest.SourceRevision)).Accepted);
        Assert.True((await fixture.AdoptAsync(original.CurrentSourceRevision)).Accepted);
    }

    [Fact]
    public async Task SDK2编译拒绝仅实现SDK1的真实入口且目标须属于草稿历史()
    {
        await using Fixture fixture = await Fixture.CreateAsync(EffectPackageFixture.EffectSource);
        AuthorDraftResponse before = fixture.Read();
        AuthorCompilationResponse compiled = await fixture.CompileAsync();
        Assert.False(compiled.Succeeded);
        Assert.Contains(compiled.Diagnostics, diagnostic => diagnostic.Code == "PackageEntryInvalid" && diagnostic.IsError);
        Assert.False((await fixture.AdoptAsync(before.CurrentSourceRevision)).Accepted);
        string foreign = "sha256:" + new string('a', 64);
        BackendOperationResult<AuthorCompilationResponse> sync = await fixture.Registry.CompileAuthorDraftAsync("client", before.DraftId,
            new(before.DraftRevision, fixture.Metadata(), SourceRevision: foreign));
        Assert.Equal(BackendErrorCode.ResourceNotFound, sync.Error!.Code);
        BackendOperationResult<JobResponse> asyncResult = await fixture.Registry.StartAuthorCompilationAsync("client", before.DraftId,
            new(before.DraftRevision, fixture.Metadata(), "async", foreign));
        Assert.Equal(BackendErrorCode.ResourceNotFound, asyncResult.Error!.Code);
    }

    [Fact]
    public async Task SDK2作者验证在排队和准备输入前拒绝非GPU选择()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        RejectingValidationExecutor executor = new();
        fixture.Registry.ConfigureValidationTasks(executor, new() { MaxRunning = 1 });
        AuthorDraftResponse draft = fixture.Read();
        BackendOperationResult<JobResponse> result = await fixture.Registry.StartAuthorValidationAsync("client", draft.DraftId,
            new(draft.DraftRevision, TemplatePreviewLimits.SingleLineSampleId, "token", Metadata: fixture.Metadata()));
        Assert.False(result.Accepted);
        Assert.Equal(BackendErrorCode.CommandNotSupported, result.Error!.Code);
        Assert.Equal("renderBackend", result.Error.Field);
        Assert.Equal("PackageBackendUnsupported", result.Error.FailureCode);
        Assert.Equal(0, executor.Calls);
    }

    private static AuthorDraftConfigurationInputDto Configuration(AuthorDraftConfigurationDto configuration, string sourceRevision) => new(
        configuration.Name, configuration.PackageId, sourceRevision, configuration.ExportId, configuration.ParametersJson,
        configuration.RelativeTarget, configuration.ReadScope, configuration.BranchInput, configuration.FixedSeed, configuration.OverlayLayer);

    private static string Diagnostics(AuthorCompilationResponse compilation) => string.Join('\n', compilation.Diagnostics.Select(diagnostic => diagnostic.Message));

    private static async Task WaitForCompilationAsync(ProjectSessionRegistry registry, string jobId)
    {
        Stopwatch elapsed = Stopwatch.StartNew();
        while (registry.ReadJob("client", jobId).Response?.State is not ("succeeded" or "failed" or "cancelled"))
        {
            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(8), "作者编译未在8秒内结束。");
            await Task.Delay(20);
        }
        Assert.Equal("succeeded", registry.ReadJob("client", jobId).Response!.State);
    }

    private sealed class RejectingValidationExecutor : IBackendValidationExecutor, IBackendRuntimeValidationExecutor
    {
        internal int Calls;
        public bool RuntimeValidationAvailable => true;
        public Task<ValidationReportDto> ExecuteAsync(ProjectSnapshot snapshot, int framesPerOutput,
            IProgress<BackendJobProgressDto>? progress = null, CancellationToken cancellationToken = default)
        { Calls++; throw new InvalidOperationException("SDK2 CPU请求不应执行验证。"); }
        public Task<ValidationReportDto> ExecuteRuntimeAsync(ReadOnlyMemory<byte> sourceBytes, int framesPerOutput,
            IProgress<BackendJobProgressDto>? progress = null, CancellationToken cancellationToken = default)
        { Calls++; throw new InvalidOperationException("SDK2 CPU请求不应准备或执行验证。"); }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly TemporaryDirectory _directory;
        internal ProjectSessionRegistry Registry { get; }
        private readonly string _draftId;
        private Fixture(TemporaryDirectory directory, ProjectSessionRegistry registry, string draftId)
        { _directory = directory; Registry = registry; _draftId = draftId; }
        internal RequestMetadataDto Metadata() => new(Guid.NewGuid().ToString("N"), Registry.HostInstanceId, Registry.ServerTimeUtc.AddMinutes(1));
        internal AuthorDraftResponse Read() => Registry.ReadAuthorDraft("client", _draftId).Response!;
        internal async Task<AuthorSourceRevisionResponse> SaveSourceAsync(string source)
        {
            BackendOperationResult<AuthorSourceRevisionResponse> result = await Registry.SaveAuthorSourceRevisionAsync("client", _draftId,
                new(Read().DraftRevision, [new("src/PackEffect.cs", source)], Metadata()));
            Assert.True(result.Accepted, result.Error?.Message);
            return result.Response!;
        }
        internal async Task<AuthorCompilationResponse> CompileAsync(string? sourceRevision = null)
        {
            BackendOperationResult<AuthorCompilationResponse> result = await Registry.CompileAuthorDraftAsync("client", _draftId,
                new(Read().DraftRevision, Metadata(), SourceRevision: sourceRevision));
            Assert.True(result.Accepted, result.Error?.Message);
            return result.Response!;
        }
        internal Task<BackendOperationResult<AuthorAdoptSourceRevisionResponse>> AdoptAsync(string sourceRevision) => Registry.AdoptAuthorSourceRevisionAsync("client",
            _draftId, new(Read().DraftRevision, sourceRevision, Metadata()));
        internal static async Task<Fixture> CreateAsync(string source = PureSource, Action<JsonObject>? configureManifest = null)
        {
            TemporaryDirectory directory = new();
            ResourceStore resources = new(new() { ResourceRoot = directory.FilePath("resources") });
            ProjectSessionRegistry registry = new("author-sdk2", resources, NewProjectPreparationService.CreateDefault());
            registry.ConfigurePersistence(new() { StateRoot = directory.FilePath("state") });
            registry.ConfigureAuthorWorkflows(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"));
            try
            {
                Dictionary<string, byte[]> files = EffectPackageFixture.Files();
                files["src/PackEffect.cs"] = Encoding.UTF8.GetBytes(source);
                JsonObject manifest = EffectPackageFixture.Manifest(files);
                manifest["packageVersion"] = EffectPackageProfiles.V2.PackageVersion;
                manifest["sdkVersion"] = EffectPackageProfiles.V2.SdkVersion;
                manifest["shaderProfile"] = EffectPackageProfiles.V2.ShaderProfile;
                manifest["requires"] = new JsonArray("token.affine");
                manifest["lineExit"] = new JsonObject { ["durationUs"] = 0L };
                manifest["emitters"] = new JsonArray();
                manifest["exports"]![0]!["outputs"]![0]!["requires"] = new JsonArray("token.affine");
                configureManifest?.Invoke(manifest);
                EffectPackageContents package = EffectPackageContents.CreateSupported(EffectPackageFixture.Bytes(manifest), EffectPackageFixture.Inputs(files));
                string archive = directory.FilePath("package.mkfx");
                await EffectPackageTransfer.WriteArchiveAsync(archive, package);
                await using FileStream stream = File.OpenRead(archive);
                StoredAsset asset = await resources.UploadAsync("client", "package.mkfx", "application/octet-stream", stream);
                BackendOperationResult<AuthorDraftResponse> created = await registry.CreateAuthorDraftAsync("client",
                    new(asset.AssetId, "scale", "author sdk2", new(Guid.NewGuid().ToString("N"), registry.HostInstanceId, registry.ServerTimeUtc.AddMinutes(1))));
                Assert.True(created.Accepted, created.Error?.Message);
                return new(directory, registry, created.Response!.DraftId);
            }
            catch { await registry.DisposeAsync(); directory.Dispose(); throw; }
        }
        public async ValueTask DisposeAsync() { await Registry.DisposeAsync(); _directory.Dispose(); }
    }
}
