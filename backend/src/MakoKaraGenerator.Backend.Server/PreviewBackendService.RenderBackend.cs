using System.Globalization;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Hosting;
using MakoKaraGenerator.Backend.Rendering;
using MakoKaraGenerator.Rendering;

namespace MakoKaraGenerator.Backend.Server;

internal sealed partial class PreviewBackendService
{
    internal void StopAccepting() => _shutdown.Cancel();

    public async Task<BackendOperationResult<RenderBackendsResponse>> ReadRenderBackendsAsync(string client,
        CancellationToken cancellationToken = default)
    {
        await _membership.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_stopping || _shutdown.IsCancellationRequested)
                return Reject<RenderBackendsResponse>(BackendErrorCode.SessionClosing, "预览服务正在关闭。");
            if (!_clients.TryReadLease(client, out _, out bool dormant) || dormant)
                return Reject<RenderBackendsResponse>(BackendErrorCode.ClientTokenExpired, "客户端租约已休眠，请恢复后重建预览。");
        }
        finally { _membership.Release(); }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        using var fallback = _renderBackends is null ? new BackendRenderBackendConfiguration(null,
            _defaultRenderBackend == "gpu" ? "vulkan" : "cpu") : null;
        var configuration = _renderBackends ?? fallback!;
        return BackendOperationResult<RenderBackendsResponse>.Success(
            await configuration.ReadAsync(linked.Token).ConfigureAwait(false));
    }

    public Task<BackendOperationResult<PreviewSessionResponse>> SwitchRenderBackendAsync(string client, string id,
        PreviewRenderBackendRequest request) => _projects.Receipts.ExecuteAsync(client, request.Metadata,
        "PUT /api/v1/preview-sessions/" + id + "/render-backend", request with { Metadata = null },
        () => MutateAsync(client, async () =>
        {
            if (request.RenderBackend is not ("cpu" or "gpu"))
                return BackendOperationResult<PreviewSessionResponse>.Rejected(InvalidRenderBackend());
            if (!long.TryParse(request.ExpectedPreviewGeneration, NumberStyles.None, CultureInfo.InvariantCulture,
                    out long generation) || generation < 0
                || generation.ToString(CultureInfo.InvariantCulture) != request.ExpectedPreviewGeneration)
                return BackendOperationResult<PreviewSessionResponse>.Rejected(new(BackendErrorCode.InvalidRequest,
                    "expectedPreviewGeneration必须为规范的非负Int64字符串。", "expectedPreviewGeneration"));
            var captured = _previews.CaptureRenderBackendInput(client, id, request.ExpectedPreviewGeneration);
            if (!captured.Accepted) return BackendOperationResult<PreviewSessionResponse>.Rejected(captured.Error!);
            BackendPreviewPinnedInput input = captured.Response!;
            bool transferred = false;
            try
            {
                if (request.RenderBackend == "cpu" && ScriptBackendRequirement.RequiresVulkan(input.Snapshot))
                    return BackendOperationResult<PreviewSessionResponse>.Rejected(new(BackendErrorCode.CommandNotSupported,
                        "SDK v2 效果要求 GPU 渲染后端。", "renderBackend", FailureCode: "PackageBackendUnsupported"));
                if (input.RenderBackend != request.RenderBackend)
                {
                    var backend = await PrepareRenderBackendAsync(request.RenderBackend).ConfigureAwait(false);
                    if (!backend.Accepted) return BackendOperationResult<PreviewSessionResponse>.Rejected(backend.Error!);
                    IRenderFrameSessionFactory factory = SelectFactory(input.Factory, request.RenderBackend);
                    if (factory is not IBackendPreviewProfileCapability capability)
                        return BackendOperationResult<PreviewSessionResponse>.Rejected(new(BackendErrorCode.PreviewUnavailable,
                            "当前预览工厂未提供所选来源能力握手。", "worker", FailureCode: "WorkerRenderBackendUnavailable"));
                    string profile = input.VerifiedProfile ?? ProjectPreviewSnapshotProfile.Select(input.Snapshot);
                    if (await capability.EnsureProfileAsync(profile, _shutdown.Token).ConfigureAwait(false) is { } error)
                        return BackendOperationResult<PreviewSessionResponse>.Rejected(error);
                    input = input with { Factory = factory, RenderBackend = request.RenderBackend };
                }
                BackendPreviewSessionResult result = _previews.SwitchRenderBackend(client, id,
                    request.ExpectedPreviewGeneration, input);
                transferred = result.Accepted;
                return result.Accepted ? BackendOperationResult<PreviewSessionResponse>.Success(result.Status!.Session)
                    : BackendOperationResult<PreviewSessionResponse>.Rejected(result.Error!);
            }
            catch (SnapshotContractException error)
            {
                return BackendOperationResult<PreviewSessionResponse>.Rejected(new(BackendErrorCode.PreviewUnavailable,
                    "当前预览工厂未提供所选渲染后端。", "renderBackend", FailureCode: error.Code));
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            { return Reject<PreviewSessionResponse>(BackendErrorCode.SessionClosing, "预览服务正在关闭。"); }
            finally { if (!transferred) input.RetentionLease?.Dispose(); }
        }));

    private async Task<BackendOperationResult<string>> PrepareRenderBackendAsync(string? requested, string? fallback = null)
    {
        string backend;
        try
        {
            backend = _renderBackends?.Resolve(requested, fallback) ?? requested ?? fallback ?? _defaultRenderBackend;
            if (backend is not ("cpu" or "gpu")) return BackendOperationResult<string>.Rejected(InvalidRenderBackend());
        }
        catch (ArgumentException) { return BackendOperationResult<string>.Rejected(InvalidRenderBackend()); }
        try
        {
            if (_renderBackends is not null)
            {
                if (await _renderBackends.VerifyAsync(backend, _shutdown.Token).ConfigureAwait(false) is { } error)
                    return BackendOperationResult<string>.Rejected(error);
            }
            else if (backend != "cpu")
                return BackendOperationResult<string>.Rejected(new(BackendErrorCode.PreviewUnavailable,
                    "当前预览未配置实际设备检查。", "renderBackend", FailureCode: "WorkerRenderBackendUnavailable"));
            return BackendOperationResult<string>.Success(backend);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        { return Reject<string>(BackendErrorCode.SessionClosing, "预览服务正在关闭。"); }
    }

    private static BackendErrorResponse InvalidRenderBackend() => new(BackendErrorCode.InvalidRequest,
        "renderBackend必须为cpu、gpu或允许省略的null。", "renderBackend");

    private static IRenderFrameSessionFactory SelectFactory(IRenderFrameSessionFactory factory, string backend) =>
        factory is IBackendPreviewRenderBackendFactory selectable ? selectable.WithRenderBackend(backend)
        : backend == "cpu" ? factory
        : throw new SnapshotContractException("WorkerRenderBackendUnavailable", "当前预览工厂未提供显式渲染后端。");
}
