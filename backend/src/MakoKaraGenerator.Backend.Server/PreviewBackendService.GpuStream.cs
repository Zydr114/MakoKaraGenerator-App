using System.Globalization;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Hosting;

namespace MakoKaraGenerator.Backend.Server;

internal sealed partial class PreviewBackendService
{
    private GpuPreviewStreamRegistry? _gpuStreams;
    private WorkerProcessOptions? _gpuWorkerOptions;
    private GpuWorkerPool? _gpuWorkerPool;
    private DesktopConsumerIdentity? _desktopConsumer;
    internal bool GpuStreamsConfigured => _gpuStreams is not null;
    internal async Task<bool> GpuStreamsAvailableAsync() => GpuStreamsConfigured && OperatingSystem.IsLinux() &&
        _renderBackends is not null && await _renderBackends.VerifyAsync("gpu", _shutdown.Token).ConfigureAwait(false) is null;

    internal GpuPreviewWorkerClaim? ClaimGpuStream(string id, string consumerId, string hostInstanceId,
        string expectedInputGeneration, string? secret)
    {
        if (_desktopConsumer is null || _gpuStreams is null || hostInstanceId != _clients.HostInstanceId ||
            !_desktopConsumer.Authenticate(consumerId, secret)) return null;
        return _gpuStreams.Claim(consumerId, id, expectedInputGeneration);
    }

    internal void ConfigureGpuStreams(DesktopConsumerIdentity? consumer, WorkerProcessOptions? options)
    {
        if (consumer is null || options is null) return;
        _desktopConsumer = consumer;
        _gpuWorkerOptions = options;
        _gpuStreams = new();
        _gpuWorkerPool = new(options);
        _renderBackends?.ConfigureGpuStreamDevice(_gpuWorkerPool.VerifyDeviceAsync);
    }

    internal async Task<bool> AbortGpuConsumerAsync(string id, string consumerId, string hostInstanceId,
        string inputGeneration, string producerEpoch, string? secret)
    {
        if (_desktopConsumer is null || _gpuStreams is null || hostInstanceId != _clients.HostInstanceId ||
            !_desktopConsumer.Authenticate(consumerId, secret)) return false;
        await _membership.WaitAsync().ConfigureAwait(false);
        try
        {
            var owned = _gpuStreams.QuarantineConsumerFailure(consumerId, id, inputGeneration, producerEpoch);
            if (owned is null) return false;
            var (client, worker) = owned.Value;
            bool terminated = false;
            try
            {
                // Terminate the host-owned process rather than treating an unknown
                // consumer as drained. Its quota stays quarantined until app exit.
                await worker.RetryTerminationAsync().ConfigureAwait(false);
                terminated = true;
            }
            catch (Exception) { /* Retained ownership permits another shutdown retry. */ }
            finally
            {
                _gpuStreams.FailChange(client, id, worker, "GpuConsumerCompletionUnknown");
                _gpuStreams.ReleaseRetiredInput(client, id, worker);
            }
            return terminated;
        }
        finally { _membership.Release(); }
    }

    internal Task<BackendOperationResult<GpuPreviewStreamState>> CreateGpuStreamAsync(string client,
        GpuPreviewStreamCreateRequest request) => _projects.Receipts.ExecuteAsync(client, request.Metadata,
        "POST /api/v1/gpu-preview-streams", request with { Metadata = null }, () => MutateAsync(client, async () =>
        {
            if (_gpuStreams is null || _gpuWorkerOptions is null || _desktopConsumer is null)
                return Reject<GpuPreviewStreamState>(BackendErrorCode.PreviewUnavailable, "GPU 流未配置。");
            if (request.Api != "vulkan" || request.ConsumerId != _desktopConsumer.ConsumerId ||
                request.Size is null || request.Size.Width is < 64 or > 2560 || request.Size.Height is < 64 or > 1440 ||
                request.AlphaMode is not (PreviewAlphaMode.Opaque or PreviewAlphaMode.Straight))
                return Reject<GpuPreviewStreamState>(BackendErrorCode.InvalidRequest, "GPU 流参数无效。");
            using IDisposable? reservation = _gpuStreams.TryReserve(client, request.ConsumerId);
            if (reservation is null) return Reject<GpuPreviewStreamState>(BackendErrorCode.SessionQuotaExceeded, "GPU 流配额已用完。");
            var backend = await PrepareRenderBackendAsync("gpu").ConfigureAwait(false);
            if (!backend.Accepted) return BackendOperationResult<GpuPreviewStreamState>.Rejected(backend.Error!);
            var capture = await CaptureAsync(client, request.Source, "gpu").ConfigureAwait(false);
            if (capture.Error is not null) return BackendOperationResult<GpuPreviewStreamState>.Rejected(capture.Error);
            BackendPreviewPinnedInput input = capture.Input!;
            GpuStreamWorkerSession? worker = null;
            bool published = false;
            bool quarantinedOpen = false;
            try
            {
                if (input.PreparedInput is null)
                    return Reject<GpuPreviewStreamState>(BackendErrorCode.PreviewUnavailable, "固定 Runtime 输入不可用。");
                string streamId = Guid.NewGuid().ToString("N");
                worker = await GpuStreamWorkerSession.OpenAsync(_gpuWorkerPool!, input.PreparedInput, streamId,
                    _clients.HostInstanceId, "1", request.Size.Width, request.Size.Height,
                    request.AlphaMode == PreviewAlphaMode.Straight, _desktopConsumer.MainPid, _shutdown.Token).ConfigureAwait(false);
                GpuPreviewStreamState state = new(streamId, "1", "1", "ready", input.Source, request.Size,
                    request.AlphaMode, "vulkan", worker.Opened.ProducerEpoch, worker.Opened.PoolGeneration,
                    worker.Opened.SlotCount, checked((long)worker.Opened.AllocationBytes));
                var result = await _projects.RegisterPreparedInputAsync(client, input, () =>
                {
                    if (!_clients.TryReadLease(client, out _, out bool dormant) || dormant || _shutdown.IsCancellationRequested)
                        return Reject<GpuPreviewStreamState>(BackendErrorCode.ClientTokenExpired, "客户端租约已失效。");
                    if (!_gpuStreams.Publish(reservation, state, input, worker))
                        return Reject<GpuPreviewStreamState>(BackendErrorCode.SessionQuotaExceeded, "GPU 流无法登记。");
                    published = true;
                    return BackendOperationResult<GpuPreviewStreamState>.Success(state);
                }).ConfigureAwait(false);
                return result;
            }
            catch (GpuStreamOpenCleanupException failure)
            {
                _gpuStreams.QuarantineOpen(reservation, null, null, input, failure);
                quarantinedOpen = true;
                throw;
            }
            finally
            {
                if (!published && !quarantinedOpen)
                    await RollbackGpuCreateAsync(_gpuStreams, reservation, input, worker).ConfigureAwait(false);
            }
        }));

    internal Task<BackendOperationResult<GpuPreviewStreamState>> ReadGpuStreamAsync(string client, string id) =>
        MutateAsync(client, async () =>
        {
            GpuPreviewStreamState? state = _gpuStreams?.Read(client, id);
            if (state is null) return Reject<GpuPreviewStreamState>(BackendErrorCode.SessionNotFound, "GPU 流不存在。");
            if (state.RegistrationState == "failed") return BackendOperationResult<GpuPreviewStreamState>.Success(state);
            GpuStreamWorkerSession? worker = _gpuStreams!.WorkerForRead(client, id);
            if (worker is null) return BackendOperationResult<GpuPreviewStreamState>.Success(state);
            GpuStreamWorkerState? failedState = null;
            try
            {
                GpuStreamWorkerState current = await worker.ReadAsync(_shutdown.Token).ConfigureAwait(false);
                if (current.State != "failed")
                    return BackendOperationResult<GpuPreviewStreamState>.Success(state with
                    { LastAppliedControlSeq = current.LastAppliedControlSeq, WorkerState = current });
                failedState = current;
            }
            catch (Exception) when (!_shutdown.IsCancellationRequested) { }
            if (_gpuStreams.TryBeginChange(client, id, state.StreamRevision, out _))
            {
                try { await worker.DisposeAsync().ConfigureAwait(false); }
                catch (Exception) { }
                finally
                {
                    _gpuStreams.FailChange(client, id, worker, failedState?.FailureCode ?? "GpuWorkerFailed", failedState);
                    _gpuStreams.ReleaseRetiredInput(client, id, worker);
                }
            }
            return BackendOperationResult<GpuPreviewStreamState>.Success(_gpuStreams.Read(client, id)!);
        });

    internal Task<BackendOperationResult<GpuPreviewStreamState>> ReplaceGpuStreamAsync(string client, string id,
        GpuPreviewStreamReplaceRequest request) => _projects.Receipts.ExecuteAsync(client, request.Metadata,
        "PUT /api/v1/gpu-preview-streams/" + id + "/source", request with { Metadata = null },
        () => request.Source is null ? Task.FromResult(Reject<GpuPreviewStreamState>(BackendErrorCode.InvalidRequest, "来源不能为空。"))
            : ChangeGpuStreamAsync(client, id, request.ExpectedStreamRevision, request.Source, null));

    internal Task<BackendOperationResult<GpuPreviewStreamState>> ResizeGpuStreamAsync(string client, string id,
        GpuPreviewStreamResizeRequest request) => _projects.Receipts.ExecuteAsync(client, request.Metadata,
        "PUT /api/v1/gpu-preview-streams/" + id + "/size", request with { Metadata = null },
        () => request.Size is null ? Task.FromResult(Reject<GpuPreviewStreamState>(BackendErrorCode.InvalidRequest, "尺寸不能为空。"))
            : ChangeGpuStreamAsync(client, id, request.ExpectedStreamRevision, null, request.Size));

    internal Task<BackendOperationResult<GpuPreviewStreamState>> CloseGpuStreamAsync(string client, string id,
        GpuPreviewStreamCloseRequest request) => _projects.Receipts.ExecuteAsync(client, request.Metadata,
        "DELETE /api/v1/gpu-preview-streams/" + id, request with { Metadata = null },
        () => MutateAsync(client, () => CloseGpuStreamCoreAsync(client, id, request.ExpectedStreamRevision)));

    private async Task<BackendOperationResult<GpuPreviewStreamState>> CloseGpuStreamCoreAsync(string client, string id, string revision)
    {
        if (_gpuStreams is null) return Reject<GpuPreviewStreamState>(BackendErrorCode.SessionNotFound, "GPU 流不存在。");
        if (!_gpuStreams.TryBeginChange(client, id, revision, out bool found))
            return Reject<GpuPreviewStreamState>(!found ? BackendErrorCode.SessionNotFound : BackendErrorCode.RevisionConflict,
                "GPU 流不存在或修订已改变。");
        var resources = _gpuStreams.GetForClose(client, id)!.Value;
        try
        {
            await resources.Worker.DisposeAsync().ConfigureAwait(false);
            _gpuStreams.ReleaseRetiredInput(client, id, resources.Worker);
            if (!_gpuStreams.CompleteClose(client, id, resources.Worker))
            {
                _gpuStreams.FailChange(client, id, resources.Worker, "GpuStreamQuarantined");
                return Reject<GpuPreviewStreamState>(BackendErrorCode.RevisionConflict, "GPU 流关闭状态已改变。");
            }
            return BackendOperationResult<GpuPreviewStreamState>.Success(resources.State with { RegistrationState = "closed" });
        }
        catch
        {
            _gpuStreams.FailChange(client, id, resources.Worker, "GpuStreamDrainFailed");
            _gpuStreams.ReleaseRetiredInput(client, id, resources.Worker);
            throw;
        }
    }

    private async Task DrainGpuClientAsync(string client)
    {
        if (_gpuStreams is null) return;
        List<Exception>? failures = null;
        foreach (var (streamId, revision) in _gpuStreams.ClientStreams(client))
        {
            // A deliberately isolated consumer is retained until the desktop exits.
            // Its owned process is already gone; do not retry the dead protocol or
            // convert its unverified GPU completion into a shutdown failure.
            if (_gpuStreams.IsConsumerIsolationComplete(client, streamId)) continue;
            try { await CloseGpuStreamCoreAsync(client, streamId, revision).ConfigureAwait(false); }
            catch (Exception error) { (failures ??= []).Add(error); }
        }
        if (failures is not null) throw new AggregateException(failures);
    }

    private Task<BackendOperationResult<GpuPreviewStreamState>> ChangeGpuStreamAsync(string client, string id,
        string expectedRevision, PreviewSourceDto? source, PreviewSizeDto? size) => MutateAsync(client, async () =>
    {
        if (_gpuStreams is null || _gpuWorkerOptions is null || _desktopConsumer is null)
            return Reject<GpuPreviewStreamState>(BackendErrorCode.PreviewUnavailable, "GPU 流未配置。");
        GpuPreviewStreamState? old = _gpuStreams.Read(client, id);
        if (old is null) return Reject<GpuPreviewStreamState>(BackendErrorCode.SessionNotFound, "GPU 流不存在。");
        if (old.RegistrationState == "failed") return Reject<GpuPreviewStreamState>(BackendErrorCode.PreviewUnavailable, "GPU 流已失败。");
        if (old.StreamRevision != expectedRevision)
            return Reject<GpuPreviewStreamState>(BackendErrorCode.RevisionConflict, "GPU 流修订已改变。");
        size ??= old.Size;
        if (size.Width is < 64 or > 2560 || size.Height is < 64 or > 1440 ||
            !long.TryParse(old.InputGeneration, NumberStyles.None, CultureInfo.InvariantCulture, out long generation) ||
            !long.TryParse(old.StreamRevision, NumberStyles.None, CultureInfo.InvariantCulture, out long revision) ||
            generation == long.MaxValue || revision == long.MaxValue)
            return Reject<GpuPreviewStreamState>(BackendErrorCode.InvalidRequest, "GPU 流尺寸或代次无效。");
        var captured = await CaptureAsync(client, source ?? old.Source, "gpu").ConfigureAwait(false);
        if (captured.Error is not null) return BackendOperationResult<GpuPreviewStreamState>.Rejected(captured.Error);
        BackendPreviewPinnedInput input = captured.Input!;
        GpuStreamWorkerSession? worker = null;
        GpuStreamWorkerSession? previous = null;
        bool published = false;
        bool begun = false;
        bool quarantinedOpen = false;
        try
        {
            if (input.PreparedInput is null) return Reject<GpuPreviewStreamState>(BackendErrorCode.PreviewUnavailable, "固定 Runtime 输入不可用。");
            var checkedInput = await _projects.RegisterPreparedInputAsync(client, input, () =>
            {
                if (!_clients.TryReadLease(client, out _, out bool dormant) || dormant || _shutdown.IsCancellationRequested)
                    return Reject<GpuPreviewStreamState>(BackendErrorCode.ClientTokenExpired, "客户端租约已失效。");
                if (!_gpuStreams.TryBeginChange(client, id, expectedRevision, out bool found))
                    return Reject<GpuPreviewStreamState>(found ? BackendErrorCode.RevisionConflict : BackendErrorCode.SessionNotFound, "GPU 流不可变更。");
                begun = true;
                return BackendOperationResult<GpuPreviewStreamState>.Success(old);
            }).ConfigureAwait(false);
            if (!checkedInput.Accepted) return checkedInput;
            var oldResources = _gpuStreams.GetForClose(client, id)!.Value;
            previous = oldResources.Worker;
            await previous.DisposeAsync().ConfigureAwait(false);
            string nextGeneration = (generation + 1).ToString(CultureInfo.InvariantCulture);
            worker = await GpuStreamWorkerSession.OpenAsync(_gpuWorkerPool!, input.PreparedInput, id,
                _clients.HostInstanceId, nextGeneration, size.Width, size.Height,
                old.AlphaMode == PreviewAlphaMode.Straight, _desktopConsumer.MainPid, _shutdown.Token).ConfigureAwait(false);
            var next = old with { StreamRevision = (revision + 1).ToString(CultureInfo.InvariantCulture),
                InputGeneration = nextGeneration, RegistrationState = "ready", Source = input.Source, Size = size,
                ProducerEpoch = worker.Opened.ProducerEpoch, PoolGeneration = worker.Opened.PoolGeneration,
                SlotCount = worker.Opened.SlotCount, AllocationBytes = checked((long)worker.Opened.AllocationBytes),
                LastAppliedControlSeq = null, FailureCode = null };
            return await _projects.RegisterPreparedInputAsync(client, input, () =>
            {
                if (!_clients.TryReadLease(client, out _, out bool dormant) || dormant || _shutdown.IsCancellationRequested)
                    return Reject<GpuPreviewStreamState>(BackendErrorCode.ClientTokenExpired, "客户端租约已失效。");
                if (!_gpuStreams.CompleteChange(client, id, previous, next, input, worker))
                    return Reject<GpuPreviewStreamState>(BackendErrorCode.RevisionConflict, "GPU 流代次已改变。");
                published = true;
                oldResources.Input.RetentionLease?.Dispose();
                return BackendOperationResult<GpuPreviewStreamState>.Success(next);
            }).ConfigureAwait(false);
        }
        catch (GpuStreamOpenCleanupException failure)
        {
            _gpuStreams.QuarantineOpen(null, client, id, input, failure);
            quarantinedOpen = true;
            throw;
        }
        finally
        {
            if (!published)
            {
                try
                {
                    if (!quarantinedOpen) await RollbackGpuChangeAsync(_gpuStreams, client, id, input, worker).ConfigureAwait(false);
                }
                finally
                {
                    if (begun && previous is not null)
                    {
                        _gpuStreams.FailChange(client, id, previous, "GpuStreamChangeFailed");
                        _gpuStreams.ReleaseRetiredInput(client, id, previous);
                    }
                }
            }
        }
    });

    private static async Task RollbackGpuCreateAsync(GpuPreviewStreamRegistry registry, IDisposable reservation,
        BackendPreviewPinnedInput input, GpuStreamWorkerSession? worker)
    {
        if (worker is not null)
        {
            try { await worker.DisposeAsync().ConfigureAwait(false); }
            catch { registry.Quarantine(reservation, input, worker); throw; }
        }
        input.RetentionLease?.Dispose();
    }

    private static async Task RollbackGpuChangeAsync(GpuPreviewStreamRegistry registry, string client, string id,
        BackendPreviewPinnedInput input, GpuStreamWorkerSession? worker)
    {
        if (worker is not null)
        {
            try { await worker.DisposeAsync().ConfigureAwait(false); }
            catch { registry.QuarantineChange(client, id, input, worker); throw; }
        }
        input.RetentionLease?.Dispose();
    }
}
