using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Runtime.Hosting;
using MakoKaraGenerator.Scripting.Compilation;

namespace MakoKaraGenerator.Backend.Worker;

/// <summary>设备检查和预热协商共享原对象；程序退出时释放尚未绑定的准备结果。</summary>
internal sealed class GpuWorkerPreparationState(bool enabled) : IDisposable
{
    private readonly object _gate = new();
    private RuntimeGpuPreparation? _prepared;
    private bool _businessBound;
    internal RuntimeGpuPreparation? Prepared { get { lock (_gate) return _prepared; } }

    internal RuntimeRenderBackendProbeInfo Verify(string backend, CancellationToken token)
    {
        if (!enabled || backend != "vulkan") return RuntimeRenderBackendProbe.Probe(backend, token);
        lock (_gate)
        {
            if (_businessBound) throw new WorkerProtocolException("已绑定业务的 Worker 不可重新预热。", GpuWorkerWarmupContract.FailureCode);
            _prepared ??= RuntimeGpuPreparation.Create(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"), token);
            return _prepared.DeviceInfo;
        }
    }

    internal WorkerMessage Warm(WorkerMessage request, CancellationToken token)
    {
        if (request.InputSha256 != WorkerProtocol.ComputeSha256(request.Payload)) throw Invalid();
        var expected = GpuWorkerWarmupContract.DecodeRequest(request.Payload);
        _ = Verify("vulkan", token);
        lock (_gate)
        {
            if (!enabled || _businessBound || _prepared is null || _prepared.Consumed ||
                expected.ReferenceIdentity != _prepared.References.Identity || expected.ReferenceCount != _prepared.References.Count ||
                expected.SdkIdentity != _prepared.References.SdkIdentity || expected.EngineIdentity != ScriptCompilationIdentity.CurrentEngineIdentity)
                throw Invalid();
            var result = new GpuWorkerWarmupResult(expected, _prepared.PreparationId, Environment.ProcessId,
                "vulkan", true, _prepared.ReferenceBytes, [.. _prepared.RuntimeDependencies.Select(a =>
                    new GpuWorkerRuntimeAssembly(a.AssemblyIdentity, a.ModuleVersionId))],
                _prepared.Proof.InitializationId, _prepared.Proof.DeviceInitializations, _prepared.Proof.ContextInitializations);
            byte[] bytes = GpuWorkerWarmupContract.EncodeResult(result);
            return new(GpuWorkerWarmupContract.ResultKind, request.RequestId, request.InputSha256, bytes,
                outputSha256: WorkerProtocol.ComputeSha256(bytes));
        }
    }

    internal WorkerMessage ReadDevice(WorkerMessage request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (request.InputSha256 != WorkerProtocol.ComputeSha256(request.Payload)) throw Invalid();
        var expected = GpuWorkerWarmupContract.DecodeRequest(request.Payload);
        lock (_gate)
        {
            if (!enabled || _prepared is null || !_prepared.IsDeviceAlive ||
                expected.ReferenceIdentity != _prepared.References.Identity || expected.ReferenceCount != _prepared.References.Count ||
                expected.SdkIdentity != _prepared.References.SdkIdentity || expected.EngineIdentity != ScriptCompilationIdentity.CurrentEngineIdentity)
                throw Invalid();
            var proof = _prepared.Proof;
            var result = new GpuWorkerWarmupResult(expected, _prepared.PreparationId, Environment.ProcessId,
                "vulkan", true, _prepared.ReferenceBytes, [.. _prepared.RuntimeDependencies.Select(a =>
                    new GpuWorkerRuntimeAssembly(a.AssemblyIdentity, a.ModuleVersionId))],
                proof.InitializationId, proof.DeviceInitializations, proof.ContextInitializations);
            byte[] bytes = GpuWorkerWarmupContract.EncodeResult(result);
            return new(GpuWorkerWarmupContract.DeviceStateResultKind, request.RequestId, request.InputSha256, bytes,
                outputSha256: WorkerProtocol.ComputeSha256(bytes));
        }
    }

    // Must be called before parsing/opening the first business input. Any attempt,
    // including malformed/cancelled input, permanently consumes this process.
    internal RuntimeGpuPreparation? Bind()
    {
        lock (_gate)
        {
            if (!enabled) return null;
            if (_businessBound || _prepared is null || _prepared.Consumed) throw Invalid();
            _businessBound = true; return _prepared;
        }
    }

    internal GpuWorkerBindingProof BindingProof()
    {
        lock (_gate)
        {
            var proof = _prepared?.Proof;
            if (!_businessBound || proof is not { Consumed: true, OriginalDeviceBound: true,
                DeviceInitializations: 1, ContextInitializations: 1 }) throw Invalid();
            return new(proof.PreparationId, proof.InitializationId, proof.DeviceInitializations,
                proof.ContextInitializations, proof.OriginalDeviceBound);
        }
    }

    public void Dispose() { lock (_gate) _prepared?.Dispose(); }
    private static WorkerProtocolException Invalid() => new("GPU 预热结果不能绑定本次业务输入。", GpuWorkerWarmupContract.FailureCode);
}
