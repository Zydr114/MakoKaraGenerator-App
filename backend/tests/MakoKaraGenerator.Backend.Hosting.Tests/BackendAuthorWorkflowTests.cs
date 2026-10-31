using System.Globalization;
using System.Diagnostics;
using MakoKaraGenerator.Application;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Hosting;
using MakoKaraGenerator.TestSupport;
using Xunit.Abstractions;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

public sealed class BackendAuthorWorkflowTests(ITestOutputHelper output)
{
    [Fact]
    public async Task 作者异步编译返回任务并读取固定源码结果()
    {
        using TemporaryDirectory directory = new();
        string archive = directory.FilePath("demo.mkfx");
        await EffectPackageTransfer.WriteArchiveAsync(archive, EffectPackageFixture.Contents());
        ResourceStore resources = new(new() { ResourceRoot = directory.FilePath("resources") });
        await using FileStream stream = File.OpenRead(archive);
        StoredAsset asset = await resources.UploadAsync("client", "demo.mkfx", "application/octet-stream", stream);
        ProjectSessionRegistry registry = NewRegistry("host-async", resources, directory.FilePath("state"), Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"));
        try
        {
            AuthorDraftResponse draft = (await registry.CreateAuthorDraftAsync("client", new(asset.AssetId, "scale", "async", Metadata(registry, "create")))).Response!;
            BackendOperationResult<JobResponse> accepted = await registry.StartAuthorCompilationAsync("client", draft.DraftId,
                new(draft.DraftRevision, Metadata(registry, "compile"), "async"));
            Assert.True(accepted.Accepted, accepted.Error?.Message);
            Assert.Equal("author-compilation", accepted.Response!.Kind);
            await WaitForCompilationAsync(registry, accepted.Response.JobId);
            BackendOperationResult<AuthorCompilationResponse> result = registry.ReadAuthorCompilationResult("client", accepted.Response.JobId);
            Assert.True(result.Accepted, result.Error?.Message);
            Assert.Equal(draft.CurrentSourceRevision, result.Response!.SourceRevision);
            Assert.Equal(draft.DraftRevision, result.Response.DraftRevision);
        }
        finally { await registry.DisposeAsync(); }
    }

    [Theory]
    [InlineData("delete")]
    [InlineData("source")]
    [InlineData("cancel")]
    public async Task 作者排队编译固定输入且拒绝覆盖后续草稿或发布取消结果(string action)
    {
        using TemporaryDirectory directory = new();
        string archive = directory.FilePath("demo.mkfx");
        await EffectPackageTransfer.WriteArchiveAsync(archive, EffectPackageFixture.Contents());
        ResourceStore resources = new(new() { ResourceRoot = directory.FilePath("resources") });
        await using FileStream stream = File.OpenRead(archive);
        StoredAsset asset = await resources.UploadAsync("client", "demo.mkfx", "application/octet-stream", stream);
        await using ProjectSessionRegistry registry = NewRegistry("host-queued", resources, directory.FilePath("state"),
            Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"));
        BackendTaskScheduler scheduler = new(new(MaxRunning: 1));
        typeof(ProjectSessionRegistry).GetField("_taskScheduler", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(registry, scheduler);
        using BackendTaskScheduler.Reservation blocker = scheduler.TryReserve("blocker", "blocker")!;
        AuthorDraftResponse draft = (await registry.CreateAuthorDraftAsync("client", new(asset.AssetId, "scale", "queued", Metadata(registry, "create")))).Response!;
        JobResponse job = (await registry.StartAuthorCompilationAsync("client", draft.DraftId,
            new(draft.DraftRevision, Metadata(registry, "queued-compile"), "async"))).Response!;
        Assert.Equal("queued", registry.ReadJob("client", job.JobId).Response!.State);
        AuthorSourceRevisionResponse? changed = null;
        if (action == "delete")
            Assert.True((await registry.DeleteAuthorDraftAsync("client", draft.DraftId, new(draft.DraftRevision, Metadata(registry, "delete")))).Accepted);
        else if (action == "source")
            changed = (await registry.SaveAuthorSourceRevisionAsync("client", draft.DraftId,
                new(draft.DraftRevision, [new("src/PackEffect.cs", "public class Changed { }")], Metadata(registry, "source")))).Response!;
        else
            Assert.True(registry.CancelJob("client", job.JobId).Accepted);
        blocker.Dispose();
        if (action == "cancel")
            await AssertEventuallyAsync(() => registry.ReadJob("client", job.JobId).Response?.State is "succeeded" or "failed" or "cancelled");
        else
            await WaitForCompilationAsync(registry, job.JobId, scheduler);
        JobResponse terminal = registry.ReadJob("client", job.JobId).Response!;
        if (action == "cancel")
        {
            Assert.Equal("cancelled", terminal.State);
            Assert.Null(terminal.AuthorCompilationResult);
            Assert.False(Directory.EnumerateFiles(directory.FilePath("state/author-compilation-results"), "compile-*.json").Any());
        }
        else
        {
            Assert.Equal("succeeded", terminal.State);
            AuthorCompilationResponse result = registry.ReadAuthorCompilationResult("client", job.JobId).Response!;
            Assert.Equal(draft.CurrentSourceRevision, result.SourceRevision);
            Assert.True(result.Succeeded);
            if (action == "delete") Assert.Equal(BackendErrorCode.ResourceNotFound, registry.ReadAuthorDraft("client", draft.DraftId).Error!.Code);
            else
            {
                AuthorDraftResponse current = registry.ReadAuthorDraft("client", draft.DraftId).Response!;
                Assert.Equal(changed!.DraftRevision, current.DraftRevision);
                Assert.Equal(changed.SourceRevision, current.CurrentSourceRevision);
                Assert.Null(current.LastCompilation);
            }
        }
        await AssertEventuallyAsync(() => scheduler.Running == 0 && scheduler.Queued == 0);
    }

    private static async Task AssertEventuallyAsync(Func<bool> predicate)
    {
        for (int i = 0; i < 100; i++)
        {
            if (predicate()) return;
            await Task.Delay(20);
        }
        Assert.True(predicate(), "异步任务未在预期时间内进入终态。");
    }

    private async Task WaitForCompilationAsync(ProjectSessionRegistry registry, string jobId, BackendTaskScheduler? scheduler = null)
    {
        // A real Roslyn compilation can wait behind the process-wide compilation gate.
        // Keep this budget separate from the short cancellation and resource-drain checks.
        Stopwatch elapsed = Stopwatch.StartNew();
        bool reportedOriginalBudget = false;
        while (true)
        {
            JobResponse? current = registry.ReadJob("client", jobId).Response;
            string diagnostic = $"state={current?.State ?? "missing"}, schedulerRunning={scheduler?.Running}, schedulerQueued={scheduler?.Queued}";
            if (current?.State is "succeeded" or "failed" or "cancelled")
            {
                output.WriteLine($"Real author compilation finished after {elapsed.Elapsed.TotalMilliseconds:F0} ms; {diagnostic}.");
                return;
            }
            if (!reportedOriginalBudget && elapsed.Elapsed >= TimeSpan.FromSeconds(2))
            {
                output.WriteLine($"Real author compilation exceeded the old 2 s polling budget; {diagnostic}.");
                reportedOriginalBudget = true;
            }
            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(8),
                $"Real author compilation did not finish within 8 s ({elapsed.Elapsed}); {diagnostic}.");
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task 作者草稿完整编译诊断可读取并可显式删除释放保护()
    {
        using TemporaryDirectory directory = new();
        string state = directory.FilePath("state");
        string archive = directory.FilePath("demo.mkfx");
        await EffectPackageTransfer.WriteArchiveAsync(archive, EffectPackageFixture.Contents());

        ResourceStore resources = new(new() { ResourceRoot = directory.FilePath("resources") });
        StoredAsset asset = await resources.UploadAsync("client", "demo.mkfx", "application/octet-stream", new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.Read));
        string references = Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0");
        ProjectSessionRegistry registry = NewRegistry("host-one", resources, state, references);
        try
        {
            RequestMetadataDto createMetadata = Metadata(registry, "create");
            BackendOperationResult<AuthorDraftResponse> created = await registry.CreateAuthorDraftAsync("client",
                new(asset.AssetId, "scale", "author", createMetadata));
            Assert.True(created.Accepted, created.Error?.Message);
            AuthorDraftResponse draft = created.Response!;
            Assert.True(registry.HasAuthorDraft("client"));
            Assert.Equal(BackendErrorCode.ResourceUnauthorized, registry.ReadAuthorDraft("other", draft.DraftId).Error!.Code);

            BackendOperationResult<AuthorCompilationResponse> compiled = await registry.CompileAuthorDraftAsync("client", draft.DraftId,
                new(draft.DraftRevision, Metadata(registry, "compile")));
            Assert.True(compiled.Accepted, compiled.Error?.Message);
            Assert.True(compiled.Response!.Succeeded, string.Join('\n', compiled.Response.Diagnostics.Select(d => d.Message)));
            Assert.Equal(compiled.Response.Diagnostics, registry.ReadAuthorCompilation("client", draft.DraftId).Response!.Diagnostics);

            AuthorSourceFileDto broken = new("src/PackEffect.cs", "public class Broken { invalid !!! }");
            BackendOperationResult<AuthorSourceRevisionResponse> revision = await registry.SaveAuthorSourceRevisionAsync("client", draft.DraftId,
                new(draft.DraftRevision, [broken], Metadata(registry, "source")));
            Assert.True(revision.Accepted, revision.Error?.Message);
            BackendOperationResult<AuthorCompilationResponse> failed = await registry.CompileAuthorDraftAsync("client", draft.DraftId,
                new(revision.Response!.DraftRevision, Metadata(registry, "compile-failed")));
            Assert.True(failed.Accepted, failed.Error?.Message);
            Assert.False(failed.Response!.Succeeded);
            Assert.Contains(failed.Response.Diagnostics, diagnostic => diagnostic.IsError && diagnostic.Path == "src/PackEffect.cs");

            AuthorCompilationResponse persisted = registry.ReadAuthorCompilation("client", draft.DraftId).Response!;
            Assert.False(persisted.Succeeded);
            Assert.Contains(persisted.Diagnostics, diagnostic => diagnostic.Path == "src/PackEffect.cs");

            AuthorDraftResponse reopened = registry.ReadAuthorDraft("client", draft.DraftId).Response!;
            BackendOperationResult<AuthorAdoptSourceRevisionResponse> adopted = await registry.AdoptAuthorSourceRevisionAsync("client", draft.DraftId,
                new(reopened.DraftRevision, draft.CurrentSourceRevision, Metadata(registry, "adopt")));
            Assert.True(adopted.Accepted, adopted.Error?.Message);
            BackendOperationResult<AuthorDraftDeleteResponse> deleted = await registry.DeleteAuthorDraftAsync("client", draft.DraftId,
                new(adopted.Response!.DraftRevision, Metadata(registry, "delete")));
            Assert.True(deleted.Accepted, deleted.Error?.Message);
            Assert.False(registry.HasAuthorDraft("client"));
            Assert.Equal(BackendErrorCode.ResourceNotFound, registry.ReadAuthorDraft("client", draft.DraftId).Error!.Code);
        }
        finally
        {
            await registry.DisposeAsync();
        }
    }

    [Fact]
    public async Task 作者草稿回执与修订门禁拒绝跨草稿requestId和CAS冲突且删除重试幂等()
    {
        using TemporaryDirectory directory = new();
        string archive = directory.FilePath("demo.mkfx");
        await EffectPackageTransfer.WriteArchiveAsync(archive, EffectPackageFixture.Contents());
        ResourceStore resources = new(new() { ResourceRoot = directory.FilePath("resources") });
        await using FileStream stream = File.OpenRead(archive);
        StoredAsset asset = await resources.UploadAsync("client", "demo.mkfx", "application/octet-stream", stream);
        ProjectSessionRegistry registry = NewRegistry("host", resources, directory.FilePath("state"), Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"));
        try
        {
            RequestMetadataDto createMetadata = Metadata(registry, "same-create");
            AuthorDraftResponse first = (await registry.CreateAuthorDraftAsync("client", new(asset.AssetId, "scale", "one", createMetadata))).Response!;
            BackendOperationResult<AuthorDraftResponse> reuse = await registry.CreateAuthorDraftAsync("client", new(asset.AssetId, "scale", "two", createMetadata));
            Assert.Equal(BackendErrorCode.RequestIdReuse, reuse.Error!.Code);
            RequestMetadataDto sourceMetadata = Metadata(registry, "source");
            AuthorSourceRevisionRequest cas = new(first.DraftRevision, [new("src/PackEffect.cs", "public class Broken { invalid !!! }")], sourceMetadata);
            AuthorSourceRevisionResponse changed = (await registry.SaveAuthorSourceRevisionAsync("client", first.DraftId, cas)).Response!;
            AuthorDraftResponse after = registry.ReadAuthorDraft("client", first.DraftId).Response!;
            BackendOperationResult<AuthorCompilationResponse> foreignCompile = await registry.CompileAuthorDraftAsync("other", first.DraftId, new(after.DraftRevision, Metadata(registry, "foreign-compile")));
            Assert.Equal(BackendErrorCode.ResourceUnauthorized, foreignCompile.Error!.Code);
            BackendOperationResult<AuthorDraftDeleteResponse> foreignDelete = await registry.DeleteAuthorDraftAsync("other", first.DraftId, new(after.DraftRevision, Metadata(registry, "foreign-delete")));
            Assert.Equal(BackendErrorCode.ResourceUnauthorized, foreignDelete.Error!.Code);
            BackendOperationResult<AuthorSourceRevisionResponse> conflict = await registry.SaveAuthorSourceRevisionAsync("client", first.DraftId,
                new(first.DraftRevision, cas.Files, Metadata(registry, "stale")));
            Assert.Equal(BackendErrorCode.RevisionConflict, conflict.Error!.Code);
            Assert.Equal(after.DraftRevision, registry.ReadAuthorDraft("client", first.DraftId).Response!.DraftRevision);
            RequestMetadataDto deleteMetadata = Metadata(registry, "delete");
            AuthorDraftDeleteResponse deleted = (await registry.DeleteAuthorDraftAsync("client", first.DraftId, new(after.DraftRevision, deleteMetadata))).Response!;
            AuthorDraftDeleteResponse retried = (await registry.DeleteAuthorDraftAsync("client", first.DraftId, new(after.DraftRevision, deleteMetadata))).Response!;
            Assert.Equal(deleted, retried);
            Assert.Equal(changed.SourceRevision, after.CurrentSourceRevision);
        }
        finally { await registry.DisposeAsync(); }
    }

    [Fact]
    public async Task 作者草稿严格拒绝路径越界坏修订与跨草稿requestId复用()
    {
        using TemporaryDirectory directory = new();
        ResourceStore resources = new(new() { ResourceRoot = directory.FilePath("resources") });
        ProjectSessionRegistry registry = NewRegistry("host", resources, directory.FilePath("state"), null);
        try
        {
            Assert.Equal(BackendErrorCode.InvalidRequest, registry.ReadAuthorDraft("client", "../draft").Error!.Code);
            RequestMetadataDto metadata = Metadata(registry, "bad");
            BackendOperationResult<AuthorAdoptSourceRevisionResponse> bad = await registry.AdoptAuthorSourceRevisionAsync("client", "draft-00000000000000000000000000000000",
                new("sha256:" + new string('0', 64), "sha256:../" + new string('0', 57), metadata));
            Assert.Equal(BackendErrorCode.ResourceNotFound, bad.Error!.Code);

            // A different operation with fresh metadata still reports the missing draft.
            Assert.Equal(BackendErrorCode.ResourceNotFound, (await registry.SaveAuthorSourceRevisionAsync("client", "draft-00000000000000000000000000000000",
                new("sha256:" + new string('0', 64), [], Metadata(registry, "missing-source")))).Error!.Code);
        }
        finally
        {
            await registry.DisposeAsync();
        }
    }

    [Fact]
    public async Task 作者工作流停止门禁稳定返回SessionClosing()
    {
        using TemporaryDirectory directory = new();
        ResourceStore resources = new(new() { ResourceRoot = directory.FilePath("resources") });
        ProjectSessionRegistry registry = NewRegistry("host", resources, directory.FilePath("state"), null);
        try
        {
            registry.StopAuthorWorkflows();
            RequestMetadataDto metadata = Metadata(registry, "stopped");
            BackendOperationResult<AuthorDraftResponse> result = await registry.CreateAuthorDraftAsync("client", new("asset", "scale", "stopped", metadata));
            Assert.Equal(BackendErrorCode.SessionClosing, result.Error!.Code);
            BackendOperationResult<AuthorDraftResponse> second = await registry.CreateAuthorDraftAsync("client", new("asset", "scale", "stopped", Metadata(registry, "stopped-2")));
            Assert.Equal(BackendErrorCode.SessionClosing, second.Error!.Code);
        }
        finally { await registry.DisposeAsync(); }
    }

    [Fact]
    public async Task 作者编译完整响应预算越界拒绝并保留草稿状态()
    {
        using TemporaryDirectory directory = new();
        string archive = directory.FilePath("demo.mkfx");
        await EffectPackageTransfer.WriteArchiveAsync(archive, EffectPackageFixture.Contents());
        ResourceStore resources = new(new() { ResourceRoot = directory.FilePath("resources") });
        await using FileStream stream = File.OpenRead(archive);
        StoredAsset asset = await resources.UploadAsync("client", "demo.mkfx", "application/octet-stream", stream);
        ProjectSessionRegistry registry = NewRegistry("host", resources, directory.FilePath("state"), Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"));
        try
        {
            AuthorDraftResponse draft = (await registry.CreateAuthorDraftAsync("client", new(asset.AssetId, "scale", "budget", Metadata(registry, "create")))).Response!;
            string source = "public class Broken { public void M() {\n" + string.Join('\n', Enumerable.Range(0, 400).Select(i => $"unknown_symbol_{i};")) + "\n} }";
            AuthorSourceRevisionResponse revision = (await registry.SaveAuthorSourceRevisionAsync("client", draft.DraftId,
                new(draft.DraftRevision, [new("src/PackEffect.cs", source)], Metadata(registry, "source")))).Response!;
            AuthorDraftResponse before = registry.ReadAuthorDraft("client", draft.DraftId).Response!;
            BackendOperationResult<AuthorCompilationResponse> result = await registry.CompileAuthorDraftAsync("client", draft.DraftId,
                new(revision.DraftRevision, Metadata(registry, "compile")));
            Assert.Equal(BackendErrorCode.ResourceTooLarge, result.Error!.Code);
            Assert.Equal(before.DraftRevision, registry.ReadAuthorDraft("client", draft.DraftId).Response!.DraftRevision);
            Assert.Null(registry.ReadAuthorCompilation("client", draft.DraftId).Response);
        }
        finally { await registry.DisposeAsync(); }
    }

    [Fact]
    public async Task 作者草稿列表源码读取与配置CAS可恢复且不写个人库()
    {
        using TemporaryDirectory directory = new();
        string archive = directory.FilePath("demo.mkfx");
        await EffectPackageTransfer.WriteArchiveAsync(archive, EffectPackageFixture.Contents());
        ResourceStore resources = new(new() { ResourceRoot = directory.FilePath("resources") });
        await using FileStream stream = File.OpenRead(archive);
        StoredAsset asset = await resources.UploadAsync("client", "demo.mkfx", "application/octet-stream", stream);
        string state = directory.FilePath("state");
        ProjectSessionRegistry registry = NewRegistry("host", resources, state, null);
        try
        {
            AuthorDraftResponse created = (await registry.CreateAuthorDraftAsync("client",
                new(asset.AssetId, "scale", "author", Metadata(registry, "create")))).Response!;
            AuthorDraftListResponse listed = registry.ReadAuthorDrafts("client").Response!;
            AuthorDraftResponse listedDraft = Assert.Single(listed.Drafts);
            Assert.Equal(created.DraftId, listedDraft.DraftId);
            Assert.NotNull(listedDraft.Configuration);
            Assert.NotNull(listedDraft.SampleOptions);
            Assert.Contains(listedDraft.SampleOptions!.Samples, sample => sample.SampleId == "workshop-single-line-v1");
            Assert.Contains("tokens", listedDraft.SampleOptions.Samples.SelectMany(sample => sample.Targets).Select(target => target.Binding.Kind));

            AuthorDraftSourcesResponse sources = registry.ReadAuthorSources("client", created.DraftId, created.DraftRevision,
                created.CurrentSourceRevision).Response!;
            Assert.Equal(created.CurrentSourceRevision, sources.SourceRevision);
            Assert.Equal("src/PackEffect.cs", Assert.Single(sources.Files).Path);
            Assert.Contains("PackEffect", sources.Files[0].Text, StringComparison.Ordinal);

            AuthorDraftConfigurationInputDto input = new("configured", created.PackageId, created.CurrentSourceRevision, "scale",
                "{\"peak\":2.25}", TemplateEffectTargetDto.Application, TemplateScriptReadScopeDto.Project, null,
                ulong.MaxValue.ToString(CultureInfo.InvariantCulture), TemplateEffectOverlayLayerDto.Package);
            AuthorDraftResponse updated = (await registry.SaveAuthorConfigurationAsync("client", created.DraftId,
                new(created.DraftRevision, input, Metadata(registry, "configuration")))).Response!;
            Assert.NotEqual(created.DraftRevision, updated.DraftRevision);
            Assert.Equal("configured", updated.Configuration!.Name);
            Assert.Equal("{\"peak\":2.25}", updated.Configuration.ParametersJson);
            Assert.Equal(ulong.MaxValue.ToString(CultureInfo.InvariantCulture), updated.Configuration.FixedSeed);
            Assert.Contains("tokens", updated.Configuration.BindingKinds);
            Assert.Contains("tokens", updated.SampleOptions!.Samples.SelectMany(sample => sample.Targets).Select(target => target.Binding.Kind));

            Assert.Equal(BackendErrorCode.RevisionConflict, (await registry.SaveAuthorConfigurationAsync("client", created.DraftId,
                new(created.DraftRevision, input, Metadata(registry, "configuration-stale")))).Error!.Code);
            Assert.Equal(BackendErrorCode.RevisionConflict, registry.ReadAuthorSources("client", created.DraftId,
                created.DraftRevision, created.CurrentSourceRevision).Error!.Code);
            Assert.Equal(BackendErrorCode.ResourceUnauthorized, registry.ReadAuthorSources("other", created.DraftId,
                updated.DraftRevision, created.CurrentSourceRevision).Error!.Code);

            await registry.DisposeAsync();
            registry = NewRegistry("host-reopen", resources, state, null);
            AuthorDraftResponse reopened = registry.ReadAuthorDraft("client", created.DraftId).Response!;
            Assert.Equal(updated.DraftRevision, reopened.DraftRevision);
            Assert.Equal(updated.Configuration.Name, reopened.Configuration!.Name);
            Assert.Equal(updated.Configuration.ParametersJson, reopened.Configuration.ParametersJson);
            Assert.Equal(updated.Configuration.FixedSeed, reopened.Configuration.FixedSeed);
            Assert.Equal(updated.Configuration.BindingKinds, reopened.Configuration.BindingKinds);
            Assert.Contains("tokens", reopened.SampleOptions!.Samples.SelectMany(sample => sample.Targets).Select(target => target.Binding.Kind));
        }
        finally
        {
            await registry.DisposeAsync();
        }
    }

    private static ProjectSessionRegistry NewRegistry(string host, ResourceStore resources, string state, string? references)
    {
        ProjectSessionRegistry registry = new(host, resources, NewProjectPreparationService.CreateDefault());
        registry.ConfigurePersistence(new() { StateRoot = state });
        registry.ConfigureAuthorWorkflows(references);
        return registry;
    }

    private static RequestMetadataDto Metadata(ProjectSessionRegistry registry, string suffix)
        => new("author-" + suffix + "-" + Guid.NewGuid().ToString("N"), registry.HostInstanceId, registry.ServerTimeUtc.AddMinutes(1));
}
