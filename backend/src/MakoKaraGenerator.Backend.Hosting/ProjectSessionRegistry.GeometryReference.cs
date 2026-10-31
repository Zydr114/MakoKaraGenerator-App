using MakoKaraGenerator.Backend.Contracts;

namespace MakoKaraGenerator.Backend.Hosting;

public sealed partial class ProjectSessionRegistry
{
    /// <summary>授权会话的精确修订固定容器参考；不执行渲染。</summary>
    public Task<BackendOperationResult<ProjectGeometryReferenceResponse>> ReadGeometryReferenceAsync(
        string clientId, string sessionId, string? expectedRevision, string? nodeId, CancellationToken cancellationToken = default) =>
        TryGet(clientId, sessionId, out ProjectSession session)
            ? session.ReadGeometryReferenceAsync(expectedRevision, nodeId, cancellationToken)
            : Task.FromResult(BackendOperationResult<ProjectGeometryReferenceResponse>.Rejected(MissingSessionError(sessionId)));
}
