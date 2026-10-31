using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Backend.Hosting;

public sealed partial class ProjectSessionRegistry
{
    // Called only while the project's single Writer is held. This performs every fallible read/cleanup
    // before returning an immutable prepared value; the caller then mutates without further file IO.
    private BackendOperationResult<ProjectAudioAnalysis> PrepareAnalysisAdoption(string clientId, ProjectSession session, string jobId)
    {
        if (FindAdoptableAnalysis(clientId, session, jobId, out JobResponse? response) is { } rejected)
            return BackendOperationResult<ProjectAudioAnalysis>.Rejected(rejected);
        try
        {
            AudioAnalysisRevision data;
            (BackendJobSidecarStore.ReadLease lease, AudioAnalysisRevision decoded) = ReadRegisteredAnalysis(jobId, response!);
            using (lease) { data = decoded; }

            BackendAnalysisJobInputDto input = response!.AnalysisInput!;
            if (!CurrentAudioDeclarationMatches(session, input))
                return Reject<ProjectAudioAnalysis>(BackendErrorCode.CommandRejected, "当前工程音频声明与分析固定来源不同。");
            StoredAsset asset = _resources.GetAuthorized(clientId, session.AudioAssetId);
            if (!CurrentAudioMatches(session, input, asset))
                return Reject<ProjectAudioAnalysis>(BackendErrorCode.CommandRejected, "当前工程音频声明与分析固定来源不同。");
            VerifyCurrentAnalysisAudio(clientId, asset);

            // Result and audio leases have been released. TTL is not extended by preparation or adoption.
            if (FindAdoptableAnalysis(clientId, session, jobId, out JobResponse? current) is { } finalRejected)
                return BackendOperationResult<ProjectAudioAnalysis>.Rejected(finalRejected);
            if (current!.AnalysisInput != input || current.AnalysisResult != response.AnalysisResult)
                return Reject<ProjectAudioAnalysis>(BackendErrorCode.TaskExecutionFailed, "已登记分析来源或结果在准备期间改变。");
            if (!CurrentAudioMatches(session, input, asset))
                return Reject<ProjectAudioAnalysis>(BackendErrorCode.CommandRejected, "当前工程音频声明在准备期间改变。");
            return BackendOperationResult<ProjectAudioAnalysis>.Success(ProjectAudioAnalysis.Available(data));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or WorkerProtocolException
            or ArgumentException or ResourceStoreException)
        {
            return Reject<ProjectAudioAnalysis>(BackendErrorCode.TaskExecutionFailed,
                "已登记分析结果或当前授权音频不可用、损坏或无法完成读取释放。");
        }
    }

    private BackendErrorResponse? FindAdoptableAnalysis(string clientId, ProjectSession session, string jobId, out JobResponse? response)
    {
        response = null;
        if (!TryFindJob(clientId, jobId, out BackendJob job))
            return new(BackendErrorCode.JobNotFound, "分析任务不存在、无权采用或保留期已结束。");
        response = job.Read();
        if (response.ExpiresAtUtc <= _timeProvider.GetUtcNow())
            return new(BackendErrorCode.JobNotFound, "分析任务保留期已结束。");
        if (response.Kind != "analysis" || response.State != "succeeded" || response.ProjectSessionId != session.ProjectSessionId)
            return new(BackendErrorCode.CommandRejected, "只可采用当前工程的成功独立分析任务。");
        if (response.AnalysisInput is not { SourceKind: "project" } input || input.SourceId != response.ProjectSessionId
            || response.AnalysisResult is null || _analysisResults is null)
            return new(BackendErrorCode.TaskExecutionFailed, "成功分析任务缺少已登记的完整结果。");
        return null;
    }

    private static bool CurrentAudioDeclarationMatches(ProjectSession session, BackendAnalysisJobInputDto input) =>
        session.AudioAssetId == input.Audio.AssetId && session.Workspace.Project.AudioMedia is { } media
        && media.Hash == input.Audio.Sha256 && media.ByteLength == input.Audio.SizeBytes;

    private static bool CurrentAudioMatches(ProjectSession session, BackendAnalysisJobInputDto input, StoredAsset asset)
    {
        Project project = session.Workspace.Project;
        StringComparison paths = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return CurrentAudioDeclarationMatches(session, input) && asset.AssetId == input.Audio.AssetId
            && asset.Size == input.Audio.SizeBytes && asset.Sha256 == input.Audio.Sha256
            // Reopened/recovered projects keep their pinned input closure path, distinct from the resource copy.
            && session.PreparedAudioSourcePath is { Length: > 0 } sourcePath && Path.IsPathFullyQualified(sourcePath)
            && string.Equals(Path.GetFullPath(sourcePath), sourcePath, paths)
            && string.Equals(project.AudioPath, sourcePath, paths);
    }

    private void VerifyCurrentAnalysisAudio(string clientId, StoredAsset asset)
    {
        if (asset.Size is < 4 or > AnalysisResultCodec.MaximumAudioBytes || new FileInfo(asset.StoragePath).LinkTarget is not null)
            throw new IOException("当前授权分析音频文件不符合固定来源。");
        VerifyFixedAudioSource(clientId, asset);
    }
}
