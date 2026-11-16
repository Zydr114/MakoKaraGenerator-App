using System.Diagnostics;

namespace MakoKaraGenerator.Processes;

/// <summary>Cross-platform ownership and termination guard for a started Worker process tree.</summary>
public interface IWorkerProcessGuard : IAsyncDisposable
{
    /// <summary>Leader process identifier verified at startup.</summary>
    int LeaderProcessId { get; }

    /// <summary>Terminates all owned processes in the guard and waits until none remain active.</summary>
    Task KillAndDrainAsync(TimeSpan timeout, CancellationToken cancellationToken = default);
}

/// <summary>Platform-neutral process guard helpers.</summary>
public static class ProcessGuards
{
    /// <summary>Whether the current host supports private Worker process isolation guards.</summary>
    public static bool IsSupported => LinuxProcessGuards.IsSupported || WindowsJobObjectGuard.IsSupported;

    /// <summary>Initializes or verifies the current process's private isolation group/job.</summary>
    public static bool InitializeCurrentProcessGuard()
    {
        if (LinuxProcessGuards.IsSupported) return LinuxProcessGuards.InitializeCurrentProcessGroup();
        if (WindowsJobObjectGuard.IsSupported) return WindowsJobObjectGuard.IsCurrentProcessGuarded();
        return false;
    }

    /// <summary>
    /// Validates or applies the Worker media file size limit. On Linux x64 this lowers <c>RLIMIT_FSIZE</c>;
    /// on Windows x64 (which has no <c>RLIMIT_FSIZE</c>) it requires a verified private Job guard and defers
    /// byte-limit enforcement to the Worker export layer.
    /// </summary>
    public static bool ApplyCurrentFileSizeLimit(long maximumBytes, bool hasVerifiedProcessGuard)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        if (LinuxProcessGuards.IsSupported) return LinuxProcessGuards.ApplyCurrentFileSizeLimit(maximumBytes);
        if (WindowsJobObjectGuard.IsSupported) return hasVerifiedProcessGuard;
        return false;
    }

    /// <summary>Enables child reaping on platforms that require it (Linux subreaper; no-op on Windows Job Objects).</summary>
    public static bool TryEnableChildReaping()
    {
        if (LinuxProcessGuards.IsSupported) return LinuxProcessGuards.TryEnableChildReaping();
        if (WindowsJobObjectGuard.IsSupported) return true;
        return false;
    }
}

/// <summary>Adapts <see cref="OwnedProcessGroup"/> to <see cref="IWorkerProcessGuard"/> without changing Linux semantics.</summary>
public sealed class LinuxOwnedProcessGroupGuard(OwnedProcessGroup group) : IWorkerProcessGuard
{
    /// <summary>Underlying verified Linux process group.</summary>
    public OwnedProcessGroup Group { get; } = group ?? throw new ArgumentNullException(nameof(group));
    /// <inheritdoc/>
    public int LeaderProcessId => Group.Leader.ProcessId;

    /// <inheritdoc/>
    public Task KillAndDrainAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        => Group.KillAndDrainAsync(timeout, cancellationToken);

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
