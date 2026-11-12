using System.Globalization;
using System.Text.Json;
using MakoKaraGenerator.Application;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.TestSupport;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

public sealed class BackendScriptConfigurationTests
{
    [Fact]
    public async Task 个人配置五命令保留完整配置且CAS回执与严格字段有效()
    {
        await using Fixture f = await Fixture.CreateAsync();
        var draft = f.Draft;
        Assert.Equal("missing", Accepted(f.Registry.ReadScriptConfigurations()).Revision);
        var input = Input(draft);
        var request = f.Request("missing", new ScriptConfigurationCreateCommandDto("create", input));
        var created = Accepted(await f.Registry.ExecuteScriptConfigurationCommandAsync("client", request));
        var read = Accepted(f.Registry.ReadScriptConfiguration(created.ConfigurationId, created.Revision));
        Assert.Equal(TemplateEffectTargetDto.Application, read.Configuration.RelativeTarget);
        Assert.Equal("18446744073709551615", read.Configuration.FixedSeed);
        Assert.Equal("{\"peak\":2.25}", read.Configuration.ParametersJson);
        Assert.Contains(read.Package.Parameters, p => p.Id == "peak");
        Assert.Contains("tokens", read.Configuration.BindingKinds);
        var unchanged = Accepted(await f.Registry.ExecuteScriptConfigurationCommandAsync("client", f.Request(created.Revision,
            new ScriptConfigurationReplaceCommandDto("replace", created.ConfigurationId, input))));
        Assert.False(unchanged.Changed); Assert.Equal(created.Revision, unchanged.Revision);
        Assert.Equal(created, Accepted(await f.Registry.ExecuteScriptConfigurationCommandAsync("client", request)));
        Assert.Equal(BackendErrorCode.RevisionConflict, f.Registry.ReadScriptConfiguration(created.ConfigurationId, "missing").Error!.Code);
        Assert.Equal(BackendErrorCode.RevisionConflict, (await f.Registry.ExecuteScriptConfigurationCommandAsync("client",
            f.Request("missing", new ScriptConfigurationRenameCommandDto("rename", created.ConfigurationId, "stale")))).Error!.Code);
        var replaced = Accepted(await f.Registry.ExecuteScriptConfigurationCommandAsync("client", f.Request(created.Revision,
            new ScriptConfigurationReplaceCommandDto("replace", created.ConfigurationId, input with { RelativeTarget = TemplateEffectTargetDto.TokenFamily }))));
        Assert.Equal(TemplateEffectTargetDto.TokenFamily, Accepted(f.Registry.ReadScriptConfiguration(created.ConfigurationId, replaced.Revision)).Configuration.RelativeTarget);
        var copied = Accepted(await f.Registry.ExecuteScriptConfigurationCommandAsync("client", f.Request(replaced.Revision,
            new ScriptConfigurationCopyCommandDto("copy", created.ConfigurationId, "copy"))));
        var renamed = Accepted(await f.Registry.ExecuteScriptConfigurationCommandAsync("client", f.Request(copied.Revision,
            new ScriptConfigurationRenameCommandDto("rename", copied.ConfigurationId, "renamed"))));
        var deleted = Accepted(await f.Registry.ExecuteScriptConfigurationCommandAsync("client", f.Request(renamed.Revision,
            new ScriptConfigurationDeleteCommandDto("delete", created.ConfigurationId))));
        Assert.Equal("renamed", Assert.Single(Accepted(f.Registry.ReadScriptConfigurations(deleted.Revision)).Configurations).Name);
        Assert.True(File.Exists(ScriptConfigurationLibrary.ResolvePath(f.State)));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ScriptConfigurationCommandDto>("{\"type\":\"delete\",\"configurationId\":\"x\",\"extra\":1}"));
    }

    [Fact]
    public async Task 合法配置可固定失败源码并从库建立草稿及模板自己的闭包()
    {
        await using Fixture f = await Fixture.CreateAsync();
        var saved = Accepted(await f.Registry.SaveAuthorSourceRevisionAsync("client", f.Draft.DraftId,
            new(f.Draft.DraftRevision, [new("src/PackEffect.cs", "public class Broken { invalid !!! }")], f.Metadata())));
        var draft = Accepted(f.Registry.ReadAuthorDraft("client", f.Draft.DraftId));
        Assert.Equal(saved.SourceRevision, draft.CurrentSourceRevision);
        var adopted = Accepted(await f.Registry.AdoptAuthorSourceRevisionAsync("client", draft.DraftId,
            new(draft.DraftRevision, f.Draft.CurrentSourceRevision, f.Metadata())));
        var adoptedDraft = Accepted(f.Registry.ReadAuthorDraft("client", draft.DraftId));
        Assert.Equal(f.Draft.CurrentSourceRevision, adoptedDraft.Configuration!.SourceRevision);
        Assert.Equal(saved.SourceRevision, adoptedDraft.CurrentSourceRevision);
        Accepted(await f.Registry.AdoptAuthorSourceRevisionAsync("client", draft.DraftId,
            new(adopted.DraftRevision, saved.SourceRevision, f.Metadata())));
        draft = Accepted(f.Registry.ReadAuthorDraft("client", draft.DraftId));
        var compiled = Accepted(await f.Registry.CompileAuthorDraftAsync("client", draft.DraftId, new(draft.DraftRevision, f.Metadata())));
        Assert.False(compiled.Succeeded);
        var created = Accepted(await f.Registry.ExecuteScriptConfigurationCommandAsync("client", f.Request("missing", new ScriptConfigurationCreateCommandDto("create", Input(draft)))));
        var detail = Accepted(f.Registry.ReadScriptConfiguration(created.ConfigurationId, created.Revision));
        var restored = Accepted(await f.Registry.CreateAuthorDraftAsync("client", new(null, "scale", "restored", f.Metadata(), created.ConfigurationId, created.Revision)));
        Assert.Equal(saved.SourceRevision, restored.CurrentSourceRevision);
        Assert.Equal(detail.Configuration.ParametersJson, restored.Configuration!.ParametersJson);
        Assert.Equal(detail.Configuration.FixedSeed, restored.Configuration.FixedSeed);
        var style = ProjectSession.ExportTemplateLibraryStyle(LyricStyle.Default);
        var recipe = new TemplateEffectRecipeDto("r", detail.Configuration.Name, detail.Configuration.RelativeTarget,
            detail.Configuration.PackageId, detail.Configuration.PackageRevision, detail.Configuration.ExportId, detail.Configuration.ParametersJson,
            detail.Configuration.OverlayLayer, true, detail.Configuration.ReadScope, detail.Configuration.BranchInput, detail.Configuration.FixedSeed, created.ConfigurationId);
        var template = Accepted(await f.Registry.ExecuteTemplateLibraryCommandAsync("client", "personal", new("missing", f.Metadata(),
            new CreateTemplateCommandDto(new FullTemplateDefinitionDto("fixed", TemplateKind.Composite, style, ["typography.fontSizePx"], [recipe], [detail.Package.Reference]), "full"))));
        Accepted(await f.Registry.ExecuteScriptConfigurationCommandAsync("client", f.Request(created.Revision, new ScriptConfigurationDeleteCommandDto("delete", created.ConfigurationId))));
        var fixedTemplate = Accepted(await f.Registry.ReadTemplateAsync("personal", template.Revision, template.TemplateId, "full"));
        Assert.Equal(saved.SourceRevision, Assert.Single(fixedTemplate.PackageClosureDetails!).PackageRevision);
        Assert.Equal("configuration", detail.Package.Reference.Kind);
        Assert.Equal("library", Assert.Single(((FullTemplateDefinitionDto)fixedTemplate.Definition!).Packages).Kind);
    }

    private static ScriptConfigurationInputDto Input(AuthorDraftResponse draft) => new("saved", "scale", "{\"peak\":2.25}",
        TemplateEffectTargetDto.Application, TemplateScriptReadScopeDto.BoundSources, null, ulong.MaxValue.ToString(CultureInfo.InvariantCulture),
        TemplateEffectOverlayLayerDto.Package, new(DraftId: draft.DraftId, ExpectedDraftRevision: draft.DraftRevision, SourceRevision: draft.CurrentSourceRevision));
    private static T Accepted<T>(BackendOperationResult<T> result) { Assert.True(result.Accepted, result.Error?.ToString()); return result.Response!; }
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly TemporaryDirectory _directory = new();
        public ProjectSessionRegistry Registry { get; private set; } = null!;
        public AuthorDraftResponse Draft { get; private set; } = null!;
        public string State => _directory.FilePath("state");
        public RequestMetadataDto Metadata() => new(Guid.NewGuid().ToString("N"), Registry.HostInstanceId, Registry.ServerTimeUtc.AddMinutes(1));
        public ScriptConfigurationCommandRequest Request(string revision, ScriptConfigurationCommandDto command) => new(revision, Metadata(), command);
        public static async Task<Fixture> CreateAsync()
        {
            var f = new Fixture(); string archive = f._directory.FilePath("demo.mkfx");
            await EffectPackageTransfer.WriteArchiveAsync(archive, EffectPackageFixture.Contents());
            var resources = new ResourceStore(new() { ResourceRoot = f._directory.FilePath("resources") });
            await using var stream = File.OpenRead(archive);
            var asset = await resources.UploadAsync("client", "demo.mkfx", "application/octet-stream", stream);
            f.Registry = new("host", resources, NewProjectPreparationService.CreateDefault());
            f.Registry.ConfigurePersistence(new() { StateRoot = f.State });
            f.Registry.ConfigureAuthorWorkflows(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"));
            f.Registry.ConfigureScriptConfigurations(); f.Registry.ConfigureTemplateLibrary();
            f.Draft = Accepted(await f.Registry.CreateAuthorDraftAsync("client", new(asset.AssetId, "scale", "draft", f.Metadata())));
            return f;
        }
        public async ValueTask DisposeAsync() { await Registry.DisposeAsync(); _directory.Dispose(); }
    }
}
