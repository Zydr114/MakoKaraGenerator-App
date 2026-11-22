using System.Diagnostics;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Rendering;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Scripting;
using MakoKaraGenerator.Processes;
using MakoKaraGenerator.Scripting.Compilation;

namespace MakoKaraGenerator.Backend.Hosting;

internal sealed class WorkerCooperativeCancellationException(CancellationToken token) : OperationCanceledException(token);
internal sealed class WorkerInvocationNotStartedException(CancellationToken token) : OperationCanceledException(token);

/// <summary>启动 Worker 时使用的参数。每个参数通过 <see cref="ProcessStartInfo.ArgumentList"/> 传递。</summary>
public sealed record WorkerProcessOptions
{
    public WorkerProcessOptions(string fileName, IReadOnlyList<string>? arguments = null, TimeSpan? cancellationGracePeriod = null)
    {
        FileName = fileName;
        Arguments = arguments;
        CancellationGracePeriod = cancellationGracePeriod ?? TimeSpan.FromSeconds(2);
    }

    public string FileName { get; init; }
    public IReadOnlyList<string>? Arguments { get; init; }
    public TimeSpan CancellationGracePeriod { get; init; }
    public long? ExportMediaFileSizeLimitBytes { get; init; }
    /// <summary>固定公开选择；null保留非渲染Worker兼容，cpu/gpu必须在实际进程内核验。</summary>
    public string? RenderBackend { get; init; }
    /// <summary>仅 GPU 流 Worker 允许创建同设备预览池。</summary>
    public bool EnableGpuPreview { get; init; }
    /// <summary>仅显式基准启动使用的合成生产者开关。</summary>
    public bool EnableGpuPreviewBenchmark { get; init; }
    /// <summary>Host 内共享编译服务；不序列化或传入 Worker。</summary>
    public IScriptCompilationService? ScriptCompilationService { get; init; }

    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(FileName);
        if (CancellationGracePeriod <= TimeSpan.Zero || CancellationGracePeriod > TimeSpan.FromMinutes(1))
            throw new ArgumentOutOfRangeException(nameof(CancellationGracePeriod));
        if (Arguments is not null && Arguments.Any(argument => argument is null))
            throw new ArgumentException("Worker 启动参数不能包含 null。", nameof(Arguments));
        if (ExportMediaFileSizeLimitBytes is <= 0)
            throw new ArgumentOutOfRangeException(nameof(ExportMediaFileSizeLimitBytes));
        if (RenderBackend is not null && !WorkerRenderBackendContract.IsSupported(RenderBackend))
            throw new ArgumentException("Worker渲染后端必须为cpu或gpu。", nameof(RenderBackend));
        if (EnableGpuPreview && RenderBackend is not ("gpu" or "cpu"))
            throw new ArgumentException("连续预览流要求显式指定 gpu 或 cpu 渲染后端。", nameof(EnableGpuPreview));
        if (EnableGpuPreviewBenchmark && !EnableGpuPreview)
            throw new ArgumentException("GPU 基准要求 GPU 预览。", nameof(EnableGpuPreviewBenchmark));
    }
}

/// <summary>Worker 调用的专用输入合同；payload 不代表工程保存文件。</summary>
public sealed record WorkerInvocation(
    string Kind,
    byte[] Payload,
    int DelayMilliseconds = 0,
    string? RequestId = null);

/// <summary>Worker 调用的校验结果。</summary>
public sealed record WorkerInvocationResult(
    string RequestId,
    byte[] Payload,
    string InputSha256,
    bool Cancelled = false);

/// <summary>跨进程 render-frame-v1 的受验证结果；调用者拥有返回像素数组。</summary>
public sealed record WorkerRenderFrameResult(
    string RequestId,
    byte[] Pixels,
    string InputSha256,
    string OutputSha256,
    int Width,
    int Height,
    int Stride,
    long TimeUs,
    string PixelFormat);

/// <summary>
/// Hosting 持有的单 Worker 代理。协议消息有长度上限，子进程退出或取消超时会被回收。
/// 固定背景帧和 snapshot-v1 静态歌词／场景持久会话分离；完整资源和脚本闭包尚未接入。
/// </summary>
public sealed partial class WorkerProcessProxy : IAsyncDisposable
{
    private static bool IsPackageCapabilitiesKind(string kind) => kind is PackageRenderWorkerContract.CapabilitiesKind
        or PackageRenderWorkerContract.ValidationCapabilitiesKind
        or PackageResourceWorkerContract.CapabilitiesKind or ProjectImagePreviewWorkerContract.CapabilitiesKind
        or ProjectImageValidationWorkerContract.CapabilitiesKind or ProjectRuntimeInputWorkerContract.CapabilitiesKind
        or RuntimeValidationWorkerContract.CapabilitiesKind;
    private static bool IsPackageRenderCapabilitiesKind(string kind) => kind is PackageRenderWorkerContract.CapabilitiesKind;
    private static bool IsPackageValidationCapabilitiesKind(string kind) => kind is PackageRenderWorkerContract.ValidationCapabilitiesKind;
    private static bool IsImageValidationCapabilitiesKind(string kind) => kind is ProjectImageValidationWorkerContract.CapabilitiesKind;
    private static bool IsPackageResourceKind(string kind) => kind is PackageResourceWorkerContract.CapabilitiesKind
        or PackageResourceWorkerContract.ValidateKind;
    private static bool IsPackageSessionKind(string kind) => kind is PackageRenderWorkerContract.RenderKind
        or PackageRenderWorkerContract.CloseKind or GpuStreamWorkerContract.OpenKind or GpuStreamWorkerContract.CloseKind
        or GpuStreamWorkerContract.ReadKind;
    private Process? _runtimeInputVerifiedProcess;
    private Process? VerifiedPackageProcess(string kind) => IsPackageResourceKind(kind)
        ? _packageResourceVerifiedProcess
        : kind is RuntimeValidationWorkerContract.CapabilitiesKind or RuntimeValidationWorkerContract.RequestKind
            ? _runtimeValidationVerifiedProcess
        : IsPackageValidationCapabilitiesKind(kind) || kind == WorkerValidationContract.RequestKind
            ? _packageValidationVerifiedProcess
            : kind is ProjectImageValidationWorkerContract.CapabilitiesKind or ProjectImageValidationWorkerContract.RequestKind
                ? _imageValidationVerifiedProcess
            : kind is ProjectRuntimeInputWorkerContract.CapabilitiesKind or ProjectRuntimeInputWorkerContract.OpenKind
                or WorkerSceneMeasurementContract.CapabilitiesKind or WorkerSceneMeasurementContract.RequestKind
                || IsPackageSessionKind(kind) && _packageSessionOpenKind == ProjectRuntimeInputWorkerContract.OpenKind
                ? _runtimeInputVerifiedProcess
            : kind is ProjectImagePreviewWorkerContract.CapabilitiesKind or ProjectImagePreviewWorkerContract.OpenKind
                || IsPackageSessionKind(kind) && _packageSessionOpenKind == ProjectImagePreviewWorkerContract.OpenKind
                ? _imagePreviewVerifiedProcess
                : IsPackageRenderCapabilitiesKind(kind) || kind == PackageRenderWorkerContract.OpenKind || IsPackageSessionKind(kind)
                    ? _packageRenderVerifiedProcess
                    : null;
    private readonly WorkerProcessOptions _options;
    private readonly object _stateGate = new();
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly SemaphoreSlim _callGate = new(1, 1);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private Process? _process;
    private Stream? _input;
    private Stream? _output;
    private StreamReader? _ownedStderrReader;
    private Task? _stderrDrain;
    private OwnedProcessGroup? _ownedGroup;
    private OwnedProcessTree? _ownedTree;
    private WindowsJobObjectGuard? _windowsGuard;
    private bool _guardHandshakeVerified;
    private bool _childReapingEnabled;
    private long? _actualMediaFileSizeLimit;
    private Process? _packageRenderVerifiedProcess;
    private Process? _packageValidationVerifiedProcess;
    private Process? _imageValidationVerifiedProcess;
    private Process? _runtimeValidationVerifiedProcess;
    private Process? _packageResourceVerifiedProcess;
    private Process? _packageSessionProcess;
    private Process? _imagePreviewVerifiedProcess;
    private string? _packageSessionOpenKind;
    private bool _disposed;
    private Task? _disposeTask;
    private CancellationTokenSource? _activeCallCancellation;

    public WorkerProcessProxy(WorkerProcessOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
    }

    /// <summary>当前子进程 ID；未启动时为 null。</summary>
    public int? ProcessId => _process is { HasExited: false } process ? process.Id : null;
    /// <summary>当前实际Worker启动时的设备检查；不代表业务会话已创建或产帧。</summary>
    public RenderBackendDeviceInfoDto? VerifiedRenderBackendDevice { get; private set; }

    private bool HasVerifiedPrivateGuard => _childReapingEnabled
        && (_ownedGroup is not null || _windowsGuard is not null && _guardHandshakeVerified);

    public bool SupportsExportGuards => HasVerifiedPrivateGuard
        && _actualMediaFileSizeLimit == _options.ExportMediaFileSizeLimitBytes;

    internal void RequireExportGuards()
    {
        if (!SupportsExportGuards) throw new WorkerProtocolException("Export requires a verified private Worker process guard and media file limit.", "WorkerExportUnavailable");
    }

    private void ThrowIfDisposed()
    {
        lock (_stateGate) ObjectDisposedException.ThrowIf(_disposed, this);
    }

    /// <summary>启动并完成协议握手。</summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (_process is { HasExited: false }) return;
            if (_gpuWarmupProcess is not null)
                throw new WorkerProtocolException("预热 GPU Worker 已终止，禁止透明重启复用其业务名额。", GpuWorkerWarmupContract.FailureCode);
            if (_process is not null) await StopProcessAsync(CancellationToken.None).ConfigureAwait(false);
            ProcessStartInfo start = new()
            {
                FileName = _options.FileName,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            RemoveDesktopCredentials(start);
            ApplyWorkerGcBudget(start);
            start.Environment.Remove("MAKO_BACKEND_GPU_PREVIEW");
            start.Environment.Remove("MAKO_BACKEND_GPU_PREVIEW_BENCHMARK");
            if (_options.EnableGpuPreview) start.Environment["MAKO_BACKEND_GPU_PREVIEW"] = "1";
            if (_options.EnableGpuPreviewBenchmark) start.Environment["MAKO_BACKEND_GPU_PREVIEW_BENCHMARK"] = "1";
            foreach (string argument in _options.Arguments ?? []) start.ArgumentList.Add(argument);
            if (_options.RenderBackend is { } selectedBackend)
                start.Environment["MAKO_RENDER_BACKEND"] = WorkerRenderBackendContract.EnvironmentValue(selectedBackend);
            start.Environment.Remove("MAKO_BACKEND_WORKER_MEDIA_LIMIT_BYTES");
            _childReapingEnabled = ProcessGuards.TryEnableChildReaping();
            if (_options.ExportMediaFileSizeLimitBytes is { } maximum)
            {
                if (!_childReapingEnabled) throw new WorkerProtocolException("Export process guards are unavailable.", "WorkerExportUnavailable");
                start.Environment["MAKO_BACKEND_WORKER_MEDIA_LIMIT_BYTES"] = maximum.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            _guardHandshakeVerified = false;
            Process process;
            if (WindowsJobObjectGuard.IsSupported)
            {
                WindowsControlledProcess controlled = WindowsJobObjectGuard.StartProcess(start);
                _windowsGuard = controlled.Guard;
                process = controlled.ManagedProcess;
                _process = process;
                _input = controlled.StandardInput;
                _output = controlled.StandardOutput;
                _ownedStderrReader = controlled.StandardError;
                _stderrDrain = WorkerTelemetryDrain.DrainAsync(controlled.StandardError, process.Id);
            }
            else
            {
                process = new() { StartInfo = start, EnableRaisingEvents = true };
                if (!process.Start()) throw new InvalidOperationException("Worker 进程启动失败。");
                _process = process;
                _ownedTree = OwnedProcessTree.TryCapture(process);
                _input = process.StandardInput.BaseStream;
                _output = process.StandardOutput.BaseStream;
                _stderrDrain = WorkerTelemetryDrain.DrainAsync(process.StandardError, process.Id);
            }
            try
            {
                WorkerMessage hello = new("hello", Guid.NewGuid().ToString("N"));
                await WriteAsync(hello, cancellationToken).ConfigureAwait(false);
                WorkerMessage response = await ReadRequiredAsync(cancellationToken).ConfigureAwait(false);
                if (response.Kind != "hello" || response.RequestId != hello.RequestId)
                    throw new WorkerProtocolException("Worker 握手回执不匹配。");
                if (response.ProcessGroupId is { } group)
                {
                    if (_windowsGuard is not null)
                    {
                        _windowsGuard.VerifyIdentity();
                        if (group != _windowsGuard.LeaderProcessId)
                            throw new WorkerProtocolException("Worker private Job guard identity does not match the started process.");
                        _guardHandshakeVerified = true;
                    }
                    else
                    {
                        _ownedGroup = OwnedProcessGroup.TryCapture(process);
                        if (_ownedGroup is null || group != _ownedGroup.Leader.ProcessGroupId)
                            throw new WorkerProtocolException("Worker private group identity does not match the started process.");
                        _guardHandshakeVerified = true;
                    }
                }
                _actualMediaFileSizeLimit = response.MediaFileSizeLimitBytes;
                if (_options.ExportMediaFileSizeLimitBytes is not null) RequireExportGuards();
                if (_options.RenderBackend is { } backend)
                {
                    using var verificationDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    verificationDeadline.CancelAfter(TimeSpan.FromSeconds(20));
                    byte[] payload = WorkerRenderBackendContract.EncodeRequest(backend);
                    WorkerMessage verification = new(WorkerRenderBackendContract.RequestKind, Guid.NewGuid().ToString("N"),
                        WorkerProtocol.ComputeSha256(payload), payload);
                    await WriteAsync(verification, verificationDeadline.Token).ConfigureAwait(false);
                    WorkerMessage verified = await ReadRequiredAsync(verificationDeadline.Token).ConfigureAwait(false);
                    if (verified.Kind == "error")
                    {
                        if (verified.RequestId != verification.RequestId)
                            throw new WorkerProtocolException("Worker设备检查关联无效。", WorkerRenderBackendContract.InvalidCode);
                        if (verified.ErrorCode is null)
                            throw new WorkerProtocolException("Worker未提供渲染后端核验能力。", WorkerRenderBackendContract.UnsupportedCode);
                        if (verified.InputSha256 != verification.InputSha256)
                            throw new WorkerProtocolException("Worker设备检查关联无效。", WorkerRenderBackendContract.InvalidCode);
                        throw new WorkerProtocolException("所选Worker渲染后端未通过设备检查。", WorkerRenderBackendContract.SafeFailureCode(verified.ErrorCode));
                    }
                    VerifiedRenderBackendDevice = WorkerRenderBackendContract.DecodeResult(verification, verified, backend);
                }
            }
            catch (Exception primary)
            {
                try { await StopProcessAsync(CancellationToken.None).ConfigureAwait(false); }
                catch (Exception cleanup) { primary.Data["WorkerCleanupFailure"] = cleanup; }
                throw;
            }
        }
        finally { _lifecycleGate.Release(); }
    }

    /// <summary>Worker 的 gen0 预算变量；Server 进程不设置。</summary>
    internal const string Gen0BudgetVariable = "DOTNET_GCgen0size";
    /// <summary>64 MiB（十六进制）。渲染逐帧短命分配密集，默认预算下 gen0 回收频率主导准备耗时。</summary>
    internal const string Gen0BudgetValue = "0x4000000";

    /// <summary>
    /// 为 Worker 子进程设定 gen0 预算；运维已显式设置（DOTNET_ 或 COMPlus_ 前缀）时沿用其值。
    /// 换取约 30% 的帧准备耗时下降，代价约为每个 Worker 增加 100 MiB 常驻内存。
    /// </summary>
    internal static void ApplyWorkerGcBudget(ProcessStartInfo start)
    {
        ArgumentNullException.ThrowIfNull(start);
        foreach (string key in (string[])[Gen0BudgetVariable, "COMPlus_GCgen0size"])
            if (start.Environment.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value)) return;
        start.Environment[Gen0BudgetVariable] = Gen0BudgetValue;
    }

    internal static void RemoveDesktopCredentials(ProcessStartInfo start)
    {
        ArgumentNullException.ThrowIfNull(start);
        foreach (string key in start.Environment.Keys.Where(key =>
                     key.StartsWith("MAKO_DESKTOP_", StringComparison.OrdinalIgnoreCase)).ToArray())
            start.Environment.Remove(key);
    }

    /// <summary>发送一条受限 Worker 命令，并校验回执的请求 ID 与输入哈希。</summary>
    public async Task<WorkerInvocationResult> InvokeAsync(WorkerInvocation invocation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        if (string.IsNullOrWhiteSpace(invocation.Kind)) throw new ArgumentException("Worker kind 不能为空。", nameof(invocation));
        ArgumentNullException.ThrowIfNull(invocation.Payload);
        if (invocation.Payload.Length > WorkerProtocol.MaxPayloadBytes)
            throw new WorkerProtocolException("Worker 输入超过单条 payload 上限。");
        if (invocation.DelayMilliseconds is < 0 or > 60_000)
            throw new ArgumentOutOfRangeException(nameof(invocation));
        string requestId = invocation.RequestId ?? Guid.NewGuid().ToString("N");
        byte[] payload = [.. invocation.Payload];
        string hash = WorkerProtocol.ComputeSha256(payload);
        WorkerMessage response = await InvokeMessageAsync(
            new(invocation.Kind, requestId, hash, payload, invocation.DelayMilliseconds), cancellationToken).ConfigureAwait(false);
        if (response.Cancelled) throw new OperationCanceledException(cancellationToken);
        return new(requestId, response.Payload, hash);
    }

    /// <summary>发送固定长度 render-frame-v1 请求；超时会走同一 cancel/进程树回收合同。</summary>
    public async Task<WorkerRenderFrameResult> InvokeRenderFrameAsync(
        RenderFrameV1WorkerRequest request, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(1))
            throw new ArgumentOutOfRangeException(nameof(timeout));
        byte[] payload = request.Encode();
        string inputHash = RenderFrameV1WorkerRequest.ComputeInputSha256(payload);
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        string requestId = Guid.NewGuid().ToString("N");
        RenderFrameV1WorkerResult? frame = null;
        await InvokeMessageAsync(new("render-frame-v1", requestId, inputHash, payload), deadline.Token,
            response => frame = RenderFrameV1WorkerResult.FromMessage(request, response)).ConfigureAwait(false);
        return new(frame!.RequestId, frame.Pixels, frame.InputSha256, frame.OutputSha256,
            frame.Width, frame.Height, frame.Stride, frame.TimeUs, frame.PixelFormat);
    }

    private (string Identity, int Count)? TryComputeScriptReferenceIdentity() =>
        TryReadScriptReferenceIdentity() is { } references ? (references.Identity, references.Count) : null;

    private ScriptReferenceIdentity? TryReadScriptReferenceIdentity()
    {
        try
        {
            // Self-contained deployments use the apphost as FileName, while Debug
            // probes commonly use `dotnet <Worker.dll>`; in the latter case the
            // runtime's script-refs live beside the managed Worker, not beside dotnet.
            string executable = _options.Arguments?.FirstOrDefault(argument =>
                    argument.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                    || argument.EndsWith(".py", StringComparison.OrdinalIgnoreCase))
                ?? _options.FileName;
            string root = Path.GetDirectoryName(Path.GetFullPath(executable)) ?? string.Empty;
            string directory = Path.Combine(root, "script-refs", "net10.0");
            return ScriptReferenceIdentity.Read(directory);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (ArgumentException) { return null; }
    }

    /// <summary>探测并校验私有 package Runtime 能力，不改变 hello 原 wire。</summary>
    public async Task<PackageRenderWorkerCapabilities> GetPackageCapabilitiesAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        => await GetPackageCapabilitiesCoreAsync(timeout, validation: false, cancellationToken).ConfigureAwait(false);

    /// <summary>验证任务专用能力协商；必须由明确支持 package validation 的 Worker 回执。</summary>
    public async Task<PackageRenderWorkerCapabilities> GetPackageValidationCapabilitiesAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        => await GetPackageCapabilitiesCoreAsync(timeout, validation: true, cancellationToken).ConfigureAwait(false);

    private async Task<PackageRenderWorkerCapabilities> GetPackageCapabilitiesCoreAsync(TimeSpan timeout, bool validation,
        CancellationToken cancellationToken, bool runtimeInput = false, Action<ScriptReferenceIdentity>? verifiedReferences = null)
    {
        try
        {
            byte[] payload = PackageRenderWorkerContract.EncodeCapabilitiesRequest();
            string requestKind = runtimeInput ? ProjectRuntimeInputWorkerContract.CapabilitiesKind : validation ? PackageRenderWorkerContract.ValidationCapabilitiesKind : PackageRenderWorkerContract.CapabilitiesKind;
            string resultKind = runtimeInput ? ProjectRuntimeInputWorkerContract.CapabilitiesResultKind : validation ? PackageRenderWorkerContract.ValidationCapabilitiesResultKind : PackageRenderWorkerContract.CapabilitiesResultKind;
            WorkerMessage request = new(requestKind, Guid.NewGuid().ToString("N"),
                WorkerProtocol.ComputeSha256(payload), payload);
            PackageRenderWorkerCapabilities? value = null;
            await InvokeTimedMessageAsync(request, resultKind, timeout,
                response =>
                {
                    value = PackageRenderWorkerContract.DecodeCapabilities(response.Payload);
                    WorkerPackageProfileAdmission.RequireCurrent(value, "WorkerPackageUnavailable");
                    EffectPackageProfile profile = ProjectEffectsJson.PackageProfile;
                    if (value.EngineIdentity != StaticProjectSnapshotCodec.EngineIdentity || value.CodecProfile != PackageProjectSnapshotCodec.Profile
                        || value.PackageVersion != profile.PackageVersion || value.SdkVersion != profile.SdkVersion || value.ShaderProfile != profile.ShaderProfile)
                        throw new WorkerProtocolException("包 Worker 能力与当前固定合同不匹配。", "WorkerPackageUnavailable");
                    var expected = TryReadScriptReferenceIdentity();
                    if (expected is null || value.ScriptRefsIdentity != expected.Identity || value.ScriptRefsCount != expected.Count)
                        throw new WorkerProtocolException("包 Worker 编译参考集与当前部署不匹配。", "WorkerPackageUnavailable");
                    if (runtimeInput && value.RuntimeInputProfile != ProjectRuntimeInputWorkerContract.Profile)
                        throw new WorkerProtocolException("Worker 未声明统一工程输入能力。", "WorkerRuntimeInputProfileMismatch");
                    if (validation && (value.ValidationProfiles is null
                        || !value.ValidationProfiles.Contains(PackageRenderWorkerContract.ValidationProfile, StringComparer.Ordinal)))
                        throw new WorkerProtocolException("包 Worker 未声明 package validation profile。", "WorkerPackageUnavailable");
                    verifiedReferences?.Invoke(expected);
                }, cancellationToken, requirePackageRuntimeGuards: true).ConfigureAwait(false);
            return value!;
        }
        catch (Exception primary)
        {
            try { await StopAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception cleanup) { primary.Data["WorkerCleanupFailure"] = cleanup; }
            // A validation-capable Worker is identified by this private handshake.
            // Older or otherwise mismatched binaries commonly answer with a generic
            // protocol/unsupported error; expose one stable admission code to callers.
            if (primary is WorkerProtocolException protocol
                && protocol.Code is not ("WorkerPackageCapabilitiesInvalid" or "WorkerPackageUnavailable")
                && !(verifiedReferences is not null && protocol.Code == WorkerRenderBackendContract.SafeFailureCode(protocol.Code)))
                throw new WorkerProtocolException("包 Worker 未提供当前验证所需的能力。", "WorkerPackageUnavailable",
                    protocol.Diagnostics, protocol.PackageDiagnostics);
            throw;
        }
    }

    /// <summary>打开一次固定 package snapshot 的独立持久 Runtime 会话。</summary>
    public async Task<StaticWorkerSessionHandle> OpenPackageSessionAsync(ReadOnlyMemory<byte> snapshotBytes,
        TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        Guid id = Guid.NewGuid();
        _ = PackageRenderWorkerContract.EncodeOpen(id, snapshotBytes.Span);
        var capabilities = await GetPackageCapabilitiesAsync(timeout, cancellationToken).ConfigureAwait(false);
        using (var source = System.Text.Json.JsonDocument.Parse(snapshotBytes))
        {
            if (source.RootElement.TryGetProperty("profile", out var profile)
                && profile.GetString() == PackagePresetProjectSnapshotCodec.Profile
                && capabilities.SourceProfiles?.Contains(PackagePresetProjectSnapshotCodec.Profile, StringComparer.Ordinal) != true)
                throw new WorkerProtocolException("包 Worker 未声明混合预设来源。", "WorkerPackageProfileMismatch");
        }
        return await OpenPreparedScriptSessionAsync(PackageRenderWorkerContract.OpenKind, id, snapshotBytes, timeout, capabilities, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>沿用固定 package 会话交付实际像素和完整 Runtime 诊断。</summary>
    public Task<WorkerMessage> RenderPackageSessionAsync(StaticWorkerFrameRequest frame, TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        byte[] payload = PackageRenderWorkerContract.EncodeFrame(frame);
        WorkerMessage request = new(PackageRenderWorkerContract.RenderKind, Guid.NewGuid().ToString("N"),
            WorkerProtocol.ComputeSha256(payload), payload, sessionId: frame.SessionId.ToString("N"),
            sourceSha256: frame.SourceSha256, frameSequence: frame.Sequence,
            frameTransparentBackground: frame.TransparentBackground, frameQuality: frame.Quality);
        return InvokeTimedMessageAsync(request, PackageRenderWorkerContract.FrameResultKind, timeout,
            response => PackageRenderWorkerContract.ValidateFrameResult(frame, response), cancellationToken,
            requirePackageRuntimeGuards: true);
    }

    /// <summary>关闭 package 会话并等待 Worker Runtime 排水。</summary>
    public async Task ClosePackageSessionAsync(StaticWorkerSessionHandle session, long sequence, TimeSpan timeout,
        CancellationToken cancellationToken = default)
        => await ClosePackageSessionCoreAsync(session, sequence, timeout, false, cancellationToken).ConfigureAwait(false);

    internal Task ClosePackageSessionForReopenAsync(StaticWorkerSessionHandle session, long sequence, TimeSpan timeout,
        CancellationToken cancellationToken = default)
        => ClosePackageSessionCoreAsync(session, sequence, timeout, true, cancellationToken);

    private async Task ClosePackageSessionCoreAsync(StaticWorkerSessionHandle session, long sequence, TimeSpan timeout,
        bool retainPreparedInput, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        byte[] payload = PackageRenderWorkerContract.EncodeClose(session.SessionId, session.SourceSha256, sequence);
        WorkerMessage request = new(PackageRenderWorkerContract.CloseKind, Guid.NewGuid().ToString("N"),
            WorkerProtocol.ComputeSha256(payload), payload, sessionId: session.SessionId.ToString("N"),
            sourceSha256: session.SourceSha256, frameSequence: sequence);
        await InvokeTimedMessageAsync(request, PackageRenderWorkerContract.SessionResultKind, timeout,
            ValidateEmptyPackageSessionResult, cancellationToken, requirePackageRuntimeGuards: true).ConfigureAwait(false);
        if (!retainPreparedInput) ReleasePreparedScriptInputs();
    }

    /// <summary>打开一次固定 snapshot-v1 输入的独立持久 Runtime 会话。</summary>
    public async Task<StaticWorkerSessionHandle> OpenStaticSessionAsync(ReadOnlyMemory<byte> snapshotBytes,
        TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        Guid id = Guid.NewGuid();
        byte[] payload = StaticRenderWorkerContract.EncodeOpen(id, snapshotBytes.Span);
        string sourceHash = WorkerProtocol.ComputeSha256(payload.AsSpan(StaticRenderWorkerContract.OpenHeaderBytes));
        WorkerMessage request = new(StaticRenderWorkerContract.OpenKind, Guid.NewGuid().ToString("N"),
            WorkerProtocol.ComputeSha256(payload), payload, sessionId: id.ToString("N"), sourceSha256: sourceHash, frameSequence: 0);
        await InvokeTimedMessageAsync(request, StaticRenderWorkerContract.SessionResultKind, timeout,
            ValidateEmptySessionResult, cancellationToken).ConfigureAwait(false);
        return new(id, sourceHash);
    }

    /// <summary>查询本次 Worker 进程实际编译的静态 snapshot 来源能力。</summary>
    public async Task<StaticWorkerCapabilitiesDto> GetStaticCapabilitiesAsync(TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        byte[] payload = StaticRenderWorkerContract.EncodeCapabilitiesRequest();
        WorkerMessage request = new(StaticRenderWorkerContract.CapabilitiesKind, Guid.NewGuid().ToString("N"),
            WorkerProtocol.ComputeSha256(payload), payload);
        WorkerMessage response = await InvokeTimedMessageAsync(request, StaticRenderWorkerContract.CapabilitiesResultKind,
            timeout, value =>
            {
                if (value.Error is not null || value.ErrorCode is not null || value.Cancelled || value.OutputSha256 is null
                    || value.SessionId is not null || value.SourceSha256 is not null || value.FrameSequence is not null
                    || value.Diagnostics is not null || value.TaskId is not null || value.ValidationProgress is not null
                    || value.ExportProgress is not null || value.AnalysisProgress is not null || value.PackageDiagnostics is not null)
                    throw new WorkerProtocolException("静态 Worker 能力回执含未协商字段。", "WorkerStaticProfileMismatch");
                _ = StaticRenderWorkerContract.DecodeCapabilities(value.Payload);
            }, cancellationToken).ConfigureAwait(false);
        return StaticRenderWorkerContract.DecodeCapabilities(response.Payload);
    }

    /// <summary>沿用固定静态会话，交付受验证像素和完整真实诊断快照。</summary>
    public Task<WorkerMessage> RenderStaticSessionAsync(StaticWorkerFrameRequest frame, TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        byte[] payload = StaticRenderWorkerContract.EncodeFrame(frame);
        WorkerMessage request = new(StaticRenderWorkerContract.RenderKind, Guid.NewGuid().ToString("N"),
            WorkerProtocol.ComputeSha256(payload), payload, sessionId: frame.SessionId.ToString("N"),
            sourceSha256: frame.SourceSha256, frameSequence: frame.Sequence,
            frameTransparentBackground: frame.TransparentBackground, frameQuality: frame.Quality);
        return InvokeTimedMessageAsync(request, StaticRenderWorkerContract.FrameResultKind, timeout,
            response => StaticRenderWorkerContract.ValidateFrameResult(frame, response), cancellationToken);
    }

    /// <summary>关闭固定会话，等待 Runtime 释放回执；失败仍由代理回收私有进程。</summary>
    public async Task CloseStaticSessionAsync(StaticWorkerSessionHandle session, long sequence, TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        byte[] payload = StaticRenderWorkerContract.EncodeClose(session.SessionId, session.SourceSha256, sequence);
        WorkerMessage request = new(StaticRenderWorkerContract.CloseKind, Guid.NewGuid().ToString("N"),
            WorkerProtocol.ComputeSha256(payload), payload, sessionId: session.SessionId.ToString("N"),
            sourceSha256: session.SourceSha256, frameSequence: sequence);
        await InvokeTimedMessageAsync(request, StaticRenderWorkerContract.SessionResultKind, timeout,
            ValidateEmptySessionResult, cancellationToken).ConfigureAwait(false);
    }

    private async Task<WorkerMessage> InvokeTimedMessageAsync(WorkerMessage request, string expectedKind,
        TimeSpan timeout, Action<WorkerMessage> validateResponse, CancellationToken cancellationToken,
        bool requirePackageRuntimeGuards = false, Process? expectedProcess = null,
        string expectedProcessFailureCode = "SceneMeasurementUnavailable",
        string expectedProcessFailureMessage = "测量Worker实际进程代次已改变。")
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(1)) throw new ArgumentOutOfRangeException(nameof(timeout));
        using CancellationTokenSource timeoutSource = new(timeout);
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        try
        {
            return await InvokeMessageAsync(request, deadline.Token, validateResponse, expectedKind,
                requirePackageRuntimeGuards: requirePackageRuntimeGuards, expectedProcess: expectedProcess,
                expectedProcessFailureCode: expectedProcessFailureCode,
                expectedProcessFailureMessage: expectedProcessFailureMessage).ConfigureAwait(false);
        }
        catch (WorkerCooperativeCancellationException) when (timeoutSource.IsCancellationRequested)
        {
            throw new OperationCanceledException(deadline.Token);
        }
        catch (WorkerInvocationNotStartedException) when (timeoutSource.IsCancellationRequested)
        {
            throw new OperationCanceledException(deadline.Token);
        }
    }

    private static void ValidateEmptyPackageSessionResult(WorkerMessage response)
    {
        ValidateEmptySessionResult(response);
        if (response.ErrorCode is not null || response.TaskId is not null || response.ProgressSequence is not null
            || response.ValidationProgress is not null || response.ExportFormat is not null || response.ExportIncludeAudio is not null
            || response.ExportProgress is not null || response.ProcessGroupId is not null || response.MediaFileSizeLimitBytes is not null
            || response.ExportDiagnostics is not null || response.ExportDiagnosticsFailureCode is not null || response.AnalysisProgress is not null)
            throw new WorkerProtocolException("包 Worker 会话回执含未协商字段。", "WorkerPackageProtocolInvalid");
    }

    private static void ValidatePackageResponseEnvelope(WorkerMessage request, WorkerMessage response,
        bool cancellationWon, bool callerCancellationRequested)
    {
        if (request.Kind is PackageResourceWorkerContract.CapabilitiesKind or PackageResourceWorkerContract.ValidateKind)
        {
            if (response.RequestId != request.RequestId || !string.Equals(response.InputSha256, request.InputSha256, StringComparison.OrdinalIgnoreCase))
                throw new WorkerProtocolException("Worker资源回执关联字段不匹配。", "WorkerResourceProtocolInvalid");
            if (response.Cancelled)
            {
                if (!cancellationWon && !callerCancellationRequested) throw new WorkerProtocolException("Worker资源未经请求返回取消。", "WorkerResourceProtocolInvalid");
                return;
            }
            if (response.Kind == "error")
            {
                if (response.Error is null || response.ErrorCode is null || response.Payload.Length != 0 || response.OutputSha256 is not null)
                    throw new WorkerProtocolException("Worker资源错误回执含非法字段。", "WorkerResourceProtocolInvalid");
                return;
            }
            string expected = request.Kind == PackageResourceWorkerContract.CapabilitiesKind
                ? PackageResourceWorkerContract.CapabilitiesResultKind : PackageResourceWorkerContract.ValidateResultKind;
            if (response.Kind != expected || response.Error is not null || response.ErrorCode is not null || response.OutputSha256 is null)
                throw new WorkerProtocolException("Worker资源成功回执字段无效。", "WorkerResourceProtocolInvalid");
            return;
        }
        static void Reject(string message) => throw new WorkerProtocolException(message, "WorkerPackageProtocolInvalid");

        if (response.RequestId != request.RequestId)
            Reject("包 Worker 回执 requestId 不匹配。");
        if (!string.Equals(response.InputSha256, request.InputSha256, StringComparison.OrdinalIgnoreCase))
            Reject("包 Worker 回执 inputSha256 不匹配。");

        bool capability = IsPackageCapabilitiesKind(request.Kind);
        if (capability)
        {
            if (response.SessionId is not null || response.SourceSha256 is not null || response.FrameSequence is not null)
                Reject("包 Worker 能力回执不能携带会话关联字段。");
        }
        else if (response.SessionId != request.SessionId || response.SourceSha256 != request.SourceSha256
            || response.FrameSequence != request.FrameSequence)
            Reject("包 Worker 回执会话、源 SHA 或请求序号不匹配。");

        if (response.TaskId is not null || response.ValidationFramesPerOutput is not null
            || response.ProgressSequence is not null || response.ValidationProgress is not null
            || response.ExportFormat is not null || response.ExportIncludeAudio is not null
            || response.ExportProgress is not null || response.ProcessGroupId is not null
            || response.MediaFileSizeLimitBytes is not null || response.ExportDiagnostics is not null
            || response.ExportDiagnosticsFailureCode is not null || response.AnalysisProgress is not null
            || response.DelayMilliseconds != 0)
            Reject("包 Worker 回执含未协商字段。");

        bool hasFrameMetadata = response.FrameWidth is not null || response.FrameHeight is not null
            || response.FrameStride is not null || response.FrameTimeUs is not null || response.FrameFormat is not null
            || response.FrameTransparentBackground is not null || response.FrameQuality is not null
            || response.Diagnostics is not null;
        if (response.Kind is PackageRenderWorkerContract.CapabilitiesResultKind or ProjectImagePreviewWorkerContract.CapabilitiesResultKind
            or ProjectRuntimeInputWorkerContract.CapabilitiesResultKind or ProjectImageValidationWorkerContract.CapabilitiesResultKind
            or RuntimeValidationWorkerContract.CapabilitiesResultKind or PackageRenderWorkerContract.SessionResultKind)
        {
            if (hasFrameMetadata || response.PackageDiagnostics is not null) Reject("包 Worker 能力／会话回执含未协商帧字段。");
        }

        if (response.Cancelled && !cancellationWon && !callerCancellationRequested)
            Reject("包 Worker 未经请求返回取消回执。");

        if (response.Cancelled)
        {
            if (response.Payload.Length != 0 || response.OutputSha256 is not null || response.Error is not null
                || response.ErrorCode is not null || hasFrameMetadata || response.PackageDiagnostics is not null)
                Reject("包 Worker 取消回执含输出或错误字段。");
            return;
        }

        if (response.Kind == "error")
        {
            bool geometry = response.FrameWidth is not null || response.FrameHeight is not null
                || response.FrameStride is not null || response.FrameTimeUs is not null || response.FrameFormat is not null
                || response.FrameTransparentBackground is not null || response.FrameQuality is not null;
            if (response.OutputSha256 is not null || response.Payload.Length != 0 || response.Cancelled
                || response.Error is null || response.ErrorCode is null || geometry
                || capability && response.Diagnostics is not null)
                Reject("包 Worker 错误回执含输出负载。");
            if (response.PackageDiagnostics is { } package)
            {
                WorkerPackageDiagnosticsContract.Validate(package);
                bool compilation = request.Kind is PackageRenderWorkerContract.OpenKind or ProjectImagePreviewWorkerContract.OpenKind or ProjectRuntimeInputWorkerContract.OpenKind
                    && response.ErrorCode == "WorkerPackageCompilationFailed" && package.Source.Length != 0
                    && package.Image.Length == 0 && package.Material.Length == 0 && response.Diagnostics is null;
                bool runtime = request.Kind == PackageRenderWorkerContract.RenderKind
                    && response.ErrorCode != "WorkerPackageCompilationFailed" && response.Diagnostics is { HasError: true } && package.Source.Length == 0;
                if (!compilation && !runtime) Reject("包错误旁报必须属于实际open编译失败或render Runtime Error。");
            }
            if (response.ErrorCode == "WorkerPackageCompilationFailed" && response.PackageDiagnostics is null)
                Reject("包编译失败缺少完整来源诊断。");
            return;
        }

        if (response.Error is not null || response.ErrorCode is not null)
            Reject("包 Worker 成功回执含错误字段。");
        if (response.Kind is PackageRenderWorkerContract.CapabilitiesResultKind or ProjectImagePreviewWorkerContract.CapabilitiesResultKind
            or ProjectRuntimeInputWorkerContract.CapabilitiesResultKind or ProjectImageValidationWorkerContract.CapabilitiesResultKind or PackageRenderWorkerContract.FrameResultKind)
        {
            if (response.OutputSha256 is null)
                Reject("包 Worker 回执缺少输出 SHA-256。");
        }
        else if (response.Kind == PackageRenderWorkerContract.SessionResultKind && response.OutputSha256 is not null)
            Reject("包 Worker 会话回执不能携带输出 SHA-256。");
    }

    private static void ValidateEmptySessionResult(WorkerMessage response)
    {
        if (response.Payload.Length != 0 || response.OutputSha256 is not null || response.Diagnostics is not null
            || response.Error is not null || response.Cancelled)
            throw new WorkerProtocolException("静态会话打开／关闭回执含非法输出。");
    }

    /// <summary>发送 shutdown 并等待进程退出；超时则杀死整个进程树。</summary>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        // Stop is a cleanup operation: a canceled caller must not skip the gate and
        // leave an already started worker running. The token is still honored by the
        // graceful shutdown below, which forcefully reaps the process tree if it wins.
        lock (_stateGate)
        {
            _activeCallCancellation?.Cancel();
            foreach (var preparation in _scriptPreparations.ToArray()) preparation.Cancel();
        }
        await _callGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await _lifecycleGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try { await StopProcessAsync(cancellationToken).ConfigureAwait(false); }
            finally { _lifecycleGate.Release(); }
        }
        finally { _callGate.Release(); }
    }

    public ValueTask DisposeAsync()
    {
        lock (_stateGate)
        {
            _disposed = true;
            if (_disposeTask is null || _disposeTask.IsFaulted || _disposeTask.IsCanceled) _disposeTask = StopAsync();
            return new(_disposeTask);
        }
        // Gates remain managed objects until GC: queued calls must acquire the gate,
        // observe _disposed and release it without racing SemaphoreSlim.Dispose.
    }

    private async Task<WorkerMessage> InvokeMessageAsync(WorkerMessage request, CancellationToken cancellationToken,
        Action<WorkerMessage>? validateResponse = null, string expectedKind = "result",
        Action<WorkerMessage>? validateMessage = null, bool requireAudioPreparationGuards = false,
        bool requirePackageRuntimeGuards = false, Process? expectedProcess = null,
        string expectedProcessFailureCode = "SceneMeasurementUnavailable",
        string expectedProcessFailureMessage = "测量Worker实际进程代次已改变。")
    {
        WorkerProtocol.Validate(request);
        try { await _callGate.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { throw new WorkerInvocationNotStartedException(cancellationToken); }
        using CancellationTokenSource callCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            lock (_stateGate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _activeCallCancellation = callCancellation;
            }
        }
        catch
        {
            _callGate.Release();
            throw;
        }
        cancellationToken = callCancellation.Token;
        Task<WorkerMessage>? read = null;
        Process? invocationProcess = null;
        Stream? invocationOutput = null;
        bool writeStarted = false;
        try
        {
            Process? verifiedPackageProcess = VerifiedPackageProcess(request.Kind);
            if (requirePackageRuntimeGuards && !IsPackageCapabilitiesKind(request.Kind)
                && (_process is null || _process.HasExited || verifiedPackageProcess is null
                    || !ReferenceEquals(_process, verifiedPackageProcess)
                    || IsPackageSessionKind(request.Kind) && !ReferenceEquals(_process, _packageSessionProcess)))
                throw new WorkerProtocolException("包 Worker 实际进程代次已改变或没有已验证会话。", "WorkerPackageUnavailable");
            await StartAsync(cancellationToken).ConfigureAwait(false);
            if (requireAudioPreparationGuards && !HasVerifiedPrivateGuard)
                throw new WorkerProtocolException("音频准备需要实际私有Worker进程守卫与子进程回收能力。", "WorkerAudioPreparationUnavailable");
            if (requirePackageRuntimeGuards && !HasVerifiedPrivateGuard)
                throw new WorkerProtocolException("包 Runtime 需要实际私有Worker进程守卫与子进程回收能力。", "WorkerPackageUnavailable");
            if (requirePackageRuntimeGuards || expectedProcess is not null)
            {
                // Public StartAsync can replace a dead process independently of the call gate.
                // Bind an expected process, the request and its pipe atomically under the lifecycle gate.
                await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    if (expectedProcess is not null
                        && (_process is null || !ReferenceEquals(expectedProcess, _process) || expectedProcess.HasExited))
                        throw new WorkerProtocolException(expectedProcessFailureMessage, expectedProcessFailureCode);
                    if (requirePackageRuntimeGuards && !IsPackageCapabilitiesKind(request.Kind)
                        && (_process is null || _process.HasExited || (verifiedPackageProcess = VerifiedPackageProcess(request.Kind)) is null
                            || !ReferenceEquals(_process, verifiedPackageProcess)
                            || IsPackageSessionKind(request.Kind) && !ReferenceEquals(_process, _packageSessionProcess)))
                        throw new WorkerProtocolException("包 Worker 实际进程代次已改变或没有已验证会话。", "WorkerPackageUnavailable");
                    invocationProcess = _process;
                    invocationOutput = _output;
                    cancellationToken.ThrowIfCancellationRequested();
                    writeStarted = true;
                    await WriteAsync(request, cancellationToken).ConfigureAwait(false);
                }
                finally { _lifecycleGate.Release(); }
            }
            else
            {
                cancellationToken.ThrowIfCancellationRequested();
                writeStarted = true;
                await WriteAsync(request, cancellationToken).ConfigureAwait(false);
            }
            read = requirePackageRuntimeGuards || expectedProcess is not null
                ? ReadRequiredFromStreamAsync(invocationOutput!, CancellationToken.None)
                : ReadRequiredAsync(CancellationToken.None);
            bool cancellationWon = false;
            if (cancellationToken.CanBeCanceled)
            {
                Task canceled = Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                if (await Task.WhenAny(read, canceled).ConfigureAwait(false) != read)
                {
                    cancellationWon = true;
                    if (!await SendCancellationAndWaitAsync(request, read).ConfigureAwait(false))
                    {
                        throw new OperationCanceledException(cancellationToken);
                    }
                }
            }
            WorkerMessage response = await read.ConfigureAwait(false);
            if (requirePackageRuntimeGuards)
            {
                await _lifecycleGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                try
                {
                    if (!ReferenceEquals(invocationProcess, _process) || invocationProcess?.HasExited == true)
                        throw new WorkerProtocolException("包 Worker 回执的实际进程代次已改变。", "WorkerPackageUnavailable");
                    ValidatePackageResponseEnvelope(request, response, cancellationWon, cancellationToken.IsCancellationRequested);
                }
                finally { _lifecycleGate.Release(); }
            }
            if (!requirePackageRuntimeGuards && response.PackageDiagnostics is not null)
                throw new WorkerProtocolException("非包请求收到包诊断。", "WorkerPackageDiagnosticsInvalid");
            validateMessage?.Invoke(response);
            if (response.RequestId != request.RequestId) throw new WorkerProtocolException("Worker 回执 requestId 不匹配。");
            if (request.SessionId is not null && (response.SessionId != request.SessionId
                || response.SourceSha256 != request.SourceSha256 || response.FrameSequence != request.FrameSequence))
                throw new WorkerProtocolException("Worker 回执会话、源 SHA 或请求序号不匹配。");
            if (response.Kind == "error")
                throw new WorkerProtocolException(response.Error ?? "Worker 执行失败。",
                    response.ErrorCode ?? "WorkerExecutionFailed", response.Diagnostics, response.PackageDiagnostics);
            if (response.Kind != expectedKind) throw new WorkerProtocolException("Worker 回执 kind 不匹配。");
            if (!string.Equals(response.InputSha256, request.InputSha256, StringComparison.OrdinalIgnoreCase))
                throw new WorkerProtocolException("Worker 回执 inputSha256 不匹配。");
            if (response.Cancelled && (requirePackageRuntimeGuards && request.Kind == PackageRenderWorkerContract.RenderKind
                || request.Kind == StaticRenderWorkerContract.RenderKind))
            {
                if (request.Kind == StaticRenderWorkerContract.RenderKind) ValidateStaticCancellation(response, cancellationWon, cancellationToken);
                throw new WorkerCooperativeCancellationException(cancellationToken);
            }
            if (cancellationWon || response.Cancelled) throw new OperationCanceledException(cancellationToken);
            validateResponse?.Invoke(response);
            if (requirePackageRuntimeGuards)
            {
                await _lifecycleGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                try
                {
                    if (!ReferenceEquals(invocationProcess, _process) || invocationProcess?.HasExited == true)
                        throw new WorkerProtocolException("包 Worker 回执的实际进程代次已改变。", "WorkerPackageUnavailable");
                    if (IsPackageRenderCapabilitiesKind(request.Kind)) _packageRenderVerifiedProcess = invocationProcess;
                    if (IsPackageValidationCapabilitiesKind(request.Kind)) _packageValidationVerifiedProcess = invocationProcess;
                    if (IsImageValidationCapabilitiesKind(request.Kind)) _imageValidationVerifiedProcess = invocationProcess;
                    if (request.Kind == RuntimeValidationWorkerContract.CapabilitiesKind) _runtimeValidationVerifiedProcess = invocationProcess;
                    if (request.Kind == PackageResourceWorkerContract.CapabilitiesKind) _packageResourceVerifiedProcess = invocationProcess;
                    if (request.Kind == ProjectRuntimeInputWorkerContract.CapabilitiesKind) _runtimeInputVerifiedProcess = invocationProcess;
                    if (request.Kind == ProjectImagePreviewWorkerContract.CapabilitiesKind) _imagePreviewVerifiedProcess = invocationProcess;
                    if (request.Kind is PackageRenderWorkerContract.OpenKind or ProjectImagePreviewWorkerContract.OpenKind or ProjectRuntimeInputWorkerContract.OpenKind)
                    { _packageSessionProcess = invocationProcess; _packageSessionOpenKind = request.Kind; }
                    if (request.Kind == PackageRenderWorkerContract.CloseKind)
                    { _packageSessionProcess = null; _packageSessionOpenKind = null; }
                }
                finally { _lifecycleGate.Release(); }
            }
            return response;
        }
        catch (OperationCanceledException) when (!writeStarted && cancellationToken.IsCancellationRequested)
        {
            throw new WorkerInvocationNotStartedException(cancellationToken);
        }
        catch (OperationCanceledException) when (read is { IsCompletedSuccessfully: true })
        {
            // A consumed, matched cancellation acknowledgement keeps the stream reusable.
            throw;
        }
        catch (Exception primary)
        {
            // A partial write, malformed reply or EOF invalidates the private stream.
            try { await ReapAsync().ConfigureAwait(false); }
            catch (Exception cleanup) { primary.Data["WorkerCleanupFailure"] = cleanup; }
            throw;
        }
        finally
        {
            lock (_stateGate) { _activeCallCancellation = null; }
            _callGate.Release();
        }

        async Task ReapAsync()
        {
            await _lifecycleGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try { await StopProcessAsync(CancellationToken.None).ConfigureAwait(false); }
            finally { _lifecycleGate.Release(); }
            if (read is not null)
            {
                try { await read.ConfigureAwait(false); }
                catch (Exception error) when (error is IOException or WorkerProtocolException or ObjectDisposedException) { }
            }
        }
    }

    private static void ValidateStaticCancellation(WorkerMessage response, bool cancellationWon, CancellationToken token)
    {
        if ((!cancellationWon && !token.IsCancellationRequested) || response.Payload.Length != 0 || response.OutputSha256 is not null
            || response.Error is not null || response.ErrorCode is not null || response.Diagnostics is not null
            || response.FrameWidth is not null || response.FrameHeight is not null || response.FrameStride is not null
            || response.FrameTimeUs is not null || response.FrameFormat is not null || response.FrameTransparentBackground is not null
            || response.FrameQuality is not null || response.TaskId is not null || response.ValidationFramesPerOutput is not null
            || response.ProgressSequence is not null || response.ValidationProgress is not null || response.ExportFormat is not null
            || response.ExportIncludeAudio is not null || response.ExportProgress is not null || response.ProcessGroupId is not null
            || response.MediaFileSizeLimitBytes is not null || response.ExportDiagnostics is not null
            || response.ExportDiagnosticsFailureCode is not null || response.AnalysisProgress is not null
            || response.PackageDiagnostics is not null || response.DelayMilliseconds != 0)
            throw new WorkerProtocolException("静态 Worker 取消回执含未协商字段或未请求取消。");
    }

    private async Task<WorkerMessage> ReadRequiredAsync(CancellationToken cancellationToken)
    {
        Stream output = _output ?? throw new InvalidOperationException("Worker 尚未启动。");
        return await ReadRequiredFromStreamAsync(output, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<WorkerMessage> ReadRequiredFromStreamAsync(Stream output, CancellationToken cancellationToken)
    {
        WorkerMessage? message = await WorkerProtocol.ReadAsync(output, cancellationToken).ConfigureAwait(false);
        return message ?? throw new EndOfStreamException("Worker 已退出。");
    }

    private async Task WriteAsync(WorkerMessage message, CancellationToken cancellationToken)
    {
        Stream input = _input ?? throw new InvalidOperationException("Worker 尚未启动。");
        await WriteAsync(input, message, cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteAsync(Stream input, WorkerMessage message, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await WorkerProtocol.WriteAsync(input, message, cancellationToken).ConfigureAwait(false); }
        finally { _writeGate.Release(); }
    }

    private async Task<bool> SendCancellationAndWaitAsync(WorkerMessage request, Task<WorkerMessage> read, Stream? input = null)
    {
        using CancellationTokenSource grace = new(_options.CancellationGracePeriod);
        try
        {
            if (input is null) await WriteAsync(new("cancel", request.RequestId), grace.Token).ConfigureAwait(false);
            else await WriteAsync(input, new("cancel", request.RequestId), grace.Token).ConfigureAwait(false);
            await read.WaitAsync(grace.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception error) when (error is OperationCanceledException or IOException or ObjectDisposedException) { return false; }
    }

    private async Task StopProcessAsync(CancellationToken cancellationToken = default)
    {
        Process? process = _process;
        if (process is null) { ReleasePreparedScriptInputs(processExited: true); return; }
        bool reaped = false;
        try
        {
            _ownedTree?.ObserveDescendants();
            if (!process.HasExited)
            {
                try
                {
                    string requestId = Guid.NewGuid().ToString("N");
                    using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeout.CancelAfter(_options.CancellationGracePeriod);
                    await WriteAsync(new("shutdown", requestId), timeout.Token).ConfigureAwait(false);
                    while (!process.HasExited) await Task.Delay(20, timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
                catch (IOException) { } // A closed pipe does not release ownership of a running process.
                await KillOwnedAsync().ConfigureAwait(false);
            }
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            // A terminated group leader is not evidence that its private
            // FFmpeg/ffprobe descendants have exited or released their pipes.
            if (_windowsGuard is not null) await _windowsGuard.KillAndDrainAsync(TimeSpan.FromSeconds(3), CancellationToken.None).ConfigureAwait(false);
            else if (_ownedGroup is not null) await _ownedGroup.KillAndDrainAsync(TimeSpan.FromSeconds(3), CancellationToken.None).ConfigureAwait(false);
            else if (_ownedTree is not null) await _ownedTree.KillAndDrainAsync(TimeSpan.FromSeconds(3), CancellationToken.None).ConfigureAwait(false);
            if (_stderrDrain is not null) await _stderrDrain.ConfigureAwait(false);
            reaped = true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Cancellation may happen while writing shutdown or waiting for its
            // acknowledgement. Reap the owned tree before propagating cancellation;
            // disposing Process alone does not terminate the child.
            await KillOwnedAsync().ConfigureAwait(false);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            if (_windowsGuard is not null) await _windowsGuard.KillAndDrainAsync(TimeSpan.FromSeconds(3), CancellationToken.None).ConfigureAwait(false);
            else if (_ownedGroup is not null) await _ownedGroup.KillAndDrainAsync(TimeSpan.FromSeconds(3), CancellationToken.None).ConfigureAwait(false);
            else if (_ownedTree is not null) await _ownedTree.KillAndDrainAsync(TimeSpan.FromSeconds(3), CancellationToken.None).ConfigureAwait(false);
            if (_stderrDrain is not null) await _stderrDrain.ConfigureAwait(false);
            reaped = true;
            throw;
        }
        finally
        {
            if (reaped)
            {
                if (_windowsGuard is not null)
                {
                    _input?.Dispose();
                    _output?.Dispose();
                    _ownedStderrReader?.Dispose();
                    await _windowsGuard.DisposeAsync().ConfigureAwait(false);
                }
                process.Dispose(); _process = null; _input = null; _output = null; _ownedStderrReader = null; _stderrDrain = null;
                _ownedGroup = null; _windowsGuard = null; _guardHandshakeVerified = false; _actualMediaFileSizeLimit = null;
                _ownedTree = null;
                _packageRenderVerifiedProcess = null; _packageValidationVerifiedProcess = null;
                _imageValidationVerifiedProcess = null; _runtimeValidationVerifiedProcess = null;
                _packageResourceVerifiedProcess = null; _packageSessionProcess = null;
                _imagePreviewVerifiedProcess = null; _runtimeInputVerifiedProcess = null; _packageSessionOpenKind = null;
                VerifiedRenderBackendDevice = null;
                ReleasePreparedScriptInputs(processExited: true);
            }
        }

        async Task KillOwnedAsync()
        {
            if (_windowsGuard is not null) await _windowsGuard.KillAndDrainAsync(TimeSpan.FromSeconds(3), CancellationToken.None).ConfigureAwait(false);
            else if (_ownedGroup is not null) await _ownedGroup.KillAndDrainAsync(TimeSpan.FromSeconds(3), CancellationToken.None).ConfigureAwait(false);
            else if (_ownedTree is not null) await _ownedTree.KillAndDrainAsync(TimeSpan.FromSeconds(3), CancellationToken.None).ConfigureAwait(false);
            else if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    }

}

/// <summary>一次固定静态快照的独立 Worker Runtime 会话身份。</summary>
public sealed record StaticWorkerSessionHandle(Guid SessionId, string SourceSha256)
{
    internal RetainedPreparedScriptInput? PreparedInput { get; init; }
}
