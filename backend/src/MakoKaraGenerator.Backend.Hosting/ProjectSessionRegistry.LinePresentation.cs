using MakoKaraGenerator.Backend.Contracts;

namespace MakoKaraGenerator.Backend.Hosting;

public sealed partial class ProjectSessionRegistry
{
    /// <summary>读取精确工程修订下的本句变换与进入动画继承。</summary>
    public Task<BackendOperationResult<ProjectLinePresentationResponse>> ReadLinePresentationAsync(string clientId, string sessionId,
        string? expectedRevision, string? branchId, string? lineId, CancellationToken cancellationToken = default) =>
        TryGet(clientId, sessionId, out ProjectSession session)
            ? session.ReadLinePresentationAsync(expectedRevision, branchId, lineId, cancellationToken)
            : Task.FromResult(BackendOperationResult<ProjectLinePresentationResponse>.Rejected(MissingSessionError(sessionId)));
}
