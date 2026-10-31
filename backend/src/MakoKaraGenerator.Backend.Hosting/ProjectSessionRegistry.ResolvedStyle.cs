using MakoKaraGenerator.Backend.Contracts;

namespace MakoKaraGenerator.Backend.Hosting;

public sealed partial class ProjectSessionRegistry
{
    /// <summary>读取精确修订下真实来源/片段/容器路径的静态预设样式及来源。</summary>
    public Task<BackendOperationResult<ProjectResolvedStyleResponse>> ReadResolvedStyleAsync(string clientId, string sessionId,
        string? expectedRevision, string? branchId, string? lineId, string? tokenId = null, string? clipId = null, string? nodeId = null,
        CancellationToken cancellationToken = default) => TryGet(clientId, sessionId, out ProjectSession session)
            ? session.ReadResolvedStyleAsync(expectedRevision, branchId, lineId, tokenId, clipId, nodeId, cancellationToken)
            : Task.FromResult(BackendOperationResult<ProjectResolvedStyleResponse>.Rejected(MissingSessionError(sessionId)));
}
