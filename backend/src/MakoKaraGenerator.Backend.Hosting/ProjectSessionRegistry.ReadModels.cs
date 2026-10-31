using MakoKaraGenerator.Backend.Contracts;

namespace MakoKaraGenerator.Backend.Hosting;

public sealed partial class ProjectSessionRegistry
{
    /// <summary>读取精确修订下单一稳定目标的 canonical 样式及静态四层继承。</summary>
    public Task<BackendOperationResult<ProjectStyleResponse>> ReadStyleAsync(string clientId, string sessionId,
        string? expectedRevision, StyleTargetDto target, CancellationToken cancellationToken = default) =>
        TryGet(clientId, sessionId, out ProjectSession session)
            ? session.ReadStyleAsync(expectedRevision, target, cancellationToken)
            : Task.FromResult(BackendOperationResult<ProjectStyleResponse>.Rejected(MissingSessionError(sessionId)));

    /// <summary>读取精确修订下的歌词编排只读投影。</summary>
    public Task<BackendOperationResult<ProjectOrchestrationResponse>> ReadOrchestrationAsync(
        string clientId, string sessionId, string? expectedRevision, CancellationToken cancellationToken = default) =>
        TryGet(clientId, sessionId, out ProjectSession session)
            ? session.ReadOrchestrationAsync(expectedRevision, cancellationToken)
            : Task.FromResult(BackendOperationResult<ProjectOrchestrationResponse>.Rejected(MissingSessionError(sessionId)));

    /// <summary>读取精确修订下的有界来源元数据。</summary>
    public Task<BackendOperationResult<ProjectLyricBranchesResponse>> ReadLyricBranchesAsync(
        string clientId, string sessionId, string? expectedRevision, int offset = 0, int limit = 100,
        CancellationToken cancellationToken = default) =>
        TryGet(clientId, sessionId, out ProjectSession session)
            ? session.ReadLyricBranchesAsync(expectedRevision, offset, limit, cancellationToken)
            : Task.FromResult(BackendOperationResult<ProjectLyricBranchesResponse>.Rejected(MissingSessionError(sessionId)));

    /// <summary>读取精确修订下的歌词正文、注音与时间窗口。</summary>
    public Task<BackendOperationResult<ProjectLyricsWindowResponse>> ReadLyricsWindowAsync(
        string clientId, string sessionId, string? branchId, string? expectedRevision, int offset = 0, int limit = 100,
        CancellationToken cancellationToken = default) =>
        TryGet(clientId, sessionId, out ProjectSession session)
            ? session.ReadLyricsWindowAsync(branchId, expectedRevision, offset, limit, cancellationToken)
            : Task.FromResult(BackendOperationResult<ProjectLyricsWindowResponse>.Rejected(MissingSessionError(sessionId)));

    /// <summary>读取精确修订下的工程标量设置。</summary>
    public Task<BackendOperationResult<ProjectSettingsResponse>> ReadSettingsAsync(
        string clientId, string sessionId, string? expectedRevision, CancellationToken cancellationToken = default) =>
        TryGet(clientId, sessionId, out ProjectSession session)
            ? session.ReadSettingsAsync(expectedRevision, cancellationToken)
            : Task.FromResult(BackendOperationResult<ProjectSettingsResponse>.Rejected(MissingSessionError(sessionId)));
}
