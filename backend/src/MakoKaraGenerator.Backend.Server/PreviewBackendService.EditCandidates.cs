using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Hosting;

namespace MakoKaraGenerator.Backend.Server;

internal sealed partial class PreviewBackendService
{
    public Task<BackendOperationResult<PreviewSessionResponse>> CreateEditPreviewAsync(string client,
        string sessionId, ProjectEditPreviewRequest request) => _projects.Receipts.ExecuteAsync(client, request.Metadata,
        "POST /api/v1/project-sessions/" + sessionId + "/edit-previews", request with { Metadata = null },
        () => MutateAsync(client, async () =>
        {
            BackendErrorResponse? size = ValidateSize(request.Size);
            if (size is not null) return BackendOperationResult<PreviewSessionResponse>.Rejected(size);
            if ((request.PreviewId is null) != (request.ExpectedPreviewGeneration is null))
                return Reject<PreviewSessionResponse>(BackendErrorCode.InvalidRequest, "替换候选须同时提供 previewId 与 expectedPreviewGeneration。");
            BackendPreviewSessionResult? existing = null;
            if (request.PreviewId is { } id)
            {
                existing = _previews.Read(client, id);
                if (!existing.Accepted) return BackendOperationResult<PreviewSessionResponse>.Rejected(existing.Error!);
                if (existing.Status!.Session.PreviewKind != "editCandidate" || existing.Status.Session.Source.SourceId != sessionId)
                    return Reject<PreviewSessionResponse>(BackendErrorCode.InvalidRequest, "只能替换同一工程的编辑候选预览。");
                if (existing.Status.Session.PreviewGeneration != request.ExpectedPreviewGeneration)
                    return Reject<PreviewSessionResponse>(BackendErrorCode.PreviewGenerationConflict, "候选预览代次已经改变。");
            }
            if (_factory is null) return BackendOperationResult<PreviewSessionResponse>.Rejected(RuntimeInfo.DisabledReason
                ?? new(BackendErrorCode.PreviewUnavailable, "预览Worker未配置。"));
            var backend = await PrepareRenderBackendAsync(request.RenderBackend, existing?.Status?.Session.RenderBackend).ConfigureAwait(false);
            if (!backend.Accepted) return BackendOperationResult<PreviewSessionResponse>.Rejected(backend.Error!);
            IDisposable? reservation = null;
            BackendPreviewInputCaptureResult captured;
            try
            {
                captured = await _projects.CaptureEditPreviewInputAsync(client, sessionId, request, SelectFactory(_factory, backend.Response!),
                    audio => CaptureResourceFactory(client, audio, out reservation, backend.Response!)).ConfigureAwait(false);
                if (captured.Error is not null) return BackendOperationResult<PreviewSessionResponse>.Rejected(captured.Error);
                IDisposable? ownedReservation = reservation; reservation = null;
                captured = await CompleteCaptureAsync(captured, ownedReservation).ConfigureAwait(false);
            }
            catch (ResourceStoreException error)
            { return BackendOperationResult<PreviewSessionResponse>.Rejected(new(error.Code, error.Message, "source")); }
            finally { reservation?.Dispose(); }
            if (captured.Error is not null) return BackendOperationResult<PreviewSessionResponse>.Rejected(captured.Error);
            var input = captured.Input!;
            // Reserve the existing receipt budget before the preview becomes observable.
            var upper = new PreviewSessionResponse("preview-" + new string('f', 32), input.Source,
                long.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture), request.Size, request.AlphaMode,
                "idle", new(1920, 1080, 1, 2, 16 * 1024 * 1024, 30, 128), PreviewKind: "editCandidate", Committable: false,
                PackageClosure: input.PackageClosure, EditCandidate: input.EditCandidate, RenderBackend: backend.Response!);
            if (System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(BackendOperationResult<PreviewSessionResponse>.Success(upper)).Length
                > _projects.Receipts.MaxReceiptBytes)
            {
                input.RetentionLease?.Dispose();
                return Reject<PreviewSessionResponse>(BackendErrorCode.ResourceTooLarge, "完整候选反馈超过现有回执预算。");
            }
            BackendPreviewSessionResult result;
            try
            {
                result = await _projects.RegisterPreparedPreviewAsync(client, input, () => existing is null ? _previews.Create(client,
                    new(input.Source, request.Size, request.AlphaMode, PreviewKind: "editCandidate", Committable: false, RenderBackend: backend.Response), input)
                    : _previews.Update(client, request.PreviewId!, existing.Status!.Session.Source.SourceRevision,
                        new(request.ExpectedPreviewGeneration!, input.Source, request.Size, request.AlphaMode, RenderBackend: backend.Response), input)).ConfigureAwait(false);
            }
            catch { input.RetentionLease?.Dispose(); throw; }
            if (!result.Accepted) input.RetentionLease?.Dispose();
            return result.Accepted ? BackendOperationResult<PreviewSessionResponse>.Success(result.Status!.Session)
                : BackendOperationResult<PreviewSessionResponse>.Rejected(result.Error!);
        }));
}
