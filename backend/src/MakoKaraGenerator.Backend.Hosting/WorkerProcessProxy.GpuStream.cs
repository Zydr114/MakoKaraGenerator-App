using System.Globalization;
using MakoKaraGenerator.Backend.Contracts;

namespace MakoKaraGenerator.Backend.Hosting;

public sealed partial class WorkerProcessProxy
{
    /// <summary>在同一固定 Runtime 进程中初始化真实 GPU 池。</summary>
    public async Task<GpuStreamWorkerOpened> OpenGpuStreamAsync(StaticWorkerSessionHandle session,
        GpuStreamWorkerOpen input, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(input);
        byte[] payload = GpuStreamWorkerContract.Encode(input);
        WorkerMessage request = new(GpuStreamWorkerContract.OpenKind, Guid.NewGuid().ToString("N"),
            WorkerProtocol.ComputeSha256(payload), payload, sessionId: session.SessionId.ToString("N"),
            sourceSha256: session.SourceSha256, frameSequence: 1);
        GpuStreamWorkerOpened? result = null;
        await InvokeTimedMessageAsync(request, GpuStreamWorkerContract.OpenResultKind, timeout, response =>
        {
            if (response.Error is not null || response.ErrorCode is not null)
            {
                if (!string.IsNullOrWhiteSpace(response.ErrorCode))
                    throw new WorkerProtocolException(response.Error ?? "连续预览流 Worker 初始化失败。", response.ErrorCode);
                throw InvalidGpuStream();
            }
            if (response.OutputSha256 is null || response.Cancelled)
                throw InvalidGpuStream();
            result = GpuStreamWorkerContract.Decode<GpuStreamWorkerOpened>(response.Payload);
            bool isShm = string.Equals(result.Transport, "shm", StringComparison.Ordinal);
            bool validUuid = result.DeviceUuid is { Length: 32 } &&
                result.DeviceUuid.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
            bool validTransport = result.Transport is null or "dma-buf" or "shm";
            bool validApi = result.BackendApi is null or "vulkan" or "cpu" or "metal";
            bool matchesInputTransport = input.Transport is null || string.Equals(result.Transport, input.Transport, StringComparison.Ordinal);
            bool matchesInputApi = input.BackendApi is null || string.Equals(result.BackendApi, input.BackendApi, StringComparison.Ordinal);
            bool validShmPayload = !isShm || (
                result.PixelFormat is "bgra8888" or "rgba8888" &&
                result.StrideBytes >= (uint)(result.Width * 4) &&
                result.StrideBytes % 4 == 0 &&
                !string.IsNullOrWhiteSpace(result.ShmResourceName));
            if (result.StreamId != input.StreamId || result.WorkerPid != ProcessId || result.Width != input.Width ||
                result.Height != input.Height || result.SlotCount != 3 || result.AllocationBytes == 0 ||
                result.AllocationBytes > input.MaximumAllocationBytes || !CanonicalUInt64(result.ProducerEpoch) ||
                !CanonicalUInt64(result.PoolGeneration) || !validTransport || !validApi ||
                !matchesInputTransport || !matchesInputApi || !validShmPayload ||
                (!isShm && !validUuid) || (isShm && result.DeviceUuid.Length != 0 && !validUuid))
                throw InvalidGpuStream();
        }, cancellationToken, requirePackageRuntimeGuards: true).ConfigureAwait(false);
        if (GpuWarmup is { } prepared && (result!.WarmupBinding is not { OriginalDeviceBound: true,
            DeviceInitializations: 1, ContextInitializations: 1 } binding ||
            binding.PreparationId != prepared.PreparationId || binding.InitializationId != prepared.InitializationId))
            throw InvalidGpuStream();
        return result!;
    }

    /// <summary>要求 Worker 停止生产并确认所有槽位已排空。</summary>
    public async Task CloseGpuStreamAsync(StaticWorkerSessionHandle session, GpuStreamWorkerClose input,
        long sequence, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(input);
        byte[] payload = GpuStreamWorkerContract.Encode(input);
        WorkerMessage request = new(GpuStreamWorkerContract.CloseKind, Guid.NewGuid().ToString("N"),
            WorkerProtocol.ComputeSha256(payload), payload, sessionId: session.SessionId.ToString("N"),
            sourceSha256: session.SourceSha256, frameSequence: sequence);
        await InvokeTimedMessageAsync(request, GpuStreamWorkerContract.CloseResultKind, timeout, response =>
        {
            if (response.OutputSha256 is null || response.Error is not null || response.ErrorCode is not null || response.Cancelled)
                throw InvalidGpuStream();
            var closed = GpuStreamWorkerContract.Decode<GpuStreamWorkerClosed>(response.Payload);
            if (!closed.Drained || closed.StreamId != input.StreamId || closed.ProducerEpoch != input.ProducerEpoch)
                throw InvalidGpuStream();
        }, cancellationToken, requirePackageRuntimeGuards: true).ConfigureAwait(false);
    }

    /// <summary>读取同一 Worker 的运行状态。</summary>
    public async Task<GpuStreamWorkerState> ReadGpuStreamAsync(StaticWorkerSessionHandle session,
        GpuStreamWorkerRead input, long sequence, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        byte[] payload = GpuStreamWorkerContract.Encode(input);
        WorkerMessage request = new(GpuStreamWorkerContract.ReadKind, Guid.NewGuid().ToString("N"),
            WorkerProtocol.ComputeSha256(payload), payload, sessionId: session.SessionId.ToString("N"),
            sourceSha256: session.SourceSha256, frameSequence: sequence);
        GpuStreamWorkerState? result = null;
        await InvokeTimedMessageAsync(request, GpuStreamWorkerContract.ReadResultKind, timeout, response =>
        {
            if (response.OutputSha256 is null || response.Error is not null || response.ErrorCode is not null || response.Cancelled)
                throw InvalidGpuStream();
            result = GpuStreamWorkerContract.Decode<GpuStreamWorkerState>(response.Payload);
            if (result.State is not ("opening" or "ready" or "playing" or "paused" or "draining" or "stopped" or "failed") ||
                result.Mode is not ("playing" or "paused") || !CanonicalNonnegativeUInt64(result.TimelineEpoch) ||
                result.LastAppliedControlSeq is < 0 or > 9007199254740991 ||
                result.EffectiveMediaTimeUs is < 0 or > 9007199254740991 || result.TimelineResetId is null)
                throw InvalidGpuStream();
        }, cancellationToken, requirePackageRuntimeGuards: true).ConfigureAwait(false);
        return result!;
    }

    private static bool CanonicalUInt64(string value) => ulong.TryParse(value, NumberStyles.None,
        CultureInfo.InvariantCulture, out ulong parsed) && parsed != 0 && parsed.ToString(CultureInfo.InvariantCulture) == value;

    private static bool CanonicalNonnegativeUInt64(string value) => ulong.TryParse(value, NumberStyles.None,
        CultureInfo.InvariantCulture, out ulong parsed) && parsed.ToString(CultureInfo.InvariantCulture) == value;

    private static WorkerProtocolException InvalidGpuStream() =>
        new("GPU 流 Worker 回执无效。", "WorkerGpuStreamProtocolInvalid");
}
