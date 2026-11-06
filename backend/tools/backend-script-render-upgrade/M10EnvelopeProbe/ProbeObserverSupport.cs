using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MakoKaraGenerator.Processes.MediaEnvelope;

namespace M10.NativeEnvelope.Probe;

internal static class ObserverSupport
{
    internal const ulong NativeBytes = 64UL * 1024 * 1024;
    internal static readonly JsonSerializerOptions Json = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16,
    };

    internal static object Safe(Exception error) => error switch
    {
        EnvelopeException value => new { category = value.Failure.ToString(), code = value.SafeCode },
        ProbeFailure => new { category = "probe", code = error.Message },
        OperationCanceledException => new { category = "deadline", code = "probe-deadline" },
        _ => new { category = error.GetType().Name, code = "probe-operation-failed" },
    };

    internal static void Require(bool condition, string code)
    { if (!condition) throw new ProbeFailure(code); }

    internal static bool Drained(EnvelopeSnapshot value) => value.Terminal && !value.CgroupPopulated
        && (value.CgroupExists || value.CgroupRemoved);

    internal static void Identity(EnvelopeSnapshot value, EnvelopeIdentity expected)
    {
        Require(value.UnitName == expected.UnitName && value.UnitPath == expected.UnitPath
            && value.InvocationId == expected.InvocationId && value.ControlGroup == expected.ControlGroup
            && value.Description == "Mako media envelope " + expected.Nonce
            && value.CgroupDevice == expected.CgroupDevice && value.CgroupInode == expected.CgroupInode
            && (value.MainPid == 0 || value.MainPid == expected.MainPid
                && value.MainStartTicks == expected.StartTicks && value.MainInControlGroup),
            "observer-probe-original-identity-changed");
    }

    internal static void Hello(HelperHello hello, EnvelopeIdentity identity, string mode)
    {
        Require(hello.Mode == mode && hello.Pid == identity.MainPid && hello.StartTicks == identity.StartTicks
            && hello.ControlGroup == identity.ControlGroup && hello.MemoryMax == NativeBytes
            && hello.SwapMax == 0 && hello.OomGroup == 1, "observer-probe-helper-owner-mismatch");
    }

    internal static async Task<EnvelopeSnapshot> DrainAsync(Func<CancellationToken, ValueTask<EnvelopeSnapshot>> observe,
        EnvelopeIdentity expected, CancellationToken token)
    {
        while (true)
        {
            EnvelopeSnapshot value = await observe(token).ConfigureAwait(false);
            Identity(value, expected);
            if (Drained(value)) return value;
            await Task.Delay(25, token).ConfigureAwait(false);
        }
    }

    internal static void MakePrivateDirectory(string path)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        if (Directory.Exists(path) || File.Exists(path)) throw new ProbeFailure("observer-probe-evidence-exists");
        Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    internal static void Summary(string directory, object value) => AtomicJson(directory, "summary.json", value);

    internal static void AtomicJson(string directory, string name, object value)
    {
        string temporary = Path.Combine(directory, name + ".next");
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None,
            4096, FileOptions.WriteThrough))
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
            stream.Write(bytes);
            stream.Flush(true);
        }
        File.Move(temporary, Path.Combine(directory, name), true);
    }
}

internal sealed record ProbeProcess(uint Pid, uint ParentPid, ulong StartTicks, string Executable)
{
    internal static ProbeProcess Read(uint pid)
    {
        string prefix = "/proc/" + pid.ToString(CultureInfo.InvariantCulture);
        string stat = File.ReadAllText(prefix + "/stat");
        int last = stat.LastIndexOf(')');
        ObserverSupport.Require(last >= 0, "observer-probe-stat-invalid");
        string[] fields = stat[(last + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        ObserverSupport.Require(fields.Length >= 20, "observer-probe-stat-short");
        string exe = new FileInfo(prefix + "/exe").ResolveLinkTarget(true)?.FullName
            ?? throw new ProbeFailure("observer-probe-exe-unresolved");
        return new(pid, uint.Parse(fields[1], CultureInfo.InvariantCulture),
            ulong.Parse(fields[19], CultureInfo.InvariantCulture), exe);
    }
}

internal sealed class ProbePidFd
{
    private int _fd;
    internal ProbeProcess Expected { get; }
    internal bool OwnedChild { get; }
    internal ProbePidFd(uint pid, ulong expectedTicks, bool ownedChild)
    {
        Expected = ProbeProcess.Read(pid);
        ObserverSupport.Require(Expected.StartTicks == expectedTicks
            && (!ownedChild || Expected.ParentPid == Environment.ProcessId), "observer-probe-pidfd-owner-mismatch");
        _fd = ProbeDiagnostics.OpenPidFd(pid);
        // Keep this instance in the caller registry before construction is used for any signal.
        try
        {
            ObserverSupport.Require(ProbeProcess.Read(pid) == Expected && !Exited,
                "observer-probe-pidfd-capture-race");
            OwnedChild = ownedChild;
        }
        catch { ProbeDiagnostics.CloseFd(_fd); _fd = -1; throw; }
    }
    internal bool Exited => _fd >= 0 ? ProbeDiagnostics.Exited(_fd) : throw new ObjectDisposedException(nameof(ProbePidFd));
    internal void KillOwnedChild()
    {
        ObserverSupport.Require(OwnedChild && !Exited && ProbeProcess.Read(Expected.Pid) == Expected,
            "observer-probe-signal-owner-no-longer-live");
        ProbeDiagnostics.KillOwned(_fd);
    }
    internal void Close()
    { if (_fd < 0) return; int fd = _fd; _fd = -1; ProbeDiagnostics.CloseFd(fd); }
}

internal sealed record RefSender(string Sender, uint Pid);
internal sealed record RefWitness(string Invocation, string Witness, string ManagerVersion, RefSender[] Entries);

internal sealed class ProbeBusWitness
{
    private IntPtr _bus;
    internal string? ManagerVersion { get; private set; }
    internal ProbeBusWitness()
    {
        try { _bus = ProbeDiagnostics.OpenBus(); }
        catch (Exception error) when (error is TypeInitializationException or DllNotFoundException
            or EntryPointNotFoundException or BadImageFormatException)
        {
            throw new EnvelopeException(EnvelopeFailure.CapacityUnsupported, "observer-probe-diagnostics-unavailable");
        }
    }
    internal RefWitness Sample(EnvelopeIdentity expected)
    {
        RefWitness value = ProbeDiagnostics.Sample(_bus, expected);
        ManagerVersion = value.ManagerVersion;
        return value;
    }
    internal void Close() { if (_bus == IntPtr.Zero) return; ProbeDiagnostics.CloseBus(_bus); _bus = IntPtr.Zero; }
}

internal static class ProbeDiagnostics
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    internal struct NativeEntry
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Sender;
        public uint Pid;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    internal struct NativeSample
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 33)] public string Invocation;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Witness;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Version;
        public uint Count;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)] public NativeEntry[] Entries;
    }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int OpenDelegate(out IntPtr bus);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void CloseDelegate(IntPtr bus);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SizeDelegate();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SampleDelegate(IntPtr bus, [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string expected, out NativeSample sample);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int PidDelegate(uint pid);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int FdDelegate(int fd);
    private static readonly IntPtr Library = NativeLibrary.Load(Path.Combine(AppContext.BaseDirectory,
        "media-tools", "libmkg_probe_diagnostics.so"));
    private static T Export<T>(string symbol) where T : Delegate
        => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(Library, symbol));
    private static readonly OpenDelegate Open = Export<OpenDelegate>("mkg_probe_witness_open");
    private static readonly CloseDelegate Close = Export<CloseDelegate>("mkg_probe_witness_close");
    private static readonly SampleDelegate Read = Export<SampleDelegate>("mkg_probe_witness_sample");
    private static readonly PidDelegate PidOpen = Export<PidDelegate>("mkg_probe_pidfd_open");
    private static readonly FdDelegate PidExited = Export<FdDelegate>("mkg_probe_pidfd_exited");
    private static readonly FdDelegate PidKill = Export<FdDelegate>("mkg_probe_owned_pidfd_kill");
    private static readonly FdDelegate FdClose = Export<FdDelegate>("mkg_probe_close_fd");
    private static readonly FdDelegate Peer = Export<FdDelegate>("mkg_probe_peer_is_self_uid");
    private static void Check(int result)
    {
        if (result == -116) throw new EnvelopeException(EnvelopeFailure.OwnershipLost,
            "observer-probe-witness-identity-changed");
        if (result < 0) throw new EnvelopeException(EnvelopeFailure.CapacityUnsupported,
            "observer-probe-diagnostics-unavailable");
    }
    internal static IntPtr OpenBus()
    {
        ObserverSupport.Require(Export<SizeDelegate>("mkg_probe_sample_size")() == Marshal.SizeOf<NativeSample>(),
            "observer-probe-diagnostics-abi-mismatch");
        Check(Open(out IntPtr bus));
        return bus;
    }
    internal static void CloseBus(IntPtr bus) => Close(bus);
    internal static RefWitness Sample(IntPtr bus, EnvelopeIdentity expected)
    {
        Check(Read(bus, expected.UnitPath, expected.InvocationId, out NativeSample sample));
        ObserverSupport.Require(sample.Count <= 8 && sample.Invocation == expected.InvocationId,
            "observer-probe-witness-invalid");
        RefSender[] senders = sample.Entries.Take((int)sample.Count).Select(x => new RefSender(x.Sender, x.Pid)).ToArray();
        ObserverSupport.Require(senders.All(x => x.Sender != sample.Witness), "observer-probe-witness-acquired-ref");
        return new(sample.Invocation, sample.Witness, sample.Version, senders);
    }
    internal static int OpenPidFd(uint pid) { int value = PidOpen(pid); Check(value); return value; }
    internal static bool Exited(int fd) { int value = PidExited(fd); Check(value); return value == 1; }
    internal static void KillOwned(int fd) => Check(PidKill(fd));
    internal static void CloseFd(int fd) => Check(FdClose(fd));
    internal static bool SameUid(int fd) { int value = Peer(fd); Check(value); return value == 1; }
}

internal sealed record ProbeMessage(int Version, string Run, string Scenario, long Sequence, string Kind, JsonElement Data)
{
    internal T Payload<T>() => Data.Deserialize<T>(ObserverSupport.Json)
        ?? throw new ProbeFailure("observer-probe-control-data-missing");
}

internal sealed class ProbeWire(Stream input, Stream output, string run, string scenario)
{
    private int _received, _total;
    internal async Task SendAsync(long sequence, string kind, object data, CancellationToken token)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new ProbeMessage(1, run, scenario, sequence, kind,
            JsonSerializer.SerializeToElement(data, ObserverSupport.Json)), ObserverSupport.Json);
        ObserverSupport.Require(bytes.Length <= 16384, "observer-probe-control-write-overflow");
        await output.WriteAsync(bytes, token).ConfigureAwait(false);
        await output.WriteAsync(new byte[] { 10 }, token).ConfigureAwait(false);
        await output.FlushAsync(token).ConfigureAwait(false);
    }
    internal async Task<ProbeMessage?> ReadAsync(CancellationToken token)
    {
        byte[] buffer = new byte[16384], one = new byte[1];
        int used = 0;
        while (true)
        {
            int count = await input.ReadAsync(one, token).ConfigureAwait(false);
            if (count == 0)
            {
                ObserverSupport.Require(used == 0, "observer-probe-control-truncated");
                return null;
            }
            ObserverSupport.Require(++_total <= 1048576, "observer-probe-control-total-overflow");
            if (one[0] == 10) break;
            ObserverSupport.Require(used < buffer.Length, "observer-probe-control-line-overflow");
            buffer[used++] = one[0];
        }
        ObserverSupport.Require(++_received <= 64, "observer-probe-control-count-overflow");
        using JsonDocument document = JsonDocument.Parse(buffer.AsMemory(0, used), new JsonDocumentOptions { MaxDepth = 16 });
        RejectDuplicates(document.RootElement);
        ProbeMessage value = document.RootElement.Deserialize<ProbeMessage>(ObserverSupport.Json)
            ?? throw new ProbeFailure("observer-probe-control-missing");
        ObserverSupport.Require(value.Version == 1 && value.Run == run && value.Scenario == scenario
            && value.Sequence > 0 && value.Kind is not null && value.Kind.Length <= 64,
            "observer-probe-control-envelope-invalid");
        return value;
    }
    private static void RejectDuplicates(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in value.EnumerateObject())
            { ObserverSupport.Require(seen.Add(property.Name), "observer-probe-control-duplicate-field"); RejectDuplicates(property.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (JsonElement item in value.EnumerateArray()) RejectDuplicates(item);
    }
}
