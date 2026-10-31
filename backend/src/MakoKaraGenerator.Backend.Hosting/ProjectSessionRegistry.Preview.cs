using MakoKaraGenerator.Backend.Contracts;

namespace MakoKaraGenerator.Backend.Hosting;

public sealed partial class ProjectSessionRegistry
{
    /// <summary>在来源锁内复核固定输入并执行登记。</summary>
    public async Task<BackendOperationResult<T>> RegisterPreparedInputAsync<T>(string clientId,
        BackendPreviewPinnedInput input, Func<BackendOperationResult<T>> register)
    {
        if (input.Source.Kind == "authorDraft")
            return await RegisterPreparedAuthorInputAsync(clientId, input, register).ConfigureAwait(false);
        ProjectSession? session = input.CapturedProjectSession;
        if (session is null) return BackendOperationResult<T>.Rejected(new(BackendErrorCode.SessionNotFound, "预览来源工程已经关闭。"));
        await session.Writer.WaitAsync().ConfigureAwait(false);
        try
        {
            if (session.OwnerClientId != clientId || !TryGet(clientId, session.ProjectSessionId, out var current) || !ReferenceEquals(current, session))
                return BackendOperationResult<T>.Rejected(new(BackendErrorCode.SessionNotFound, "预览来源工程已经关闭。"));
            if (session.MutationBlocked) return BackendOperationResult<T>.Rejected(new(BackendErrorCode.SessionClosing, "预览来源工程正在关闭。"));
            string revision = input.EditCandidate?.BaseProjectRevision ?? input.Source.SourceRevision;
            if (session.ProjectRevision.ToString(System.Globalization.CultureInfo.InvariantCulture) != revision)
                return BackendOperationResult<T>.Rejected(new(BackendErrorCode.RevisionConflict, "固定媒体准备期间工程修订已经改变。", "expectedRevision"));
            return register();
        }
        finally { session.Writer.Release(); }
    }

    /// <summary>固定媒体准备后，在原工程Writer内重新核对身份并登记预览。</summary>
    public async Task<BackendPreviewSessionResult> RegisterPreparedPreviewAsync(string clientId, BackendPreviewPinnedInput input,
        Func<BackendPreviewSessionResult> register)
    {
        if (input.Source.Kind == "authorDraft")
            return await RegisterPreparedAuthorPreviewAsync(clientId, input, register).ConfigureAwait(false);
        ProjectSession? session = input.CapturedProjectSession;
        if (session is null) return new(null, new(BackendErrorCode.SessionNotFound, "预览来源工程已经关闭。"));
        await session.Writer.WaitAsync().ConfigureAwait(false);
        try
        {
            if (session.OwnerClientId != clientId || !TryGet(clientId, session.ProjectSessionId, out var current) || !ReferenceEquals(current, session))
                return new(null, new(BackendErrorCode.SessionNotFound, "预览来源工程已经关闭。"));
            if (session.MutationBlocked) return new(null, new(BackendErrorCode.SessionClosing, "预览来源工程正在关闭。"));
            string revision = input.EditCandidate?.BaseProjectRevision ?? input.Source.SourceRevision;
            if (session.ProjectRevision.ToString(System.Globalization.CultureInfo.InvariantCulture) != revision)
                return new(null, new(BackendErrorCode.RevisionConflict, "固定媒体准备期间工程修订已经改变。", "expectedRevision"));
            return register();
        }
        finally { session.Writer.Release(); }
    }

    /// <summary>客户端拥有预览事件流；所属工程关闭后仍可提示已固定预览的状态。</summary>
    public void PublishPreviewEvent(string clientId, PreviewEventDto preview)
    {
        string type = preview.State switch
        {
            "ready" => "preview.frameReady",
            "failed" => "preview.failed",
            "closed" => "preview.closed",
            _ => "preview.stateChanged",
        };
        _events.Append(clientId, type, preview: preview);
    }
}
