using System.Diagnostics;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Processes;
using MakoKaraGenerator.Scripting.Compilation;

namespace MakoKaraGenerator.Backend.Hosting;

public sealed partial class WorkerProcessProxy
{
    private Process? _gpuWarmupProcess;
    /// <summary>仅私有诊断使用的原进程 PID/startTicks/组身份；不由可执行文件名推断所有权。</summary>
    public ProcessIdentity? GpuWarmupProcessIdentity { get; private set; }
    public GpuWorkerWarmupResult? GpuWarmup { get; private set; }

    /// <summary>未绑定业务输入时建立完整准备；此后代理不得透明重启成另一个业务进程。</summary>
    public async Task<GpuWorkerWarmupResult> WarmGpuAsync(CancellationToken token = default)
    {
        if (_options.RenderBackend != "gpu" || !_options.EnableGpuPreview)
            throw new InvalidOperationException("GPU 预热只允许显式 GPU preview Worker。");
        await GetPackageCapabilitiesCoreAsync(TimeSpan.FromSeconds(20), validation: false,
            token, runtimeInput: true).ConfigureAwait(false);
        Process process;
        ProcessIdentity identity;
        await _lifecycleGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ProcessIdentity? ownedIdentity = TryGetOwnedProcessIdentity();
            if (_packageSessionProcess is not null || _gpuWarmupProcess is not null ||
                _process is null || _process.HasExited || ownedIdentity is null ||
                !ReferenceEquals(_process, _runtimeInputVerifiedProcess))
                throw new WorkerProtocolException("Worker 不是未绑定业务输入的原进程。", GpuWorkerWarmupContract.FailureCode);
            process = _process; identity = ownedIdentity;
        }
        finally { _lifecycleGate.Release(); }
        var referenceRoot = WorkerReferenceDirectory();
        var references = ScriptReferenceIdentity.Read(referenceRoot);
        var expected = new GpuWorkerWarmupIdentity(1, references.Identity, references.Count,
            references.SdkIdentity, ScriptCompilationIdentity.CurrentEngineIdentity);
        byte[] payload = GpuWorkerWarmupContract.EncodeRequest(expected);
        var request = new WorkerMessage(GpuWorkerWarmupContract.RequestKind, Guid.NewGuid().ToString("N"),
            WorkerProtocol.ComputeSha256(payload), payload);
        GpuWorkerWarmupResult? warmed = null;
        await InvokeTimedMessageAsync(request, GpuWorkerWarmupContract.ResultKind, TimeSpan.FromSeconds(20),
            response =>
            {
                if (response.OutputSha256 != WorkerProtocol.ComputeSha256(response.Payload))
                    throw new WorkerProtocolException("GPU 预热输出身份不匹配。", GpuWorkerWarmupContract.FailureCode);
                warmed = GpuWorkerWarmupContract.DecodeResult(response.Payload, expected);
                if (warmed.WorkerPid != identity.ProcessId)
                    throw new WorkerProtocolException("GPU 预热 PID 与原进程不一致。", GpuWorkerWarmupContract.FailureCode);
            }, token, expectedProcess: process,
            expectedProcessFailureCode: GpuWorkerWarmupContract.FailureCode,
            expectedProcessFailureMessage: "GPU 预热原进程已经改变。").ConfigureAwait(false);
        await _lifecycleGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (!ReferenceEquals(process, _process) || process.HasExited || TryGetOwnedProcessIdentity() != identity)
                throw new WorkerProtocolException("GPU 预热原进程已经改变。", GpuWorkerWarmupContract.FailureCode);
            _gpuWarmupProcess = process; GpuWarmupProcessIdentity = identity; GpuWarmup = warmed;
        }
        finally { _lifecycleGate.Release(); }
        return warmed!;
    }

    /// <summary>只向已经准备的原进程询问原设备状态；不启动进程、探测设备或执行作者代码。</summary>
    internal async Task<RenderBackendDeviceInfoDto> ReadGpuDeviceProofAsync(CancellationToken token = default)
    {
        Process process;
        ProcessIdentity identity;
        GpuWorkerWarmupResult expected;
        RenderBackendDeviceInfoDto device;
        await _lifecycleGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ProcessIdentity? ownedIdentity = TryGetOwnedProcessIdentity();
            if (_gpuWarmupProcess is null || !ReferenceEquals(_gpuWarmupProcess, _process) ||
                _process.HasExited || ownedIdentity is null || GpuWarmupProcessIdentity != ownedIdentity ||
                GpuWarmup is null || VerifiedRenderBackendDevice is null)
                throw new WorkerProtocolException("GPU 原进程没有存活的设备证明。", GpuWorkerWarmupContract.FailureCode);
            process = _process; identity = ownedIdentity; expected = GpuWarmup; device = VerifiedRenderBackendDevice;
        }
        finally { _lifecycleGate.Release(); }
        byte[] payload = GpuWorkerWarmupContract.EncodeRequest(expected.Identity);
        var request = new WorkerMessage(GpuWorkerWarmupContract.DeviceStateKind, Guid.NewGuid().ToString("N"),
            WorkerProtocol.ComputeSha256(payload), payload);
        await InvokeTimedMessageAsync(request, GpuWorkerWarmupContract.DeviceStateResultKind, TimeSpan.FromSeconds(20),
            response =>
            {
                if (response.OutputSha256 != WorkerProtocol.ComputeSha256(response.Payload))
                    throw new WorkerProtocolException("GPU 设备状态输出身份不匹配。", GpuWorkerWarmupContract.FailureCode);
                var proof = GpuWorkerWarmupContract.DecodeResult(response.Payload, expected.Identity);
                if (proof.WorkerPid != identity.ProcessId || proof.PreparationId != expected.PreparationId ||
                    proof.InitializationId != expected.InitializationId)
                    throw new WorkerProtocolException("GPU 设备状态不是原进程的原设备。", GpuWorkerWarmupContract.FailureCode);
            }, token, expectedProcess: process,
            expectedProcessFailureCode: GpuWorkerWarmupContract.FailureCode,
            expectedProcessFailureMessage: "GPU 设备状态原进程已经改变。").ConfigureAwait(false);
        await _lifecycleGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (!ReferenceEquals(process, _process) || process.HasExited || TryGetOwnedProcessIdentity() != identity)
                throw new WorkerProtocolException("GPU 设备状态原进程已经改变。", GpuWorkerWarmupContract.FailureCode);
            return device;
        }
        finally { _lifecycleGate.Release(); }
    }

    private ProcessIdentity? TryGetOwnedProcessIdentity()
    {
        if (_ownedGroup is not null) return _ownedGroup.Leader;
        if (_windowsGuard is not null && _guardHandshakeVerified)
        {
            try { _windowsGuard.VerifyIdentity(); }
            catch { return null; }
            var leader = _windowsGuard.Leader;
            return new ProcessIdentity(leader.ProcessId, unchecked((ulong)leader.CreationTimeFileTime), leader.ProcessId);
        }
        return null;
    }

    internal string WorkerReferenceDirectory()
    {
        string executable = _options.Arguments?.FirstOrDefault(argument => argument.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            ?? _options.FileName;
        return Path.Combine(Path.GetDirectoryName(Path.GetFullPath(executable))!, "script-refs", "net10.0");
    }
}
