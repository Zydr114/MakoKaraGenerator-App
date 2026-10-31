using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Rendering;
using MakoKaraGenerator.Runtime.Hosting;

namespace MakoKaraGenerator.Backend.Worker;

/// <summary>只检查进程入口已经固定的设备选择；不会修改环境或执行业务来源。</summary>
internal static class RenderBackendVerificationTask
{
    internal static WorkerMessage Execute(WorkerMessage request, string fixedBackend, CancellationToken cancellationToken)
    {
        string expected = WorkerRenderBackendContract.DecodeRequest(request);
        if (WorkerRenderBackendContract.EnvironmentValue(expected) != fixedBackend)
            throw new WorkerProtocolException("Worker启动选择与请求固定选择不同。", WorkerRenderBackendContract.MismatchCode);
        RuntimeRenderBackendProbeInfo info = RuntimeRenderBackendProbe.Probe(fixedBackend, cancellationToken);
        if (info.Backend != fixedBackend)
            throw new WorkerProtocolException("实际设备与Worker启动选择不同。", WorkerRenderBackendContract.MismatchCode);
        byte[] payload = WorkerRenderBackendContract.EncodeResult(expected, new(info.Vendor, info.Device, info.Driver, info.ApiVersion));
        return new(WorkerRenderBackendContract.ResultKind, request.RequestId, request.InputSha256, payload,
            outputSha256: WorkerProtocol.ComputeSha256(payload));
    }
}
