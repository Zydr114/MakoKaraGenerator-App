using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MakoKaraGenerator.Processes.MediaEnvelope;

/// <summary>Safe control-layer failure categories, independent of a media profile.</summary>
public enum EnvelopeFailure
{
    /// <summary>The platform cannot establish the requested hard envelope.</summary>
    CapacityUnsupported,
    /// <summary>The frozen budget or kernel memory envelope was exhausted.</summary>
    CapacityExceeded,
    /// <summary>The native tool or its protocol failed.</summary>
    DecodeFailed,
    /// <summary>The captured native owner changed; no replacement may be signalled.</summary>
    OwnershipLost,
    /// <summary>Terminal state and all owned pipe EOFs could not be proven.</summary>
    DrainFailed,
}

/// <summary>A safe category and ASCII code; native exceptions and paths are never attached.</summary>
public sealed class EnvelopeException : Exception
{
    // Keep failure evidence reachable for a supervisor without adding arbitrary
    // paths/native text to Message, InnerException or default ToString().
    private Exception? _retainedCause;
    /// <summary>Creates an exception containing only a bounded safe code.</summary>
    public EnvelopeException(EnvelopeFailure failure, string safeCode) : base(ValidateCode(safeCode))
    { Failure = failure; }

    /// <summary>The category suitable for mapping to the calling domain.</summary>
    public EnvelopeFailure Failure { get; }

    /// <summary>The bounded path-free diagnostic code.</summary>
    public string SafeCode => Message;

    /// <summary>Path-free category of an overridden failure, or null when no failure was overridden.</summary>
    public string? SafeCauseCode { get; private set; }

    internal Exception? RetainedCause => _retainedCause;

    internal static EnvelopeException WithRetainedCause(EnvelopeFailure failure, string code, Exception cause)
    {
        var result = new EnvelopeException(failure, code)
        {
            _retainedCause = cause,
            SafeCauseCode = cause is EnvelopeException envelope ? envelope.SafeCode
                : cause is OperationCanceledException ? "consumer-canceled" : "consumer-or-cleanup-failed",
        };
        return result;
    }

    private static string ValidateCode(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length is < 1 or > 96 || value.Any(c => c is not (>= 'a' and <= 'z') and not (>= '0' and <= '9') and not '-'))
            throw new ArgumentException("A safe lowercase ASCII code is required.", nameof(value));
        return value;
    }
}

/// <summary>Structured internal tool command. Never construct this from author or HTTP parameters.</summary>
/// <param name="GateExecutable">Absolute path of the packaged pre-exec gate.</param>
/// <param name="Executable">Absolute path of the packaged native decoder.</param>
/// <param name="Arguments">Bounded argument vector without a shell.</param>
public sealed record EnvelopeCommand(string GateExecutable, string Executable, string[] Arguments)
{
    /// <summary>Rejects malformed internal commands before opening native resources.</summary>
    public void Validate()
    {
        if (GateExecutable is null || Executable is null || Arguments is null
            || !Path.IsPathFullyQualified(GateExecutable) || !Path.IsPathFullyQualified(Executable)
            || Arguments.Length > 128 || Arguments.Any(x => x is null || x.Length > 8192 || x.Contains('\0'))
            || GateExecutable.Contains('\0') || Executable.Contains('\0'))
            throw new EnvelopeException(EnvelopeFailure.CapacityUnsupported, "native-command-not-internal");
    }
}

/// <summary>Captured owner. The kernel cgroup inode is pinned by the backend until drain.</summary>
/// <param name="Nonce">Random lowercase 128-bit nonce.</param>
/// <param name="UnitName">Nonce-scoped transient service name.</param>
/// <param name="UnitPath">Manager object path.</param>
/// <param name="InvocationId">Exact invocation identifier.</param>
/// <param name="ControlGroup">Exact cgroup v2 path, empty only for a gate that already failed.</param>
/// <param name="MainPid">Captured gate PID; exec preserves this PID.</param>
/// <param name="StartTicks">Linux process start ticks.</param>
/// <param name="CgroupDevice">Pinned cgroup device.</param>
/// <param name="CgroupInode">Pinned cgroup inode.</param>
public sealed record EnvelopeIdentity(string Nonce, string UnitName, string UnitPath, string InvocationId,
    string ControlGroup, uint MainPid, ulong StartTicks, ulong CgroupDevice, ulong CgroupInode);

/// <summary>Manager and kernel observation. Counters determine outcome; sampled RSS never admits work.</summary>
/// <param name="UnitName">Observed unit identifier.</param>
/// <param name="UnitPath">Observed manager object path.</param>
/// <param name="InvocationId">Observed invocation identifier.</param>
/// <param name="Description">Nonce-bearing description.</param>
/// <param name="ControlGroup">Observed or previously pinned cgroup path.</param>
/// <param name="MainPid">Current live main PID, or zero after exit.</param>
/// <param name="MainStartTicks">Current live main start ticks.</param>
/// <param name="CgroupDevice">Pinned directory device.</param>
/// <param name="CgroupInode">Pinned directory inode.</param>
/// <param name="MainInControlGroup">Whether the main PID belongs to this exact cgroup.</param>
/// <param name="CgroupExists">Whether the pinned cgroup still exposes its controls.</param>
/// <param name="CgroupPopulated">Kernel populated state.</param>
/// <param name="CgroupRemoved">Previously pinned directory was removed, or a failed gate has no cgroup.</param>
/// <param name="MemoryMax">Manager memory limit.</param>
/// <param name="EffectiveMemoryMax">Effective ancestor-aware memory limit.</param>
/// <param name="MemorySwapMax">Manager swap limit.</param>
/// <param name="KernelMemoryMax">Kernel memory.max.</param>
/// <param name="KernelSwapMax">Kernel memory.swap.max.</param>
/// <param name="KernelOomGroup">Kernel memory.oom.group is one.</param>
/// <param name="MemoryAccounting">Manager accounting enabled.</param>
/// <param name="Delegate">Manager delegation flag.</param>
/// <param name="Type">Service type.</param>
/// <param name="KillMode">Service kill mode.</param>
/// <param name="Restart">Service restart policy.</param>
/// <param name="OomPolicy">Service OOM policy.</param>
/// <param name="ActiveState">Manager active state.</param>
/// <param name="Result">Manager retained service result.</param>
/// <param name="ExecMainCode">CLD main exit code.</param>
/// <param name="ExecMainStatus">Main exit status or signal.</param>
/// <param name="OomKills">Cumulative OOM kills observed before cgroup removal.</param>
/// <param name="KernelKillAvailable">Pinned cgroup.kill was openable for write before decoder exec.</param>
/// <param name="ControlsComplete">All required manager and kernel controls were readable.</param>
public sealed record EnvelopeSnapshot(string UnitName, string UnitPath, string InvocationId, string Description,
    string ControlGroup, uint MainPid, ulong MainStartTicks, ulong CgroupDevice, ulong CgroupInode,
    bool MainInControlGroup, bool CgroupExists, bool CgroupPopulated, bool CgroupRemoved,
    ulong MemoryMax, ulong EffectiveMemoryMax, ulong MemorySwapMax, ulong KernelMemoryMax,
    ulong KernelSwapMax, bool KernelOomGroup, bool MemoryAccounting, bool Delegate,
    string Type, string KillMode, string Restart, string OomPolicy, string ActiveState,
    string Result, int ExecMainCode, int ExecMainStatus, ulong OomKills, bool KernelKillAvailable, bool ControlsComplete)
{
    /// <summary>Whether the manager retained a completed service state.</summary>
    public bool Terminal => ActiveState is "inactive" or "failed";
    /// <summary>Whether an observed kernel event or manager result establishes OOM.</summary>
    public bool Oom => Result == "oom-kill" || OomKills > 0;
}

/// <summary>Immutable request applied atomically before starting the gate.</summary>
/// <param name="Nonce">Random owned nonce.</param>
/// <param name="UnitName">Derived owned unit name.</param>
/// <param name="NativeLimitBytes">Frozen native memory.max partition.</param>
/// <param name="Command">Internal structured command.</param>
public sealed record EnvelopeStart(string Nonce, string UnitName, ulong NativeLimitBytes, EnvelopeCommand Command);

/// <summary>Generic finite execution policy supplied by the profile-owning caller.</summary>
/// <param name="StartTimeout">Gate capture deadline.</param>
/// <param name="IdleTimeout">Maximum time without successful owned pipe I/O.</param>
/// <param name="ExitTimeout">Natural exit deadline after consumer completion.</param>
/// <param name="DrainTimeout">Independent abort/drain deadline.</param>
/// <param name="PollInterval">Bounded supervision interval.</param>
public sealed record EnvelopeExecutionPolicy(TimeSpan StartTimeout, TimeSpan IdleTimeout, TimeSpan ExitTimeout,
    TimeSpan DrainTimeout, TimeSpan PollInterval)
{
    /// <summary>Rejects infinite, negative or internally inconsistent policy values.</summary>
    public void Validate()
    {
        if (StartTimeout <= TimeSpan.Zero || IdleTimeout <= TimeSpan.Zero || ExitTimeout <= TimeSpan.Zero
            || DrainTimeout <= TimeSpan.Zero || PollInterval <= TimeSpan.Zero || PollInterval > StartTimeout
            || PollInterval > ExitTimeout || PollInterval > DrainTimeout)
            throw new ArgumentOutOfRangeException(nameof(StartTimeout), "Finite positive deadlines are required.");
    }
}

/// <summary>Opens owned transport resources, without starting a native process.</summary>
public interface IEnvelopeBackend
{
    /// <summary>Opens the manager connection and four pipes. No unit is created here.</summary>
    ValueTask<IEnvelopeSession> OpenAsync(CancellationToken cancellationToken);
}

/// <summary>One manager connection, pinned cgroup identity, and four owned UNIX pipes.</summary>
public interface IEnvelopeSession : IAsyncDisposable
{
    /// <summary>Parent-to-tool input; the backend consumes only the initial gate byte.</summary>
    Stream Input { get; }
    /// <summary>Native pixel output.</summary>
    Stream Output { get; }
    /// <summary>Independent structured metadata output, native fd3.</summary>
    Stream Metadata { get; }
    /// <summary>Bounded diagnostic stream, never a frame database.</summary>
    Stream Diagnostics { get; }
    /// <summary>Actual total kernel pipe capacity verified using F_GETPIPE_SZ.</summary>
    long PipeBackingBytes { get; }
    /// <summary>Monotone count of successful pipe reads and writes, for inactivity supervision.</summary>
    long ProgressVersion { get; }
    /// <summary>True once a start request may have reached the manager.</summary>
    bool StartAttempted { get; }
    /// <summary>Creates the referenced service with limits, running only the blocking gate.</summary>
    ValueTask StartHeldAsync(EnvelopeStart start, CancellationToken cancellationToken);
    /// <summary>Reads retained manager state, pinned kernel controls and live PID identity.</summary>
    ValueTask<EnvelopeSnapshot> ObserveAsync(CancellationToken cancellationToken);
    /// <summary>Reconciles a failed start. Ambiguous bus timeouts must not be reported as absent.</summary>
    ValueTask<bool> ConfirmStartAbsentAsync(CancellationToken cancellationToken);
    /// <summary>Releases exactly one gate byte after both manager and kernel controls were verified.</summary>
    ValueTask ReleaseExecGateAsync(CancellationToken cancellationToken);
    /// <summary>Rechecks invocation, PID/start ticks and pinned inode before writing cgroup.kill.</summary>
    ValueTask KillVerifiedCgroupAsync(EnvelopeIdentity identity, CancellationToken cancellationToken);
    /// <summary>Releases the manager reference only after terminal, empty/removed cgroup and pipe EOF.</summary>
    ValueTask UnrefVerifiedAsync(EnvelopeIdentity identity, CancellationToken cancellationToken);
}

/// <summary>Monotone testable clock; wall-clock changes cannot extend a lease.</summary>
public interface IEnvelopeClock
{
    /// <summary>Current monotone timestamp.</summary>
    long Timestamp { get; }
    /// <summary>Elapsed monotone time from a prior timestamp.</summary>
    TimeSpan Elapsed(long before);
    /// <summary>Cancelable finite delay.</summary>
    ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

/// <summary>Stopwatch implementation of the envelope clock.</summary>
public sealed class EnvelopeClock : IEnvelopeClock
{
    /// <inheritdoc />
    public long Timestamp => System.Diagnostics.Stopwatch.GetTimestamp();
    /// <inheritdoc />
    public TimeSpan Elapsed(long before) => System.Diagnostics.Stopwatch.GetElapsedTime(before);
    /// <inheritdoc />
    public async ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        => await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
}

/// <summary>Retained failed terminal. It owns the operation/source lease through its native reservation.</summary>
/// <param name="SafeReason">Path-free failure code.</param>
/// <param name="Identity">Last captured owner, if known.</param>
/// <param name="Session">Transport and pinned native owner, deliberately not disposed.</param>
/// <param name="Reservation">Native reservation that must remain live.</param>
/// <param name="TransportReservation">Managed transport capacity that must remain live.</param>
public sealed record EnvelopeQuarantine(string SafeReason, EnvelopeIdentity? Identity, IEnvelopeSession Session,
    DecodeOperationBudget.NativeLease Reservation, DecodeOperationBudget.ManagedLease TransportReservation);

/// <summary>Supervisor-owned failed-terminal store. Retention must be durable for the worker lifetime.</summary>
public interface IEnvelopeQuarantine
{
    /// <summary>Retains all handles and leases. This never converts failed drain into success.</summary>
    void Retain(EnvelopeQuarantine record);
}
