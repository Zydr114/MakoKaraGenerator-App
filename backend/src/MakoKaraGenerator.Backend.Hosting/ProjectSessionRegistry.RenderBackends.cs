using MakoKaraGenerator.Backend.Contracts;

namespace MakoKaraGenerator.Backend.Hosting;

public sealed partial class ProjectSessionRegistry
{
    private BackendRenderBackendConfiguration? _renderBackends;

    /// <summary>固定服务启动时的默认后端及隔离设备检查服务。</summary>
    public void ConfigureRenderBackends(BackendRenderBackendConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (_renderBackends is not null) throw new InvalidOperationException("渲染后端只配置一次。");
        _renderBackends = configuration;
    }

    private sealed record TaskRenderSelection<TExecutor>(TExecutor Executor, string? RenderBackend);

    private async Task<BackendOperationResult<TaskRenderSelection<TExecutor>>> SelectTaskRenderBackendAsync<TExecutor>(
        TExecutor executor, string? requested, CancellationToken cancellationToken = default) where TExecutor : class
    {
        if (requested is not (null or "cpu" or "gpu"))
            return BackendOperationResult<TaskRenderSelection<TExecutor>>.Rejected(InvalidRenderBackend());
        // Trusted legacy adapters have no device declaration. Preserve historical unknown values;
        // explicit public selection always requires the configured provider and a bindable executor.
        if (_renderBackends is null)
            return requested is null
                ? BackendOperationResult<TaskRenderSelection<TExecutor>>.Success(new(executor, null))
                : BackendOperationResult<TaskRenderSelection<TExecutor>>.Rejected(RenderBackendUnavailable());
        string backend = _renderBackends.Resolve(requested);
        if (executor is not IBackendRenderBackendExecutor<TExecutor> selectable)
            return BackendOperationResult<TaskRenderSelection<TExecutor>>.Rejected(RenderBackendUnavailable());
        BackendErrorResponse? failure = await _renderBackends.VerifyAsync(backend, cancellationToken).ConfigureAwait(false);
        if (failure is not null) return BackendOperationResult<TaskRenderSelection<TExecutor>>.Rejected(failure);
        return BackendOperationResult<TaskRenderSelection<TExecutor>>.Success(new(selectable.WithRenderBackend(backend), backend));
    }

    private static BackendErrorResponse InvalidRenderBackend() =>
        new(BackendErrorCode.InvalidRequest, "renderBackend必须为cpu、gpu或null。", "renderBackend");

    private static BackendErrorResponse RenderBackendUnavailable() =>
        new(BackendErrorCode.TaskUnavailable, "当前部署没有可固定此渲染后端的执行器。", "renderBackend",
            FailureCode: "RenderBackendUnavailable");

    private async Task<BackendOperationResult<TaskRenderSelection<WorkerProcessOptions>>> SelectMeasurementRenderBackendAsync(
        string? requested, string? fallback, CancellationToken token)
    {
        if (requested is not (null or "cpu" or "gpu"))
            return BackendOperationResult<TaskRenderSelection<WorkerProcessOptions>>.Rejected(InvalidRenderBackend());
        if (_sceneMeasurementWorker is null)
            return BackendOperationResult<TaskRenderSelection<WorkerProcessOptions>>.Rejected(MeasurementUnavailable());
        if (_renderBackends is null)
            // In-process test/legacy adapters predate public device binding. An omitted
            // request stays historically unknown even when a pinned candidate carries
            // its newer default (usually cpu). Any explicit public choice still requires
            // the configured provider and is rejected above this compatibility seam.
            return requested is null
                ? BackendOperationResult<TaskRenderSelection<WorkerProcessOptions>>.Success(new(_sceneMeasurementWorker, null))
                : BackendOperationResult<TaskRenderSelection<WorkerProcessOptions>>.Rejected(RenderBackendUnavailable());
        string backend = _renderBackends.Resolve(requested, fallback);
        BackendErrorResponse? failure = await _renderBackends.VerifyAsync(backend, token).ConfigureAwait(false);
        if (failure is not null) return BackendOperationResult<TaskRenderSelection<WorkerProcessOptions>>.Rejected(failure);
        return BackendOperationResult<TaskRenderSelection<WorkerProcessOptions>>.Success(
            new(_sceneMeasurementWorker with { RenderBackend = backend }, backend));
    }
}
