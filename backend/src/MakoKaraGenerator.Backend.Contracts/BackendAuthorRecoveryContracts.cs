using System.Text.Json.Serialization;

namespace MakoKaraGenerator.Backend.Contracts;

/// <summary>本机 profile 中作者草稿的所有权摘要。</summary>
public sealed record ProfileAuthorDraftDto(string DraftId, string DraftRevision, string Name,
    string OwnershipRevision, string OwnershipState, bool CanClaim);

/// <summary>本机 profile 中可发现的作者草稿。</summary>
public sealed record ProfileAuthorDraftListResponse(ProfileAuthorDraftDto[] Drafts, string? NextAfterDraftId,
    string ProtocolVersion = BackendProtocol.Version);

/// <summary>按内容修订和所有权修订认领作者草稿。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AuthorDraftClaimRequest(string ExpectedDraftRevision, string ExpectedOwnershipRevision,
    RequestMetadataDto? Metadata = null);

/// <summary>认领后的草稿与所有权修订。</summary>
public sealed record AuthorDraftClaimResponse(AuthorDraftResponse Draft, string OwnershipRevision,
    string ProtocolVersion = BackendProtocol.Version);
