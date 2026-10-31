using System.Diagnostics;
using System.Globalization;

namespace MakoKaraGenerator.Processes;

/// <summary>Exact legacy child identities when the Worker has no private group handshake.</summary>
public sealed class OwnedProcessTree
{
    private readonly Dictionary<int, ProcessIdentity> _members = [];
    private OwnedProcessTree(ProcessIdentity leader) { Leader = leader; }
    /// <summary>The verified direct child whose .NET Process owns the wait status.</summary>
    public ProcessIdentity Leader { get; }

    /// <summary>Capture only this host's exact direct child, without claiming its shared process group.</summary>
    public static OwnedProcessTree? TryCapture(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (!LinuxProcessGuards.IsSupported) return null;
        Entry? current = Read(process.Id);
        return current is { Parent: var parent } && parent == Environment.ProcessId ? new(current.Identity) : null;
    }

    /// <summary>Capture ancestry while the leader still exists; never infer ownership from names or shared groups.</summary>
    public void ObserveDescendants()
    {
        Entry? leader = Read(Leader.ProcessId);
        if (leader is null) return;
        if (!SameProcess(leader.Identity, Leader)) throw new IOException("Owned tree leader PID was replaced.");
        List<Entry> entries = [];
        foreach (string path in Directory.EnumerateDirectories("/proc"))
            if (int.TryParse(Path.GetFileName(path), NumberStyles.None, CultureInfo.InvariantCulture, out int pid)
                && Read(pid) is { } entry) entries.Add(entry);
        Dictionary<int, ProcessIdentity> parents = new() { [Leader.ProcessId] = Leader };
        Dictionary<int, ProcessIdentity> candidates = [];
        bool added;
        do
        {
            added = false;
            foreach (Entry entry in entries)
            {
                if (!parents.TryGetValue(entry.Parent, out ProcessIdentity? parent) || parents.ContainsKey(entry.Identity.ProcessId)) continue;
                // Verify each live ancestry link against the scanned identities,
                // rather than trusting a PID whose parent exited during /proc enumeration.
                Entry? actual = Read(entry.Identity.ProcessId);
                if (!SameProcess(Read(entry.Parent)?.Identity, parent) || !SameProcess(actual?.Identity, entry.Identity) || actual!.Parent != entry.Parent) continue;
                if (entry.Identity.StartTicks < Leader.StartTicks) throw new IOException("Owned descendant predates its leader.");
                if (_members.TryGetValue(entry.Identity.ProcessId, out ProcessIdentity? previous) && !SameProcess(previous, entry.Identity))
                    throw new IOException("Owned descendant PID was replaced.");
                parents.Add(entry.Identity.ProcessId, entry.Identity);
                candidates[entry.Identity.ProcessId] = entry.Identity;
                added = true;
            }
        } while (added);
        if (!SameProcess(Read(Leader.ProcessId)?.Identity, Leader)) return;
        foreach ((int pid, ProcessIdentity identity) in candidates) _members[pid] = identity;
    }

    /// <summary>Terminate and reap only captured descendants. The caller's Process retains the leader wait status.</summary>
    public async Task KillAndDrainAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        ObserveDescendants();
        Signal(Leader);
        foreach (ProcessIdentity member in _members.Values) Signal(member);
        Stopwatch watch = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool pending = false;
            foreach (ProcessIdentity member in _members.Values)
            {
                Entry? actual = Read(member.ProcessId);
                if (actual is null || !SameProcess(actual.Identity, member)) continue;
                if (actual.Parent == Environment.ProcessId && actual.State is 'Z' or 'X')
                    _ = Native.WaitPid(member.ProcessId, out _, 1);
                if (SameProcess(Read(member.ProcessId)?.Identity, member)) pending = true;
            }
            if (!pending) return;
            if (watch.Elapsed >= timeout) throw new IOException("Owned legacy descendants did not drain.");
            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
        }
    }

    private static void Signal(ProcessIdentity identity)
    {
        Entry? actual = Read(identity.ProcessId);
        if (SameProcess(actual?.Identity, identity)) _ = Native.Kill(identity.ProcessId, 9);
    }

    // A legacy process may legitimately setpgid after Process.Start. This tracker
    // signals individual PIDs only; private-group identity remains OwnedProcessGroup's contract.
    private static bool SameProcess(ProcessIdentity? actual, ProcessIdentity expected) => actual is not null
        && actual.ProcessId == expected.ProcessId && actual.StartTicks == expected.StartTicks;

    private static Entry? Read(int pid)
    {
        try
        {
            string stat = File.ReadAllText("/proc/" + pid.ToString(CultureInfo.InvariantCulture) + "/stat");
            string[] values = stat[(stat.LastIndexOf(')') + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return new(new(pid, ulong.Parse(values[19], CultureInfo.InvariantCulture), int.Parse(values[2], CultureInfo.InvariantCulture)),
                int.Parse(values[1], CultureInfo.InvariantCulture), values[0][0]);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or FormatException or IndexOutOfRangeException) { return null; }
    }
    private sealed record Entry(ProcessIdentity Identity, int Parent, char State);
}
