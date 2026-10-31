using System.Text.Json.Serialization;

namespace MakoKaraGenerator.Backend.Contracts;

/// <summary>一个保留的固定源码修订；旧草稿未知的首次保存时间不推测。</summary>
public sealed record AuthorSourceRevisionDto(
    string SourceRevision,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? SavedAtUtc = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AuthorCompilationSummaryDto? Compilation = null);

/// <summary>精确草稿修订下按源码修订字典序排列的一页历史。</summary>
public sealed record AuthorSourceRevisionsResponse(
    string DraftId,
    string DraftRevision,
    AuthorSourceRevisionDto[] Revisions,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? NextAfterSourceRevision = null,
    string ProtocolVersion = BackendProtocol.Version);
