using System.Text;
using System.Text.Json;
using MakoKaraGenerator.Processes.MediaEnvelope;

namespace M10.NativeEnvelope.Probe;

internal sealed record HelperHello(string Mode, uint Pid, uint ParentPid, ulong StartTicks,
    string ControlGroup, ulong MemoryMax, ulong SwapMax, ulong OomGroup);

internal sealed class ProbePumps
{
    private readonly EvidenceJournal _journal;
    private readonly string _scenario;
    private readonly List<HelperHello> _hellos = [];
    private readonly Dictionary<uint, ulong> _processIdentities = [];
    private readonly HashSet<string> _allocationPhases = [];
    private readonly object _gate = new();
    private Task? _all;
    internal TaskCompletionSource<HelperHello> Hello { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal byte[] Stdout { get; private set; } = [];
    internal byte[] Stderr { get; private set; } = [];
    internal Task All => _all ?? throw new InvalidOperationException();
    internal bool MetadataEof { get; private set; }
    internal bool StdoutEof { get; private set; }
    internal bool StderrEof { get; private set; }
    internal IReadOnlyList<HelperHello> Hellos { get { lock (_gate) return _hellos.ToArray(); } }
    internal IReadOnlyList<uint> SeenPids { get { lock (_gate) return _processIdentities.Keys.ToArray(); } }

    internal IReadOnlyDictionary<uint, ulong> ProcessIdentities { get { lock (_gate) return new Dictionary<uint, ulong>(_processIdentities); } }
    internal bool AggregateAllocationAuthorized { get { lock (_gate) return _allocationPhases.Contains("parent-allocation-complete")
        && _allocationPhases.Contains("child-allocation-begin"); } }

    internal ProbePumps(EvidenceJournal journal, string scenario) { _journal = journal; _scenario = scenario; }

    internal void Start(IEnvelopeSession session, CancellationToken token)
        => _all = Task.WhenAll(ReadMetadataAsync(session.Metadata, token), ReadStdoutAsync(session.Output, token),
            ReadStderrAsync(session.Diagnostics, token));

    private async Task ReadStdoutAsync(Stream stream, CancellationToken token)
    { Stdout = await ReadBoundedAsync(stream, "stdout", token).ConfigureAwait(false); StdoutEof = true; }
    private async Task ReadStderrAsync(Stream stream, CancellationToken token)
    { Stderr = await ReadBoundedAsync(stream, "stderr", token).ConfigureAwait(false); StderrEof = true; }

    private async Task<byte[]> ReadBoundedAsync(Stream stream, string pipe, CancellationToken token)
    {
        byte[] result = new byte[65536];
        int used = 0;
        while (true)
        {
            if (used == result.Length) throw new ProbeFailure("probe-fixture-output-overflow");
            int read = await stream.ReadAsync(result.AsMemory(used), token).ConfigureAwait(false);
            if (read == 0)
            {
                _journal.Write(_scenario, "fixture-pipe-eof", new { pipe, byteCount = used,
                    controlledUtf8 = Encoding.UTF8.GetString(result, 0, used) });
                return result[..used];
            }
            used += read;
        }
    }

    private async Task ReadMetadataAsync(Stream stream, CancellationToken token)
    {
        byte[] scratch = new byte[4096], record = new byte[8192];
        int used = 0, records = 0;
        try
        {
            while (true)
            {
                int count = await stream.ReadAsync(scratch, token).ConfigureAwait(false);
                if (count == 0)
                {
                    if (used != 0) throw new ProbeFailure("probe-fixture-metadata-truncated");
                    MetadataEof = true;
                    _journal.Write(_scenario, "fixture-pipe-eof", new { pipe = "metadata", records });
                    if (!Hello.Task.IsCompleted) throw new ProbeFailure("probe-fixture-hello-missing");
                    return;
                }
                for (int index = 0; index < count; index++)
                {
                    byte value = scratch[index];
                    if (value == (byte)'\n')
                    {
                        if (++records > 32) throw new ProbeFailure("probe-fixture-metadata-count");
                        using JsonDocument json = JsonDocument.Parse(record.AsMemory(0, used));
                        ProcessRecord(json.RootElement);
                        used = 0;
                    }
                    else
                    {
                        if (used == record.Length) throw new ProbeFailure("probe-fixture-metadata-size");
                        record[used++] = value;
                    }
                }
            }
        }
        catch (Exception error)
        {
            Hello.TrySetException(error);
            Waiting.TrySetException(error);
            throw;
        }
    }

    private void ProcessRecord(JsonElement element)
    {
        string kind = element.GetProperty("kind").GetString() ?? throw new ProbeFailure("probe-fixture-record-kind");
        uint pid = element.GetProperty("pid").GetUInt32();
        ulong ticks = element.GetProperty("startTicks").GetUInt64();
        if (pid == 0 || ticks == 0) throw new ProbeFailure("probe-fixture-process-identity-missing");
        _journal.Write(_scenario, "fixture-metadata", element.Clone());
        lock (_gate)
        {
            if (_processIdentities.TryGetValue(pid, out ulong previous) && previous != ticks)
                throw new ProbeFailure("probe-fixture-pid-reused-during-stream");
            _processIdentities[pid] = ticks;
            if (kind.Contains("allocation", StringComparison.Ordinal))
            {
                if (element.GetProperty("bytes").GetUInt64() != 40UL * 1024 * 1024)
                    throw new ProbeFailure("probe-fixture-allocation-size-mismatch");
                _allocationPhases.Add(kind);
            }
        }
        if (kind == "hello")
        {
            var value = new HelperHello(element.GetProperty("mode").GetString() ?? "", pid,
                element.GetProperty("ppid").GetUInt32(), ticks, element.GetProperty("controlGroup").GetString() ?? "",
                element.GetProperty("memoryMax").GetUInt64(), element.GetProperty("swapMax").GetUInt64(),
                element.GetProperty("oomGroup").GetUInt64());
            lock (_gate) _hellos.Add(value);
            Hello.TrySetResult(value);
        }
        else if (kind == "waiting") Waiting.TrySetResult();
    }
}
