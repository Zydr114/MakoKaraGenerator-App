using System.Globalization;
using MakoKaraGenerator.Backend.Contracts;

namespace MakoKaraGenerator.Backend.Hosting;

public sealed partial class ProjectSessionRegistry
{
    private WorkerProcessOptions? _fontWorker;

    /// <summary>配置字体目录和检查使用的受管理 Worker。</summary>
    public void ConfigureFontWorker(WorkerProcessOptions? worker) => _fontWorker = worker;

    /// <summary>读取实际 Worker 字体族目录。</summary>
    public async Task<BackendOperationResult<FontListResponse>> ListFontsAsync(
        string clientId, CancellationToken cancellationToken = default)
    {
        if (_fontWorker is null)
            return BackendOperationResult<FontListResponse>.Rejected(new(
                BackendErrorCode.TaskUnavailable, "字体 Worker 未部署。", FailureCode: "FontWorkerUnavailable"));
        try
        {
            await using WorkerProcessProxy proxy = new(_fontWorker);
            return BackendOperationResult<FontListResponse>.Success(
                await proxy.ListFontsAsync(TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false));
        }
        catch (WorkerProtocolException error)
        {
            return BackendOperationResult<FontListResponse>.Rejected(new(
                BackendErrorCode.TaskUnavailable, "字体目录不可用。", FailureCode: error.Code));
        }
    }

    /// <summary>读取实际 Worker 对指定文本的字体整形与回退诊断。</summary>
    public async Task<BackendOperationResult<FontInspectResponse>> InspectFontAsync(
        string clientId, FontInspectRequest input, CancellationToken cancellationToken = default)
    {
        try { FontWorkerContract.Validate(input); }
        catch (WorkerProtocolException error)
        {
            return BackendOperationResult<FontInspectResponse>.Rejected(new(
                BackendErrorCode.InvalidRequest, "字体检查参数无效。", "text", error.Code));
        }
        if (input.SessionId is not null || input.ExpectedRevision is not null)
        {
            if (input.SessionId is null || input.ExpectedRevision is null
                || !TryGet(clientId, input.SessionId, out ProjectSession session)
                || session.ProjectRevision.ToString(CultureInfo.InvariantCulture) != input.ExpectedRevision)
                return BackendOperationResult<FontInspectResponse>.Rejected(new(
                    BackendErrorCode.RevisionConflict, "工程字体检查来源修订已经改变。", "expectedRevision"));
        }
        if (_fontWorker is null)
            return BackendOperationResult<FontInspectResponse>.Rejected(new(
                BackendErrorCode.TaskUnavailable, "字体 Worker 未部署。", FailureCode: "FontWorkerUnavailable"));
        try
        {
            await using WorkerProcessProxy proxy = new(_fontWorker);
            return BackendOperationResult<FontInspectResponse>.Success(
                await proxy.InspectFontAsync(input, TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false));
        }
        catch (WorkerProtocolException error)
        {
            return BackendOperationResult<FontInspectResponse>.Rejected(new(
                BackendErrorCode.TaskUnavailable, "字体检查不可用。", FailureCode: error.Code));
        }
    }
}
