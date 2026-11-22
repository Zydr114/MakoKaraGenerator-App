using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Hosting;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

public sealed partial class BackendAuthorV2WorkflowTests
{
    private static readonly JsonSerializerOptions InspectionTestJson = new(JsonSerializerDefaults.Web);
    private static readonly string[] InspectionResponseFields = ["bindingKinds", "diagnostics", "draftId", "draftRevision", "exports", "files", "inputs", "name", "packageId", "packageRevision", "packageVersion", "parameters", "protocolVersion", "sdkVersion", "shaderProfile", "sourceRevision", "structureValid", "version"];

    [Fact]
    public async Task 固定源码检查读取在用最新与历史并保留Int64原文且不创建资产或写盘()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        AuthorDraftResponse original = await fixture.ReadAsync();
        AuthorDraftResponse latest = await SaveMigratedSchemaAsync(fixture);
        Assert.Equal(original.Configuration!.SourceRevision, latest.Configuration!.SourceRevision);
        Assert.Null(latest.LastCompilation);
        DraftDiskSnapshot before = await SnapshotDraftAsync(fixture);
        string[] assetsBefore = AllResourceFiles(fixture);
        AuthorSourceInspectionResponse old = (await InspectAsync(fixture, original.CurrentSourceRevision)).Response!;
        AuthorSourceInspectionResponse target = (await InspectAsync(fixture, latest.CurrentSourceRevision)).Response!;
        Assert.True(old.StructureValid); Assert.True(target.StructureValid);
        Assert.Equal("scale", Assert.Single(old.Exports).Id);
        Assert.Equal("peak", Assert.Single(old.Parameters).Id);
        Assert.Equal("migrated", Assert.Single(target.Exports).Id);
        var parameter = Assert.Single(target.Parameters);
        Assert.Equal("counter", parameter.Id);
        Assert.Equal("9007199254740993", parameter.DefaultValueJson);
        Assert.Equal("-9223372036854775808", parameter.MinimumJson);
        Assert.Equal("9223372036854775807", parameter.MaximumJson);
        Assert.Equal(latest.DraftId, target.DraftId);
        Assert.Equal(latest.DraftRevision, target.DraftRevision);
        Assert.Equal(latest.CurrentSourceRevision, target.SourceRevision);
        Assert.Equal(target.SourceRevision, target.PackageRevision);
        Assert.Equal(latest.PackageId, target.PackageId);
        AuthorPackageFilesResponse files = (await fixture.Registry.ReadAuthorPackageFilesAsync("client", latest.DraftId,
            latest.DraftRevision, latest.CurrentSourceRevision)).Response!;
        Assert.Equal(files.Files.Select(file => (file.Path, file.Role, file.SizeBytes, file.Sha256)),
            target.Files.Select(file => (file.Path, file.Role, file.Size, file.Sha256)));
        string responseJson = JsonSerializer.Serialize(target, InspectionTestJson);
        using JsonDocument json = JsonDocument.Parse(responseJson);
        Assert.Equal(InspectionResponseFields,
            json.RootElement.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.DoesNotContain("IPureKaraokeEffect", responseJson, StringComparison.Ordinal);
        Assert.DoesNotContain(DraftDirectory(fixture), responseJson, StringComparison.Ordinal);
        Assert.Equal(assetsBefore, AllResourceFiles(fixture));
        await AssertSameDraftAsync(before, fixture);
    }

    [Fact]
    public async Task 固定源码检查拒绝他人旧CAS未知和其他草稿来源且取消后锁与生命周期可继续使用()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await using Fixture foreign = await Fixture.CreateAsync(PureSource + "\n// foreign retained only");
        AuthorDraftResponse initial = await fixture.ReadAsync();
        await fixture.SaveSourceAsync(PureSource + "\n// own latest");
        AuthorDraftResponse current = await fixture.ReadAsync();
        var unauthorized = await fixture.Registry.InspectAuthorSourceAsync("other", current.DraftId, current.DraftRevision, current.CurrentSourceRevision);
        Assert.Equal(BackendErrorCode.ResourceUnauthorized, unauthorized.Error!.Code);
        var stale = await fixture.Registry.InspectAuthorSourceAsync("client", current.DraftId, initial.DraftRevision, initial.CurrentSourceRevision);
        Assert.Equal(BackendErrorCode.RevisionConflict, stale.Error!.Code);
        foreach (string source in new[] { "sha256:" + new string('e', 64), (await foreign.ReadAsync()).CurrentSourceRevision })
        {
            var missing = await InspectAsync(fixture, source);
            Assert.Equal(BackendErrorCode.ResourceNotFound, missing.Error!.Code);
            Assert.Equal("sourceRevision", missing.Error.Field);
        }
        var invalid = await fixture.Registry.InspectAuthorSourceAsync("client", current.DraftId, "latest", current.CurrentSourceRevision);
        Assert.Equal(BackendErrorCode.InvalidRequest, invalid.Error!.Code);
        SemaphoreSlim gate = AuthorGates(fixture).GetOrAdd(current.DraftId, _ => new(1, 1));
        await gate.WaitAsync();
        try
        {
            using CancellationTokenSource cancellation = new();
            Task<BackendOperationResult<AuthorSourceInspectionResponse>> pending = fixture.Registry.InspectAuthorSourceAsync("client", current.DraftId,
                current.DraftRevision, current.CurrentSourceRevision, cancellation.Token);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);
            Assert.Equal(0, PrivateField<int>(fixture.Registry, "_templateOperations"));
        }
        finally { gate.Release(); }
        Assert.True((await InspectAsync(fixture, initial.CurrentSourceRevision)).Accepted);
        await using Fixture stopping = await Fixture.CreateAsync();
        AuthorDraftResponse stoppingDraft = await stopping.ReadAsync();
        SemaphoreSlim stoppingGate = AuthorGates(stopping).GetOrAdd(stoppingDraft.DraftId, _ => new(1, 1));
        await stoppingGate.WaitAsync();
        try
        {
            Task<BackendOperationResult<AuthorSourceInspectionResponse>> pending = stopping.Registry.InspectAuthorSourceAsync(
                "client", stoppingDraft.DraftId, stoppingDraft.DraftRevision, stoppingDraft.CurrentSourceRevision);
            PrivateField<CancellationTokenSource>(stopping.Registry, "_authorStop").Cancel();
            var stopped = await pending;
            Assert.Equal(BackendErrorCode.SessionClosing, stopped.Error!.Code);
            Assert.Equal(0, PrivateField<int>(stopping.Registry, "_templateOperations"));
        }
        finally { stoppingGate.Release(); }
    }

    [Fact]
    public async Task 固定源码检查在取得草稿锁后重新核对owner且超8MiB响应整次拒绝()
    {
        await using (Fixture fixture = await Fixture.CreateAsync())
        {
            AuthorDraftResponse draft = await fixture.ReadAsync();
            SemaphoreSlim gate = AuthorGates(fixture).GetOrAdd(draft.DraftId, _ => new(1, 1));
            await gate.WaitAsync();
            Task<BackendOperationResult<AuthorSourceInspectionResponse>> pending;
            try
            {
                pending = fixture.Registry.InspectAuthorSourceAsync("client", draft.DraftId, draft.DraftRevision, draft.CurrentSourceRevision);
                string path = Path.Combine(DraftDirectory(fixture), "draft.json");
                JsonObject document = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
                document["ownerClientId"] = "new-owner";
                File.WriteAllText(path, document.ToJsonString());
            }
            finally { gate.Release(); }
            Assert.Equal(BackendErrorCode.ResourceUnauthorized, (await pending).Error!.Code);
        }
        await using Fixture oversized = await Fixture.CreateAsync(configureManifest: manifest =>
        {
            manifest["parameters"] = new JsonArray(new JsonObject { ["id"] = "huge", ["type"] = "text", ["label"] = "Huge", ["default"] = new string('q', 8 * 1024 * 1024) });
            manifest["exports"]![0]!["parameters"] = new JsonArray("huge");
        });
        DraftDiskSnapshot before = await SnapshotDraftAsync(oversized);
        var result = await InspectAsync(oversized, (await oversized.ReadAsync()).CurrentSourceRevision);
        Assert.False(result.Accepted); Assert.Null(result.Response);
        Assert.Equal(BackendErrorCode.ResourceTooLarge, result.Error!.Code);
        Assert.Equal("AuthorSourceInspectionTooLarge", result.Error.FailureCode);
        await AssertSameDraftAsync(before, oversized);
    }

    [Fact]
    public async Task 完整配置原子采用保留历史B而最新C不动并支持带完整快照撤销重做及回执重放()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        AuthorDraftResponse a = await fixture.ReadAsync();
        Assert.True((await fixture.CompileAsync(a.CurrentSourceRevision)).Succeeded);
        AuthorDraftResponse b = await SaveMigratedSchemaAsync(fixture);
        Assert.True((await fixture.CompileAsync(b.CurrentSourceRevision)).Succeeded);
        await fixture.SaveSourceAsync(PureSource + "\n// C saved after B compiled");
        AuthorDraftResponse c = await fixture.ReadAsync();
        DraftDiskSnapshot before = await SnapshotDraftAsync(fixture);
        var legacy = await fixture.AdoptAsync(b.CurrentSourceRevision);
        Assert.False(legacy.Accepted);
        Assert.Equal("PackageExportMissing", legacy.Error!.FailureCode);
        await AssertSameDraftAsync(before, fixture);
        AuthorPackageConfigurationInputDto target = MigratedConfiguration();
        AuthorAdoptSourceRevisionRequest request = new(c.DraftRevision, b.CurrentSourceRevision, fixture.Metadata(), Configuration: target);
        var adopted = await fixture.Registry.AdoptAuthorSourceRevisionAsync("client", c.DraftId, request);
        Assert.True(adopted.Accepted, adopted.Error?.Message);
        AuthorDraftResponse after = await fixture.ReadAsync();
        Assert.Equal(c.CurrentSourceRevision, after.CurrentSourceRevision);
        Assert.Equal(b.CurrentSourceRevision, after.AdoptedSourceRevision);
        Assert.Equal(b.CurrentSourceRevision, after.Configuration!.SourceRevision);
        Assert.Equal(target.Name, after.Name); Assert.Equal(target.Name, after.Configuration.Name);
        Assert.Equal(target.ExportId, after.ExportId); Assert.Equal(target.ExportId, after.Configuration.ExportId);
        Assert.Equal(target.FixedSeed, after.Configuration.FixedSeed);
        Assert.Equal(target.ReadScope, after.Configuration.ReadScope);
        Assert.Equal(target.RelativeTarget, after.Configuration.RelativeTarget);
        Assert.Equal(target.ParametersJson, after.Configuration.ParametersJson);
        Assert.Equal(JsonSerializer.Serialize(c.LastCompilation), JsonSerializer.Serialize(after.LastCompilation));
        Assert.NotEmpty(after.SampleOptions!.Samples);
        var noOp = await fixture.Registry.AdoptAuthorSourceRevisionAsync("client", c.DraftId,
            new(after.DraftRevision, b.CurrentSourceRevision, fixture.Metadata(), Configuration: target));
        Assert.True(noOp.Accepted, noOp.Error?.Message);
        Assert.Equal(after.DraftRevision, noOp.Response!.DraftRevision);
        var undo = await fixture.Registry.AdoptAuthorSourceRevisionAsync("client", c.DraftId,
            new(after.DraftRevision, a.CurrentSourceRevision, fixture.Metadata(), Configuration: PackageConfiguration(a.Configuration!)));
        Assert.True(undo.Accepted, undo.Error?.Message);
        AuthorDraftResponse undone = await fixture.ReadAsync();
        Assert.Equal(JsonSerializer.Serialize(a.Configuration), JsonSerializer.Serialize(undone.Configuration));
        Assert.Equal(a.CurrentSourceRevision, undone.AdoptedSourceRevision);
        Assert.Equal(c.CurrentSourceRevision, undone.CurrentSourceRevision);
        DraftDiskSnapshot afterUndo = await SnapshotDraftAsync(fixture);
        var replay = await fixture.Registry.AdoptAuthorSourceRevisionAsync("client", c.DraftId, request);
        Assert.Equal(adopted.Response, replay.Response);
        await AssertSameDraftAsync(afterUndo, fixture);
        var reuse = await fixture.Registry.AdoptAuthorSourceRevisionAsync("client", c.DraftId,
            request with { Configuration = target with { Name = "changed semantic payload" } });
        Assert.Equal(BackendErrorCode.RequestIdReuse, reuse.Error!.Code);
        await AssertSameDraftAsync(afterUndo, fixture);
        var redo = await fixture.Registry.AdoptAuthorSourceRevisionAsync("client", c.DraftId,
            new(undone.DraftRevision, b.CurrentSourceRevision, fixture.Metadata(), Configuration: target));
        Assert.True(redo.Accepted, redo.Error?.Message);
        Assert.Equal(JsonSerializer.Serialize(after.Configuration), JsonSerializer.Serialize((await fixture.ReadAsync()).Configuration));
        AuthorSourceRevisionsResponse history = (await fixture.Registry.ReadAuthorSourceRevisionsAsync("client", c.DraftId, (await fixture.ReadAsync()).DraftRevision)).Response!;
        Assert.True(history.Revisions.Single(item => item.SourceRevision == a.CurrentSourceRevision).Compilation!.Succeeded);
        Assert.True(history.Revisions.Single(item => item.SourceRevision == b.CurrentSourceRevision).Compilation!.Succeeded);
        Assert.Null(history.Revisions.Single(item => item.SourceRevision == c.CurrentSourceRevision).Compilation);
    }

    [Fact]
    public async Task 原子采用所有无效完整配置在写盘前拒绝并保留在用配置采用标记历史及试样()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        AuthorDraftResponse b = await SaveMigratedSchemaAsync(fixture);
        Assert.True((await fixture.CompileAsync(b.CurrentSourceRevision)).Succeeded);
        AuthorPackageConfigurationInputDto valid = MigratedConfiguration();
        (AuthorPackageConfigurationInputDto Input, string Field)[] invalid =
        [
            (valid with { Name = " " }, "name"),
            (valid with { ExportId = "missing" }, "exportId"),
            (valid with { ParametersJson = "{broken" }, "parametersJson"),
            (valid with { ParametersJson = "[]" }, "parametersJson"),
            (valid with { ParametersJson = "{\"counter\":\"bad\"}" }, "parametersJson"),
            (valid with { ParametersJson = "{\"counter\":9223372036854775808}" }, "parametersJson"),
            (valid with { ParametersJson = "{\"peak\":2}" }, "parametersJson"),
            (valid with { ParametersJson = "{\"counter\":1,\"counter\":2}" }, "parametersJson"),
            (valid with { RelativeTarget = TemplateEffectTargetDto.Line }, "relativeTarget"),
            (valid with { RelativeTarget = (TemplateEffectTargetDto)99 }, "relativeTarget"),
            (valid with { ReadScope = (TemplateScriptReadScopeDto)99 }, "readScope"),
            (valid with { ReadScope = TemplateScriptReadScopeDto.BranchInput, BranchInput = null }, "branchInput"),
            (valid with { BranchInput = "unexpected" }, "branchInput"),
            (valid with { FixedSeed = "18446744073709551616" }, "fixedSeed"),
            (valid with { OverlayLayer = TemplateEffectOverlayLayerDto.AboveText }, "overlayLayer"),
        ];
        DraftDiskSnapshot before = await SnapshotDraftAsync(fixture);
        foreach (var entry in invalid)
        {
            var result = await fixture.Registry.AdoptAuthorSourceRevisionAsync("client", b.DraftId,
                new((await fixture.ReadAsync()).DraftRevision, b.CurrentSourceRevision, fixture.Metadata(), Configuration: entry.Input));
            Assert.False(result.Accepted);
            Assert.Equal("configuration." + entry.Field, result.Error!.Field);
            Assert.False(string.IsNullOrWhiteSpace(result.Error.FailureCode));
            await AssertSameDraftAsync(before, fixture);
        }
        var stale = await fixture.Registry.AdoptAuthorSourceRevisionAsync("client", b.DraftId,
            new("sha256:" + new string('f', 64), b.CurrentSourceRevision, fixture.Metadata(), Configuration: valid));
        Assert.Equal(BackendErrorCode.RevisionConflict, stale.Error!.Code);
        await AssertSameDraftAsync(before, fixture);
        var unauthorized = await fixture.Registry.AdoptAuthorSourceRevisionAsync("other", b.DraftId,
            new((await fixture.ReadAsync()).DraftRevision, b.CurrentSourceRevision, fixture.Metadata(), Configuration: valid));
        Assert.Equal(BackendErrorCode.ResourceUnauthorized, unauthorized.Error!.Code);
        await AssertSameDraftAsync(before, fixture);
    }

    [Fact]
    public async Task 成功编译A不放行未编译B或失败C且配额拒绝不部分切源()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        Assert.True((await fixture.CompileAsync()).Succeeded);
        AuthorDraftResponse b = await SaveMigratedSchemaAsync(fixture);
        DraftDiskSnapshot before = await SnapshotDraftAsync(fixture);
        var uncompiled = await fixture.Registry.AdoptAuthorSourceRevisionAsync("client", b.DraftId,
            new(b.DraftRevision, b.CurrentSourceRevision, fixture.Metadata(), Configuration: MigratedConfiguration()));
        Assert.Equal("AuthorSourceCompilationRequired", uncompiled.Error!.FailureCode);
        await AssertSameDraftAsync(before, fixture);
        await fixture.SaveSourceAsync("this is not CSharp");
        Assert.False((await fixture.CompileAsync()).Succeeded);
        AuthorDraftResponse c = await fixture.ReadAsync();
        before = await SnapshotDraftAsync(fixture);
        var failed = await fixture.Registry.AdoptAuthorSourceRevisionAsync("client", c.DraftId,
            new(c.DraftRevision, c.CurrentSourceRevision, fixture.Metadata(), Configuration: MigratedConfiguration()));
        Assert.Equal("AuthorSourceCompilationRequired", failed.Error!.FailureCode);
        await AssertSameDraftAsync(before, fixture);
        Assert.True((await fixture.CompileAsync(b.CurrentSourceRevision)).Succeeded);
        before = await SnapshotDraftAsync(fixture);
        string quota = Path.Combine(DraftDirectory(fixture), "quota-probe.bin");
        try
        {
            using (FileStream sparse = File.Create(quota)) sparse.SetLength(256L * 1024 * 1024);
            var rejected = await fixture.Registry.AdoptAuthorSourceRevisionAsync("client", c.DraftId,
                new((await fixture.ReadAsync()).DraftRevision, b.CurrentSourceRevision, fixture.Metadata(), Configuration: MigratedConfiguration()));
            Assert.Equal(BackendErrorCode.ResourceQuotaExceeded, rejected.Error!.Code);
            await AssertSameDraftAsync(before, fixture);
        }
        finally { File.Delete(quota); }
    }

    [Fact]
    public async Task 源码历史损坏在配置归一化前拒绝且不误报为参数JSON错误()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        AuthorDraftResponse b = await SaveMigratedSchemaAsync(fixture);
        Assert.True((await fixture.CompileAsync(b.CurrentSourceRevision)).Succeeded);
        File.WriteAllText(Path.Combine(DraftDirectory(fixture), "source-history.json"), "{broken history");
        DraftDiskSnapshot before = await SnapshotDraftAsync(fixture);
        var result = await fixture.Registry.AdoptAuthorSourceRevisionAsync("client", b.DraftId,
            new((await fixture.ReadAsync()).DraftRevision, b.CurrentSourceRevision, fixture.Metadata(), Configuration: MigratedConfiguration()));
        Assert.Equal(BackendErrorCode.ProjectLoadFailed, result.Error!.Code);
        Assert.Equal("sourceRevisions", result.Error.Field);
        Assert.Equal("AuthorSourceHistoryInvalid", result.Error.FailureCode);
        await AssertSameDraftAsync(before, fixture);
    }

    private static async Task<AuthorDraftResponse> SaveMigratedSchemaAsync(Fixture fixture)
    {
        AuthorDraftResponse draft = await fixture.ReadAsync();
        AuthorPackageFilesResponse files = (await fixture.Registry.ReadAuthorPackageFilesAsync("client", draft.DraftId, draft.DraftRevision,
            draft.CurrentSourceRevision)).Response!;
        JsonObject manifest = JsonNode.Parse(files.ManifestJson)!.AsObject();
        manifest["parameters"] = new JsonArray(new JsonObject { ["id"] = "counter", ["type"] = "integer", ["label"] = "Counter",
            ["default"] = 9_007_199_254_740_993L, ["min"] = long.MinValue, ["max"] = long.MaxValue });
        manifest["exports"]![0]!["id"] = "migrated";
        manifest["exports"]![0]!["parameters"] = new JsonArray("counter");
        AuthorPackageTextFileDto[] texts = [.. files.Files.Where(file => file.Editable).Select(file => new AuthorPackageTextFileDto(file.Path, file.Text!))];
        var saved = await fixture.Registry.SaveAuthorPackageFilesAsync("client", draft.DraftId,
            new(draft.DraftRevision, manifest.ToJsonString(), texts, Metadata: fixture.Metadata()));
        Assert.True(saved.Accepted, saved.Error?.Message);
        return saved.Response!;
    }

    private static AuthorPackageConfigurationInputDto MigratedConfiguration() => new("Migrated config", "migrated",
        "{\"counter\":9007199254740993}", TemplateEffectTargetDto.TokenFamily, TemplateScriptReadScopeDto.Project, null,
        "18446744073709551615", TemplateEffectOverlayLayerDto.Package);
    private static AuthorPackageConfigurationInputDto PackageConfiguration(AuthorDraftConfigurationDto value) => new(value.Name,
        value.ExportId, value.ParametersJson, value.RelativeTarget, value.ReadScope, value.BranchInput, value.FixedSeed, value.OverlayLayer);
    private static async Task<BackendOperationResult<AuthorSourceInspectionResponse>> InspectAsync(Fixture fixture, string source)
    {
        AuthorDraftResponse draft = await fixture.ReadAsync();
        return await fixture.Registry.InspectAuthorSourceAsync("client", draft.DraftId, draft.DraftRevision, source);
    }
    private sealed record DraftDiskSnapshot(string ResponseJson, byte[] Draft, byte[] History);
    private static async Task<DraftDiskSnapshot> SnapshotDraftAsync(Fixture fixture) => new(JsonSerializer.Serialize(await fixture.ReadAsync()),
        File.ReadAllBytes(Path.Combine(DraftDirectory(fixture), "draft.json")),
        File.ReadAllBytes(Path.Combine(DraftDirectory(fixture), "source-history.json")));
    private static async Task AssertSameDraftAsync(DraftDiskSnapshot snapshot, Fixture fixture)
    {
        Assert.Equal(snapshot.ResponseJson, JsonSerializer.Serialize(await fixture.ReadAsync()));
        Assert.Equal(snapshot.Draft, File.ReadAllBytes(Path.Combine(DraftDirectory(fixture), "draft.json")));
        Assert.Equal(snapshot.History, File.ReadAllBytes(Path.Combine(DraftDirectory(fixture), "source-history.json")));
    }
    private static string DraftDirectory(Fixture fixture) => Path.Combine(PrivateField<string>(fixture.Registry, "_authorRoot"), fixture.DraftId);
    private static string[] AllResourceFiles(Fixture fixture) => Directory.GetFiles(Path.Combine(Path.GetDirectoryName(
        PrivateField<string>(fixture.Registry, "_authorRoot"))!, "..", "resources"), "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray();
    private static ConcurrentDictionary<string, SemaphoreSlim> AuthorGates(Fixture fixture) => PrivateField<ConcurrentDictionary<string, SemaphoreSlim>>(fixture.Registry, "_authorDraftLocks");
    private static T PrivateField<T>(object value, string field) => (T)value.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(value)!;
}
