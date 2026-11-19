using System.Globalization;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Hosting;
using MakoKaraGenerator.Backend.Rendering;
using MakoKaraGenerator.Rendering;

namespace MakoKaraGenerator.Backend.Server;

internal sealed partial class PreviewBackendService
{
    private GpuPreviewStreamRegistry? _gpuStreams;
    private WorkerProcessOptions? _gpuWorkerOptions;
    private GpuWorkerPool? _gpuWorkerPool;
    private DesktopConsumerIdentity? _desktopConsumer;
    internal bool GpuStreamsConfigured => _gpuStreams is not null;

    internal async Task<bool> GpuStreamsAvailableAsync()
    {
        if (!GpuStreamsConfigured || (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows()) || _renderBackends is null)
            return false;

        PreviewTransportKind? configuredTransport;
        try
        {
            string? raw = Environment.GetEnvironmentVariable("MAKO_PREVIEW_TRANSPORT");
            configuredTransport = string.IsNullOrWhiteSpace(raw) ? null : PreviewWireConventions.ParseTransport(raw.Trim());
        }
        catch (GpuPreviewException)
        {
            return false;
        }

        if (configuredTransport == PreviewTransportKind.DmaBuf)
        {
            if (!OperatingSystem.IsLinux()) return false;
            return await _renderBackends.VerifyAsync("gpu", _shutdown.Token).ConfigureAwait(false) is null;
        }

        return await _renderBackends.VerifyAsync("gpu", _shutdown.Token).ConfigureAwait(false) is null
            || await _renderBackends.VerifyAsync("cpu", _shutdown.Token).ConfigureAwait(false) is null;
    }

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
                TryReleaseRetiredInput(_gpuStreams, client, id, worker, "abort_consumer");
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
            if (request.Api is not ("vulkan" or "cpu") || request.ConsumerId != _desktopConsumer.ConsumerId ||
                request.Size is null || request.Size.Width is < 64 or > 2560 || request.Size.Height is < 64 or > 1440 ||
                request.AlphaMode is not (PreviewAlphaMode.Opaque or PreviewAlphaMode.Straight))
                return Reject<GpuPreviewStreamState>(BackendErrorCode.InvalidRequest, "连续预览流参数无效。");
            PreviewTransportKind resolvedTransport;
            try
            {
                resolvedTransport = GpuStreamWorkerSession.ResolveEffectiveTransport(request.Api, transport: null);
            }
            catch (GpuPreviewException error)
            {
                return BackendOperationResult<GpuPreviewStreamState>.Rejected(new(
                    BackendErrorCode.CommandNotSupported, error.Message, "api", FailureCode: error.Code));
            }
            catch (PlatformNotSupportedException error)
            {
                return BackendOperationResult<GpuPreviewStreamState>.Rejected(new(
                    BackendErrorCode.PreviewUnavailable, error.Message, "api", FailureCode: "GpuPreviewUnsupportedPlatform"));
            }
            string renderBackend = request.Api == "cpu" ? "cpu" : "gpu";
            using IDisposable? reservation = _gpuStreams.TryReserve(client, request.ConsumerId);
            if (reservation is null) return Reject<GpuPreviewStreamState>(BackendErrorCode.SessionQuotaExceeded, "GPU 流配额已用完。");
            if (_renderBackends?.UsesWorkerOptions(_gpuWorkerOptions) != true)
            {
                var backend = await PrepareRenderBackendAsync(renderBackend).ConfigureAwait(false);
                if (!backend.Accepted) return BackendOperationResult<GpuPreviewStreamState>.Rejected(backend.Error!);
            }
            var capture = await CaptureAsync(client, request.Source, renderBackend,
                request.AlphaMode == PreviewAlphaMode.Straight).ConfigureAwait(false);
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
                    request.AlphaMode == PreviewAlphaMode.Straight, _desktopConsumer.MainPid,
                    transport: resolvedTransport, backendApi: request.Api, token: _shutdown.Token).ConfigureAwait(false);
                string effectiveTransport = worker.Opened.Transport ?? resolvedTransport.ToWireName();
                GpuPreviewStreamState state = new(streamId, "1", "1", "ready", input.Source, request.Size,
                    request.AlphaMode, request.Api, worker.Opened.ProducerEpoch, worker.Opened.PoolGeneration,
                    worker.Opened.SlotCount, checked((long)worker.Opened.AllocationBytes),
                    Transport: effectiveTransport);
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
                Console.Error.WriteLine(
                    $"BACKEND_GPU_STREAM_CREATE_FAILED stage=open_cleanup type={failure.GetType().Name} inner={failure.InnerException?.GetType().Name ?? "none"}");
                _gpuStreams.QuarantineOpen(reservation, null, null, input, failure);
                quarantinedOpen = true;
                return BackendOperationResult<GpuPreviewStreamState>.Rejected(new(
                    BackendErrorCode.PreviewUnavailable, "GPU 流初始化失败且进程清理状态未知，已隔离该名额。",
                    FailureCode: "GpuStreamOpenQuarantined"));
            }
            catch (Exception error) when (TryMapGpuStreamException("CREATE", error, _shutdown.IsCancellationRequested, out BackendErrorResponse? mapped))
            {
                return BackendOperationResult<GpuPreviewStreamState>.Rejected(mapped!);
            }
            finally
            {
                if (!published && !quarantinedOpen)
                    await RollbackGpuCreateAsync(_gpuStreams, reservation, input, worker).ConfigureAwait(false);
            }
        }));

    internal Task<BackendOperationResult<GpuPreviewStreamListResponse>> ReadGpuStreamsAsync(string client) =>
        MutateAsync(client, () => Task.FromResult(BackendOperationResult<GpuPreviewStreamListResponse>.Success(
            new(_gpuStreams?.List(client) ?? []))));

    internal Task<BackendOperationResult<GpuPreviewStreamState>> ReadGpuStreamAsync(string client, string id) =>
        MutateAsync(client, async () =>
        {
            GpuPreviewStreamState? state = _gpuStreams?.Read(client, id);
            if (state is null) return Reject<GpuPreviewStreamState>(BackendErrorCode.SessionNotFound, "GPU 流不存在。");
            if (state.RegistrationState == "failed" || state.Quarantined == true)
                return BackendOperationResult<GpuPreviewStreamState>.Success(state);
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
                    TryReleaseRetiredInput(_gpuStreams, client, id, worker, "read_failed_worker");
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
        () => MutateAsync(client, async () =>
        {
            try { return await CloseGpuStreamCoreAsync(client, id, request.ExpectedStreamRevision).ConfigureAwait(false); }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            { return Reject<GpuPreviewStreamState>(BackendErrorCode.SessionClosing, "预览服务正在关闭。"); }
            catch (Exception error) when (!_shutdown.IsCancellationRequested)
            {
                Console.Error.WriteLine(
                    $"BACKEND_GPU_STREAM_CLOSE_FAILED stage=http type={error.GetType().Name} inner={error.InnerException?.GetType().Name ?? "none"}");
                return BackendOperationResult<GpuPreviewStreamState>.Rejected(new(
                    BackendErrorCode.PreviewUnavailable, "GPU 流进程尚未确认退出，已保留隔离。",
                    FailureCode: "GpuStreamDrainFailed"));
            }
        }));

    private async Task<BackendOperationResult<GpuPreviewStreamState>> CloseGpuStreamCoreAsync(string client, string id, string revision)
    {
        if (_gpuStreams is null) return Reject<GpuPreviewStreamState>(BackendErrorCode.SessionNotFound, "GPU 流不存在。");
        if (_gpuStreams.IsQuarantined(client, id))
        {
            GpuPreviewStreamState? quarantinedState = _gpuStreams.Read(client, id);
            return BackendOperationResult<GpuPreviewStreamState>.Rejected(new(
                BackendErrorCode.PreviewUnavailable, "GPU 流已进入故障隔离，请重启应用后重试。",
                FailureCode: quarantinedState?.FailureCode ?? "GpuStreamQuarantined"));
        }
        if (!_gpuStreams.TryBeginChange(client, id, revision, out bool found))
            return Reject<GpuPreviewStreamState>(!found ? BackendErrorCode.SessionNotFound : BackendErrorCode.RevisionConflict,
                "GPU 流不存在或修订已改变。");
        var resources = _gpuStreams.GetForClose(client, id)!.Value;
        try
        {
            await resources.Worker.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(
                $"BACKEND_GPU_STREAM_CLOSE_FAILED stage=dispose_worker type={error.GetType().Name} inner={error.InnerException?.GetType().Name ?? "none"}");
            _gpuStreams.FailChange(client, id, resources.Worker, "GpuStreamDrainFailed");
            TryReleaseRetiredInput(_gpuStreams, client, id, resources.Worker, "close_drain_failed");
            throw;
        }

        // Worker 终止已确认；Windows 下固定输入目录短暂占用不得阻止 CompleteClose 归还配额。
        TryReleaseRetiredInput(_gpuStreams, client, id, resources.Worker, "close");
        if (!_gpuStreams.CompleteClose(client, id, resources.Worker))
        {
            if (_gpuStreams.IsQuarantined(client, id))
            {
                return BackendOperationResult<GpuPreviewStreamState>.Rejected(new(
                    BackendErrorCode.PreviewUnavailable, "GPU 流已进入故障隔离，请重启应用后重试。",
                    FailureCode: _gpuStreams.Read(client, id)?.FailureCode ?? "GpuStreamQuarantined"));
            }
            _gpuStreams.FailChange(client, id, resources.Worker, "GpuStreamQuarantined");
            return Reject<GpuPreviewStreamState>(BackendErrorCode.RevisionConflict, "GPU 流关闭状态已改变。");
        }
        return BackendOperationResult<GpuPreviewStreamState>.Success(resources.State with { RegistrationState = "closed" });
    }

    internal async Task DrainGpuProjectAsync(string client, string projectSessionId)
    {
        if (_gpuStreams is null || _stopping || _shutdown.IsCancellationRequested) return;
        await _membership.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_gpuStreams is null || _stopping || _shutdown.IsCancellationRequested) return;
            foreach (var (streamId, revision) in _gpuStreams.ClientStreamsForProject(client, projectSessionId))
            {
                if (_gpuStreams.IsConsumerIsolationComplete(client, streamId)) continue;
                try { await CloseGpuStreamCoreAsync(client, streamId, revision).ConfigureAwait(false); }
                catch (Exception error)
                {
                    Console.Error.WriteLine(
                        $"BACKEND_GPU_PROJECT_DRAIN_FAILED type={error.GetType().Name} inner={error.InnerException?.GetType().Name ?? "none"}");
                }
            }
        }
        finally { _membership.Release(); }
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
        if (old.Quarantined == true)
            return BackendOperationResult<GpuPreviewStreamState>.Rejected(new(
                BackendErrorCode.PreviewUnavailable, "GPU 流已进入故障隔离，请重启应用后重试。",
                FailureCode: old.FailureCode ?? "GpuStreamQuarantined"));
        if (old.RegistrationState == "failed") return Reject<GpuPreviewStreamState>(BackendErrorCode.PreviewUnavailable, "GPU 流已失败。");
        if (old.StreamRevision != expectedRevision)
            return Reject<GpuPreviewStreamState>(BackendErrorCode.RevisionConflict, "GPU 流修订已改变。");
        size ??= old.Size;
        if (size.Width is < 64 or > 2560 || size.Height is < 64 or > 1440 ||
            !long.TryParse(old.InputGeneration, NumberStyles.None, CultureInfo.InvariantCulture, out long generation) ||
            !long.TryParse(old.StreamRevision, NumberStyles.None, CultureInfo.InvariantCulture, out long revision) ||
            generation == long.MaxValue || revision == long.MaxValue)
            return Reject<GpuPreviewStreamState>(BackendErrorCode.InvalidRequest, "GPU 流尺寸或代次无效。");
        string renderBackend = old.Api == "cpu" ? "cpu" : "gpu";
        PreviewTransportKind resolvedTransport;
        try
        {
            resolvedTransport = GpuStreamWorkerSession.ResolveEffectiveTransport(old.Api, transport: null);
        }
        catch (GpuPreviewException error)
        {
            return BackendOperationResult<GpuPreviewStreamState>.Rejected(new(
                BackendErrorCode.CommandNotSupported, error.Message, "api", FailureCode: error.Code));
        }
        catch (PlatformNotSupportedException error)
        {
            return BackendOperationResult<GpuPreviewStreamState>.Rejected(new(
                BackendErrorCode.PreviewUnavailable, error.Message, "api", FailureCode: "GpuPreviewUnsupportedPlatform"));
        }
        var captured = await CaptureAsync(client, source ?? old.Source, renderBackend,
            old.AlphaMode == PreviewAlphaMode.Straight).ConfigureAwait(false);
        if (captured.Error is not null) return BackendOperationResult<GpuPreviewStreamState>.Rejected(captured.Error);
        BackendPreviewPinnedInput input = captured.Input!;
        GpuStreamWorkerSession? worker = null;
        GpuStreamWorkerSession? previous = null;
        bool published = false;
        bool begun = false;
        bool quarantinedOpen = false;
        string? changeFailureCode = null;
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
            try
            {
                await previous.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception error) when (!_shutdown.IsCancellationRequested)
            {
                Console.Error.WriteLine(
                    $"BACKEND_GPU_STREAM_CHANGE_FAILED stage=dispose_previous type={error.GetType().Name} inner={error.InnerException?.GetType().Name ?? "none"}");
                changeFailureCode = "GpuStreamDrainFailed";
                return BackendOperationResult<GpuPreviewStreamState>.Rejected(new(
                    BackendErrorCode.PreviewUnavailable, "旧预览渲染进程尚未确认退出，请稍后重试。", FailureCode: changeFailureCode));
            }
            string nextGeneration = (generation + 1).ToString(CultureInfo.InvariantCulture);
            worker = await GpuStreamWorkerSession.OpenAsync(_gpuWorkerPool!, input.PreparedInput, id,
                _clients.HostInstanceId, nextGeneration, size.Width, size.Height,
                old.AlphaMode == PreviewAlphaMode.Straight, _desktopConsumer.MainPid,
                transport: resolvedTransport, backendApi: old.Api, token: _shutdown.Token).ConfigureAwait(false);
            var next = old with { StreamRevision = (revision + 1).ToString(CultureInfo.InvariantCulture),
                InputGeneration = nextGeneration, RegistrationState = "ready", Source = input.Source, Size = size,
                ProducerEpoch = worker.Opened.ProducerEpoch, PoolGeneration = worker.Opened.PoolGeneration,
                SlotCount = worker.Opened.SlotCount, AllocationBytes = checked((long)worker.Opened.AllocationBytes),
                LastAppliedControlSeq = null, FailureCode = null,
                Transport = worker.Opened.Transport ?? resolvedTransport.ToWireName() };
            return await _projects.RegisterPreparedInputAsync(client, input, () =>
            {
                if (!_clients.TryReadLease(client, out _, out bool dormant) || dormant || _shutdown.IsCancellationRequested)
                    return Reject<GpuPreviewStreamState>(BackendErrorCode.ClientTokenExpired, "客户端租约已失效。");
                if (!_gpuStreams.CompleteChange(client, id, previous, next, input, worker))
                    return Reject<GpuPreviewStreamState>(BackendErrorCode.RevisionConflict, "GPU 流代次已改变。");
                published = true;
                TryDisposeRetentionLease(oldResources.Input.RetentionLease, "change_old_input");
                return BackendOperationResult<GpuPreviewStreamState>.Success(next);
            }).ConfigureAwait(false);
        }
        catch (GpuStreamOpenCleanupException failure)
        {
            Console.Error.WriteLine(
                $"BACKEND_GPU_STREAM_CHANGE_FAILED stage=open_cleanup type={failure.GetType().Name} inner={failure.InnerException?.GetType().Name ?? "none"}");
            _gpuStreams.QuarantineOpen(null, client, id, input, failure);
            quarantinedOpen = true;
            changeFailureCode = "GpuStreamOpenQuarantined";
            return BackendOperationResult<GpuPreviewStreamState>.Rejected(new(
                BackendErrorCode.PreviewUnavailable, "GPU 流切换失败且进程清理状态未知，已隔离该名额。", FailureCode: changeFailureCode));
        }
        catch (Exception error) when (TryMapGpuStreamException("CHANGE", error, _shutdown.IsCancellationRequested, out BackendErrorResponse? mapped))
        {
            changeFailureCode = mapped!.FailureCode ?? changeFailureCode;
            return BackendOperationResult<GpuPreviewStreamState>.Rejected(mapped!);
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
                        _gpuStreams.FailChange(client, id, previous, changeFailureCode ?? "GpuStreamChangeFailed");
                        TryReleaseRetiredInput(_gpuStreams, client, id, previous, "change_fail");
                    }
                }
            }
        }
    });

    private static Task RollbackGpuCreateAsync(GpuPreviewStreamRegistry registry, IDisposable reservation,
        BackendPreviewPinnedInput input, GpuStreamWorkerSession? worker) =>
        RollbackGpuWorkerAndInputAsync("create_rollback", input, worker, () => registry.Quarantine(reservation, input, worker!));

    private static Task RollbackGpuChangeAsync(GpuPreviewStreamRegistry registry, string client, string id,
        BackendPreviewPinnedInput input, GpuStreamWorkerSession? worker) =>
        RollbackGpuWorkerAndInputAsync("change_rollback", input, worker, () => registry.QuarantineChange(client, id, input, worker!));

    private static async Task RollbackGpuWorkerAndInputAsync(string stage, BackendPreviewPinnedInput input,
        GpuStreamWorkerSession? worker, Action quarantine)
    {
        if (worker is not null)
        {
            try { await worker.DisposeAsync().ConfigureAwait(false); }
            catch (Exception error)
            {
                Console.Error.WriteLine(
                    $"BACKEND_GPU_STREAM_ROLLBACK_QUARANTINED stage={stage} type={error.GetType().Name} inner={error.InnerException?.GetType().Name ?? "none"}");
                quarantine();
                return;
            }
        }
        TryDisposeRetentionLease(input.RetentionLease, stage);
    }

    private static void TryReleaseRetiredInput(GpuPreviewStreamRegistry registry, string client, string id,
        GpuStreamWorkerSession worker, string stage)
    {
        try { registry.ReleaseRetiredInput(client, id, worker); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"BACKEND_GPU_INPUT_CLEANUP_FAILED stage={stage} type={error.GetType().Name}");
        }
    }

    private static void TryDisposeRetentionLease(IDisposable? lease, string stage)
    {
        if (lease is null) return;
        try { lease.Dispose(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"BACKEND_GPU_INPUT_CLEANUP_FAILED stage={stage} type={error.GetType().Name}");
        }
    }

    private static bool TryMapGpuStreamException(string stage, Exception error, bool shuttingDown,
        out BackendErrorResponse? mapped)
    {
        if (error is OperationCanceledException && shuttingDown)
        {
            mapped = new(BackendErrorCode.SessionClosing, "预览服务正在关闭，已取消连续预览流操作。");
            return true;
        }
        if (error is ObjectDisposedException && shuttingDown)
        {
            mapped = new(BackendErrorCode.SessionClosing, "预览服务正在关闭，连续预览流工作池已释放。");
            return true;
        }
        if (error is OperationCanceledException)
        {
            Console.Error.WriteLine($"BACKEND_GPU_STREAM_{stage}_FAILED stage=timeout type={error.GetType().Name}");
            mapped = new(BackendErrorCode.PreviewUnavailable, "预览流初始化超时，请重试。",
                FailureCode: stage == "CHANGE" ? "GpuStreamChangeTimeout" : "GpuStreamOpenTimeout");
            return true;
        }
        if (error is WorkerProtocolException protocol)
        {
            Console.Error.WriteLine(
                $"BACKEND_GPU_STREAM_{stage}_FAILED stage=worker_protocol type={error.GetType().Name} code={protocol.Code}");
            mapped = stage == "CREATE" && protocol.Code is (WorkerRenderBackendContract.UnavailableCode or
                WorkerRenderBackendContract.UnsupportedCode or WorkerRenderBackendContract.MismatchCode or WorkerRenderBackendContract.InvalidCode)
                ? new(BackendErrorCode.TaskUnavailable, "所选渲染后端当前不可用。", "renderBackend", FailureCode: protocol.Code)
                : new(BackendErrorCode.PreviewUnavailable, "预览渲染进程初始化失败，请重试。", FailureCode: protocol.Code);
            return true;
        }
        if (error is System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            mapped = new(BackendErrorCode.TaskUnavailable, "所选渲染后端当前不可用。", "renderBackend",
                FailureCode: WorkerRenderBackendContract.UnavailableCode);
            return true;
        }
        if (error is GpuPreviewException preview)
        {
            Console.Error.WriteLine(
                $"BACKEND_GPU_STREAM_{stage}_FAILED stage=preview type={error.GetType().Name} code={preview.Code}");
            mapped = new(BackendErrorCode.PreviewUnavailable, preview.Message, FailureCode: preview.Code);
            return true;
        }
        if (error is InvalidOperationException)
        {
            Console.Error.WriteLine($"BACKEND_GPU_STREAM_{stage}_FAILED stage=pool_or_state type={error.GetType().Name}");
            mapped = new(BackendErrorCode.PreviewUnavailable, "GPU 渲染进程忙碌或正在退避，请稍后重试。", FailureCode: "GpuWorkerPoolBusy");
            return true;
        }
        if (error is IOException)
        {
            Console.Error.WriteLine($"BACKEND_GPU_STREAM_{stage}_FAILED stage=io type={error.GetType().Name}");
            mapped = new(BackendErrorCode.PreviewUnavailable, "预览输入或通道准备失败，请重试。", FailureCode: "GpuStreamIoFailed");
            return true;
        }
        mapped = null;
        return false;
    }
}
