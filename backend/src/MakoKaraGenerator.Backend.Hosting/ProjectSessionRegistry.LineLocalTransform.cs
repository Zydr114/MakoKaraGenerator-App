using MakoKaraGenerator.Backend.Contracts;

namespace MakoKaraGenerator.Backend.Hosting;

public sealed partial class ProjectSessionRegistry
{
    /// <summary>读取精确修订下稳定来源与行的局部微调。</summary>
    public Task<BackendOperationResult<ProjectLineLocalTransformResponse>> ReadLineLocalTransformAsync(
        string clientId, string sessionId, string? expectedRevision, string? branchId, string? lineId,
        CancellationToken cancellationToken = default) =>
        TryGet(clientId, sessionId, out ProjectSession session)
            ? session.ReadLineLocalTransformAsync(expectedRevision, branchId, lineId, cancellationToken)
            : Task.FromResult(BackendOperationResult<ProjectLineLocalTransformResponse>.Rejected(MissingSessionError(sessionId)));
}
