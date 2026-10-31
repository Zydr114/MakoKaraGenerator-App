using System.Text.Json.Nodes;
using MakoKaraGenerator.Application;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Hosting;
using MakoKaraGenerator.TestSupport;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

public sealed class BackendAuthorRecoveryTests
{
    [Fact]
    public async Task 认领要求过期无在途操作并阻止旧所有权修订重放()
    {
        using TemporaryDirectory directory = new();
        Clock clock = new();
        ClientRegistry clients = new("host", new string('s', 32), new() { LeaseLifetimeSeconds = 1, TokenLifetimeSeconds = 60 }, clock);
        ClientRegistrationResponse owner = clients.Register(new string('s', 32), new()).Response!;
        ClientRegistrationResponse other = clients.Register(new string('s', 32), new()).Response!;
        ResourceStore resources = new(new() { ResourceRoot = directory.FilePath("resources") });
        await using ProjectSessionRegistry registry = NewRegistry(resources, directory.FilePath("state"), clients);
        AuthorDraftResponse draft = await CreateAsync(directory, resources, registry, owner.ClientId);
        ProfileAuthorDraftDto found = Assert.Single(registry.ReadProfileAuthorDrafts(other.ClientId).Response!.Drafts);
        Assert.Equal("active", found.OwnershipState);
        Assert.False(found.CanClaim);
        Assert.Equal(BackendErrorCode.RevisionConflict, (await Claim(registry, other.ClientId, found)).Error!.Code);
        Assert.Null(clients.TryAcquireOperation(owner.ClientId, owner.AccessToken, out IDisposable? pin));
        clock.Now = clock.Now.AddSeconds(2);
        Assert.False(Assert.Single(registry.ReadProfileAuthorDrafts(other.ClientId).Response!.Drafts).CanClaim);
        Assert.Equal(BackendErrorCode.RevisionConflict, (await Claim(registry, other.ClientId, found)).Error!.Code);
        pin!.Dispose();
        AuthorDraftClaimResponse claimed = (await Claim(registry, other.ClientId, found)).Response!;
        Assert.NotEqual(found.OwnershipRevision, claimed.OwnershipRevision);
        Assert.Equal(draft.DraftRevision, claimed.Draft.DraftRevision);
        Assert.Equal(BackendErrorCode.ResourceUnauthorized, registry.ReadAuthorDraft(owner.ClientId, draft.DraftId).Error!.Code);
        Assert.True(registry.ReadAuthorDraft(other.ClientId, draft.DraftId).Accepted);
        Assert.Equal(BackendErrorCode.RevisionConflict, (await Claim(registry, owner.ClientId, found)).Error!.Code);
        ClientRegistrationResponse third = clients.Register(new string('s', 32), new()).Response!;
        ProfileAuthorDraftDto current = Assert.Single(registry.ReadProfileAuthorDrafts(third.ClientId).Response!.Drafts);
        BackendOperationResult<AuthorDraftClaimResponse>[] results = await Task.WhenAll(
            Claim(registry, owner.ClientId, current), Claim(registry, third.ClientId, current));
        Assert.Single(results, result => result.Accepted);
        Assert.Single(results, result => result.Error?.Code == BackendErrorCode.RevisionConflict);
    }

    [Fact]
    public async Task 旧格式重启后可认领未知版本拒绝()
    {
        using TemporaryDirectory directory = new();
        ResourceStore resources = new(new() { ResourceRoot = directory.FilePath("resources") });
        string state = directory.FilePath("state");
        ClientRegistry oldClients = new("old-host", new string('s', 32));
        string oldClient = oldClients.Register(new string('s', 32), new()).Response!.ClientId;
        AuthorDraftResponse draft;
        await using (ProjectSessionRegistry oldRegistry = NewRegistry(resources, state, oldClients))
            draft = await CreateAsync(directory, resources, oldRegistry, oldClient);
        ClientRegistry newClients = new("new-host", new string('s', 32));
        string newClient = newClients.Register(new string('s', 32), new()).Response!.ClientId;
        await using ProjectSessionRegistry registry = NewRegistry(resources, state, newClients);
        string path = Path.Combine(state, "author-drafts", draft.DraftId, "draft.json");
        JsonObject json = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        json.Remove("schemaVersion"); json.Remove("ownershipRevision");
        await File.WriteAllTextAsync(path, json.ToJsonString());
        ProfileAuthorDraftDto found = Assert.Single(registry.ReadProfileAuthorDrafts(newClient).Response!.Drafts);
        Assert.Equal("orphaned", found.OwnershipState);
        Assert.True(found.CanClaim);
        Assert.True((await Claim(registry, newClient, found)).Accepted);
        JsonObject persisted = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        Assert.Equal(1, persisted["schemaVersion"]!.GetValue<int>());
        Assert.NotNull(persisted["ownershipRevision"]);
        persisted["schemaVersion"] = 42;
        await File.WriteAllTextAsync(path, persisted.ToJsonString());
        Assert.Equal(BackendErrorCode.ProjectLoadFailed, registry.ReadAuthorDraft(newClient, draft.DraftId).Error!.Code);
        Assert.Equal(BackendErrorCode.ProjectLoadFailed, registry.ReadProfileAuthorDrafts(newClient).Error!.Code);
        await File.WriteAllTextAsync(path, "[]");
        Assert.Equal(BackendErrorCode.ProjectLoadFailed, registry.ReadAuthorDraft(newClient, draft.DraftId).Error!.Code);
    }

    [Fact]
    public async Task 分页按身份排序并在目标客户端草稿配额满时拒绝认领()
    {
        using TemporaryDirectory directory = new();
        ClientRegistry clients = new("host", new string('s', 32));
        string owner = clients.Register(new string('s', 32), new()).Response!.ClientId;
        string other = clients.Register(new string('s', 32), new()).Response!.ClientId;
        ResourceStore resources = new(new() { ResourceRoot = directory.FilePath("resources") });
        ProjectSessionRegistry registry = NewRegistry(resources, directory.FilePath("state"), clients);
        string archive = directory.FilePath("demo.mkfx");
        await EffectPackageTransfer.WriteArchiveAsync(archive, EffectPackageFixture.Contents());
        await using FileStream stream = File.OpenRead(archive);
        StoredAsset asset = await resources.UploadAsync(other, "demo.mkfx", "application/octet-stream", stream);
        for (int i = 0; i < 16; i++)
            Assert.True((await registry.CreateAuthorDraftAsync(other, new(asset.AssetId, "scale", "draft", Metadata(registry)))).Accepted);
        await using FileStream ownerStream = File.OpenRead(archive);
        StoredAsset ownerAsset = await resources.UploadAsync(owner, "demo.mkfx", "application/octet-stream", ownerStream);
        AuthorDraftResponse target = (await registry.CreateAuthorDraftAsync(owner, new(ownerAsset.AssetId, "scale", "target", Metadata(registry)))).Response!;
        List<ProfileAuthorDraftDto> all = [];
        string? cursor = null;
        do
        {
            ProfileAuthorDraftListResponse page = registry.ReadProfileAuthorDrafts(other, 3, cursor).Response!;
            Assert.InRange(page.Drafts.Length, 1, 3);
            all.AddRange(page.Drafts);
            cursor = page.NextAfterDraftId;
        } while (cursor is not null);
        Assert.Equal(17, all.Count);
        Assert.Equal(all.Select(x => x.DraftId).Order(StringComparer.Ordinal), all.Select(x => x.DraftId));
        Assert.Equal(BackendErrorCode.InvalidRequest, registry.ReadProfileAuthorDrafts(other, 101).Error!.Code);
        Assert.Equal(BackendErrorCode.InvalidRequest, registry.ReadProfileAuthorDrafts(other, 1, "bad").Error!.Code);
        ProfileAuthorDraftDto targetItem = all.Single(x => x.DraftId == target.DraftId);
        await registry.DisposeAsync();
        ClientRegistry restarted = new("restart", new string('s', 32));
        string requester = restarted.Register(new string('s', 32), new()).Response!.ClientId;
        await using ProjectSessionRegistry restartedRegistry = NewRegistry(resources, directory.FilePath("state"), restarted);
        // Preserve the target client's existing ownership across restart to exercise its quota.
        string root = directory.FilePath("state/author-drafts");
        foreach (ProfileAuthorDraftDto item in all.Where(x => x.DraftId != target.DraftId))
        {
            string path = Path.Combine(root, item.DraftId, "draft.json");
            JsonObject json = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
            json["ownerClientId"] = requester;
            await File.WriteAllTextAsync(path, json.ToJsonString());
        }
        Assert.Equal(BackendErrorCode.RequestQuotaExceeded, (await Claim(restartedRegistry, requester, targetItem)).Error!.Code);
        Assert.True(restartedRegistry.ReadAuthorDraft(owner, target.DraftId).Accepted);
    }

    private static ProjectSessionRegistry NewRegistry(ResourceStore resources, string state, ClientRegistry clients)
    {
        ProjectSessionRegistry registry = new(clients.HostInstanceId, resources, NewProjectPreparationService.CreateDefault(), clients: clients);
        registry.ConfigurePersistence(new() { StateRoot = state });
        registry.ConfigureAuthorWorkflows(null);
        return registry;
    }

    private static async Task<AuthorDraftResponse> CreateAsync(TemporaryDirectory directory, ResourceStore resources,
        ProjectSessionRegistry registry, string owner)
    {
        string archive = directory.FilePath("demo.mkfx");
        await EffectPackageTransfer.WriteArchiveAsync(archive, EffectPackageFixture.Contents());
        await using FileStream stream = File.OpenRead(archive);
        StoredAsset asset = await resources.UploadAsync(owner, "demo.mkfx", "application/octet-stream", stream);
        BackendOperationResult<AuthorDraftResponse> result = await registry.CreateAuthorDraftAsync(owner, new(asset.AssetId, "scale", "draft", Metadata(registry)));
        Assert.True(result.Accepted, result.Error?.Message);
        return result.Response!;
    }

    private static Task<BackendOperationResult<AuthorDraftClaimResponse>> Claim(ProjectSessionRegistry registry, string client, ProfileAuthorDraftDto draft) =>
        registry.ClaimProfileAuthorDraftAsync(client, draft.DraftId, new(draft.DraftRevision, draft.OwnershipRevision, Metadata(registry)));

    private static RequestMetadataDto Metadata(ProjectSessionRegistry registry) => new(Guid.NewGuid().ToString("N"), registry.HostInstanceId, registry.ServerTimeUtc.AddMinutes(1));
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
