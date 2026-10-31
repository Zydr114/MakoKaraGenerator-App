using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace MakoKaraGenerator.Processes;

/// <summary>Auditable Linux process identity; names and executable paths are not ownership signals.</summary>
public sealed record ProcessIdentity(int ProcessId, ulong StartTicks, int ProcessGroupId);

/// <summary>One previously verified private Worker group, including descendants after its leader exits.</summary>
public sealed class OwnedProcessGroup
{
    private readonly Dictionary<int, ulong> _observed = [];
    private OwnedProcessGroup(ProcessIdentity leader) { Leader = leader; _observed[leader.ProcessId] = leader.StartTicks; }

    /// <summary>The original group leader identity captured while it was alive.</summary>
    public ProcessIdentity Leader { get; }

    /// <summary>Capture only a group whose ID equals the exact started process PID.</summary>
    public static OwnedProcessGroup? TryCapture(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (!LinuxProcessGuards.IsSupported) return null;
        Entry? entry = ReadEntry(process.Id);
        return entry is { } value && value.ParentId == Environment.ProcessId && value.Identity.ProcessGroupId == process.Id
            ? new(value.Identity) : null;
    }

    /// <summary>Observe identities belonging to this private group, rejecting a replaced leader.</summary>
    public IReadOnlyList<ProcessIdentity> ObserveMembers()
    {
        VerifyLeader();
        List<ProcessIdentity> members = [];
        foreach (string directory in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(directory), NumberStyles.None, CultureInfo.InvariantCulture, out int pid)) continue;
            Entry? entry = ReadEntry(pid);
            if (entry is not { } value || value.Identity.ProcessGroupId != Leader.ProcessGroupId) continue;
            if (value.Identity.StartTicks < Leader.StartTicks) throw new IOException("Private group contains an identity predating its leader.");
            if (_observed.TryGetValue(pid, out ulong previous) && previous != value.Identity.StartTicks)
                throw new IOException("Observed group member PID has been replaced.");
            _observed[pid] = value.Identity.StartTicks;
            members.Add(value.Identity);
        }
        return members;
    }

    /// <summary>Kill only the registered group and drain owned descendants; never reap the .NET-owned leader.</summary>
    public async Task KillAndDrainAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        VerifyLeader();
        _ = ObserveMembers();
        if (Native.Kill(-Leader.ProcessGroupId, 9) != 0 && Marshal.GetLastPInvokeError() != 3)
            throw new IOException("Private Worker group termination failed.");
        Stopwatch watch = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool pending = false;
            foreach (ProcessIdentity member in ObserveMembers())
            {
                Entry? entry = ReadEntry(member.ProcessId);
                if (entry is not { } actual || actual.Identity != member) continue;
                if (member.ProcessId == Leader.ProcessId)
                {
                    // The caller's System.Diagnostics.Process owns this wait status.
                    if (actual.State is not ('Z' or 'X')) pending = true;
                    continue;
                }
                if (actual.State is 'Z' or 'X')
                {
                    // Exact registered member only; never waitpid(-1) or another group.
                    _ = Native.WaitPid(member.ProcessId, out _, 1);
                    if (ReadEntry(member.ProcessId) is not null) pending = true;
                }
                else pending = true;
            }
            if (!pending) return;
            if (watch.Elapsed >= timeout) throw new IOException("Private Worker descendants did not drain before the deadline.");
            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
        }
    }

    private void VerifyLeader()
    {
        Entry? current = ReadEntry(Leader.ProcessId);
        if (current is { } value && value.Identity != Leader)
            throw new IOException("Private Worker group leader identity changed; refusing termination.");
    }

    private static Entry? ReadEntry(int pid)
    {
        try
        {
            string stat = File.ReadAllText("/proc/" + pid.ToString(CultureInfo.InvariantCulture) + "/stat");
            string[] values = stat[(stat.LastIndexOf(')') + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return new(new(pid, ulong.Parse(values[19], CultureInfo.InvariantCulture), int.Parse(values[2], CultureInfo.InvariantCulture)),
                values[0][0], int.Parse(values[1], CultureInfo.InvariantCulture));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or FormatException or IndexOutOfRangeException) { return null; }
    }

    private sealed record Entry(ProcessIdentity Identity, char State, int ParentId);
}
