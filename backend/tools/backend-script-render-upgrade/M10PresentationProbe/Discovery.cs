using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using MakoKaraGenerator.Core.Media;
using MakoKaraGenerator.Media;
using MakoKaraGenerator.Processes.MediaEnvelope;

namespace M10.Presentation.Probe;

/// <summary>Discovery only: the raw parser learns the finite actual loaded closure. It does not accept a source.</summary>
internal sealed class Discovery(string gate, string helper, string fixtureRoot, FixtureSet fixtures,
    EvidenceJournal journal, long nativeBytes) : IDisposable
{
    private const long ManagedBytes = 24L * 1024 * 1024;
    private readonly Dictionary<string, LoadedFile> _files = new(StringComparer.Ordinal);
    // The closure union survives individual helpers, so its reservation survives them too.
    // Native128 + operation-managed24 + retained-host-map4 MiB remain below the fixed 256 MiB ceiling.
    private readonly IMediaMemoryReservation _hostMapReservation = new MediaWorkingSetBudget(
        MediaAbCapacityV1.MaximumDecodeWorkingBytes).Reserve(4 * 1024 * 1024, MediaWorkingSetPurpose.Metadata);
    private int _pathBytes;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal async Task<ClosureManifest> RunAsync(string fixtureManifest, CancellationToken token)
    {
        AddLocalExecutable(gate);
        AddLocalExecutable(helper);
        foreach (FixtureCase item in fixtures.Cases)
            foreach (bool inspect in new[] { true, false }) await RunOneAsync(item, inspect, token).ConfigureAwait(false);
        LoadedFile[] files = _files.Values.OrderBy(f => f.Path, StringComparer.Ordinal)
            .Select((file, i) => file with { LogicalId = "loaded-" + i.ToString("D4", CultureInfo.InvariantCulture) }).ToArray();
        foreach (LoadedFile file in files)
            if (new FileInfo(file.Path).Length != file.ByteLength || ProbeFiles.Hash(file.Path) != file.Sha256)
                throw new ProbeFailure("probe-discovered-file-changed");
        // Gate is observed before exec and pinned separately; helper epochs enumerate the post-exec closure.
        // This manifest is a discovery output. Verification must rerun every fixture through the true runner.
        return new(1, "actual-type4-type5-discovery-not-acceptance", gate, helper, ProbeFiles.Hash(fixtureManifest), files);
    }

    private void AddLocalExecutable(string path)
    {
        if (!Path.IsPathFullyQualified(path) || !File.Exists(path) || new FileInfo(path).LinkTarget is not null)
            throw new ProbeFailure("probe-executable-location-invalid");
        Add(new(path, new FileInfo(path).Length, ProbeFiles.Hash(path), "pending", true));
    }

    private void Add(LoadedFile file)
    {
        if (_files.TryGetValue(file.Path, out LoadedFile? previous))
        {
            if (previous.ByteLength != file.ByteLength || previous.Sha256 != file.Sha256)
                throw new ProbeFailure("probe-loaded-identity-changed-during-discovery");
            return;
        }
        int pathBytes = StrictUtf8.GetByteCount(file.Path);
        if (_files.Count >= 1024 || pathBytes > 4096 || _pathBytes + pathBytes > 64 * 1024)
            throw new ProbeFailure("probe-discovery-closure-capacity-exceeded");
        _pathBytes += pathBytes;
        _files.Add(file.Path, file);
    }

    private async Task RunOneAsync(FixtureCase item, bool inspect, CancellationToken caller)
    {
        string name = "discovery-" + item.Name + (inspect ? "-inspect" : "-decode");
        using var duration = CancellationTokenSource.CreateLinkedTokenSource(caller);
        duration.CancelAfter(TimeSpan.FromSeconds(40));
        var raw = new PrivateFixtureLease(ProbeFiles.Child(fixtureRoot, item.File), item);
        await using var source = new MediaEnvelopeFileLease(raw);
        var operation = new DecodeOperationBudget(MediaAbCapacityV1.MaximumDecodeWorkingBytes, ManagedBytes,
            [nativeBytes], MediaAbCapacityV1.MaximumOwnedDecodeProcesses, source.AcquireHold());
        var quarantine = new JournalQuarantine(journal, name);
        var backend = new AuditedEnvelopeBackend(new LinuxSystemdEnvelopeBackend(), journal, name, (ulong)nativeBytes);
        var owner = new OwnedNativeEnvelope(backend, quarantine, new(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(10)));
        journal.Write(name, "discovery-begin", new { item.Sha256, item.Family, inspect, nativeBytes, managedBytes = ManagedBytes });
        Exception? failure = null;
        try
        {
            await owner.RunAsync(operation.BeginNative(0), new(gate, helper,
                [item.Family.ToString(CultureInfo.InvariantCulture), "0", inspect ? "inspect" : "decode",
                    source.TrustedLocalPath, "mkg-libav-same-frame-v1"]), async (session, token) =>
            {
                // Covers fixed arrays, the bounded per-epoch set, transient path strings, and JSON records.
                using var backing = operation.ReserveManaged(4 * 1024 * 1024);
                using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
                Task diagnostics = DrainDiagnosticsAsync(session.Diagnostics, journal, name, stop.Token);
                Task records = ReadRecordsAsync(session, item, inspect, name, stop.Token);
                Task[] pumps = [diagnostics, records];
                try
                {
                    Task first = await Task.WhenAny(pumps).ConfigureAwait(false);
                    await first.ConfigureAwait(false);
                    await Task.WhenAll(pumps).ConfigureAwait(false);
                }
                finally
                {
                    stop.Cancel();
                    try { await Task.WhenAll(pumps).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
                }
            }, duration.Token).ConfigureAwait(false);
        }
        catch (Exception error) { failure = error; }
        if (quarantine.Count != 0) Program.RetainFailure(quarantine, operation, source);
        await operation.ReleaseSourceAsync().ConfigureAwait(false);
        AuditDrain.Require(backend, quarantine, journal, name, operation.LiveManagedBytes, 0);
        if (operation.ActiveBorrows != 0) throw new ProbeFailure("probe-discovery-borrow-remains");
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        if (ProbeFiles.Hash(source.TrustedLocalPath) != item.Sha256) throw new ProbeFailure("probe-source-changed-after-discovery");
        journal.Write(name, "discovery-drained-not-acceptance", new { loadedFiles = _files.Count });
    }

    private async Task ReadRecordsAsync(IEnvelopeSession session, FixtureCase item, bool inspect,
        string name, CancellationToken token)
    {
        byte[] record = new byte[16 * 1024];
        byte[] scratch = new byte[64 * 1024];
        byte[] ack = [inspect ? (byte)'S' : (byte)'N'];
        var epoch = new HashSet<string>(StringComparer.Ordinal);
        bool sealedEpoch = false;
        long frames = 0;
        int epochs = 0;
        while (true)
        {
            await session.Metadata.ReadExactlyAsync(record.AsMemory(0, 16), token).ConfigureAwait(false);
            if (!record.AsSpan(0, 8).SequenceEqual("MKGPRE1\0"u8)) throw new ProbeFailure("probe-discovery-wire-magic");
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(8));
            uint length = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(12));
            if (length > record.Length) throw new ProbeFailure("probe-discovery-record-capacity");
            await session.Metadata.ReadExactlyAsync(record.AsMemory(0, (int)length), token).ConfigureAwait(false);
            switch (type)
            {
                case 4:
                    if (sealedEpoch) { epoch.Clear(); sealedEpoch = false; }
                    if (length < 44) throw new ProbeFailure("probe-discovery-file-truncated");
                    long bytes = BinaryPrimitives.ReadInt64LittleEndian(record);
                    uint pathBytes = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(40));
                    if (bytes <= 0 || pathBytes is 0 or > 4096 || pathBytes != length - 44 || epoch.Count >= 1024)
                        throw new ProbeFailure("probe-discovery-file-capacity");
                    string path = StrictUtf8.GetString(record, 44, (int)pathBytes);
                    if (!Path.IsPathFullyQualified(path) || path.Contains('\0') || !epoch.Add(path))
                        throw new ProbeFailure("probe-discovery-file-path");
                    string hash = Convert.ToHexStringLower(record.AsSpan(8, 32));
                    Add(new(path, bytes, hash, "pending", path == gate || path == helper));
                    journal.Write(name, "actual-loaded-file-discovered", new { epoch = epochs, path, bytes, sha256 = hash });
                    break;
                case 5:
                    if (length != 0 || epoch.Count == 0 || ++epochs > 132) throw new ProbeFailure("probe-discovery-epoch-invalid");
                    sealedEpoch = true;
                    journal.Write(name, "actual-loaded-epoch-end", new { epoch = epochs, count = epoch.Count });
                    break;
                case 2:
                    if (!sealedEpoch || length != 224 || BinaryPrimitives.ReadInt64LittleEndian(record) != frames)
                        throw new ProbeFailure("probe-discovery-frame-invalid");
                    sealedEpoch = false; epoch.Clear();
                    int width = BinaryPrimitives.ReadInt32LittleEndian(record.AsSpan(52));
                    int height = BinaryPrimitives.ReadInt32LittleEndian(record.AsSpan(56));
                    int planeCount = BinaryPrimitives.ReadInt32LittleEndian(record.AsSpan(136));
                    if (width is < 1 or > 128 || height is < 1 or > 128 || planeCount is not (1 or 3) || frames >= 64)
                        throw new ProbeFailure("probe-discovery-frame-capacity");
                    long planeBytes = 0;
                    foreach (int offset in new[] { 144, 152, 160 })
                    {
                        int plane = BinaryPrimitives.ReadInt32LittleEndian(record.AsSpan(offset));
                        if (plane < 0 || plane > 128 * 128 * 8) throw new ProbeFailure("probe-discovery-plane-capacity");
                        planeBytes += plane;
                    }
                    if (!inspect)
                        while (planeBytes > 0)
                        {
                            int take = (int)Math.Min(planeBytes, scratch.Length);
                            await session.Output.ReadExactlyAsync(scratch.AsMemory(0, take), token).ConfigureAwait(false);
                            planeBytes -= take;
                        }
                    journal.Write(name, "discovery-native-frame-metadata", new { ordinal = frames,
                        rawMetadataBase64 = Convert.ToBase64String(record, 0, (int)length) });
                    frames++;
                    await session.Input.WriteAsync(ack, token).ConfigureAwait(false);
                    await session.Input.FlushAsync(token).ConfigureAwait(false);
                    break;
                case 3:
                    if (!sealedEpoch || length != 24 || BinaryPrimitives.ReadInt64LittleEndian(record) != frames
                        || frames != (inspect ? 1 : item.Frames.Length)
                        || BinaryPrimitives.ReadInt64LittleEndian(record.AsSpan(8)) is < 1 or > 64
                        || BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(16)) != (inspect ? 0u : 1u)
                        || BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(20)) != 0)
                        throw new ProbeFailure("probe-discovery-completion-invalid");
                    if (await session.Output.ReadAsync(scratch.AsMemory(0, 1), token).ConfigureAwait(false) != 0
                        || await session.Metadata.ReadAsync(scratch.AsMemory(0, 1), token).ConfigureAwait(false) != 0)
                        throw new ProbeFailure("probe-discovery-trailing-pipe-bytes");
                    journal.Write(name, "discovery-complete-eof", new { frames, epochs });
                    return;
                default: throw new ProbeFailure("probe-discovery-record-type");
            }
        }
    }

    private static async Task DrainDiagnosticsAsync(Stream input, EvidenceJournal journal,
        string name, CancellationToken token)
    {
        byte[] bytes = new byte[8192]; byte[] retained = new byte[8192]; int retainedCount = 0; long total = 0; int count;
        while ((count = await input.ReadAsync(bytes, token).ConfigureAwait(false)) != 0)
        {
            int take = Math.Min(count, retained.Length - retainedCount);
            bytes.AsSpan(0, take).CopyTo(retained.AsSpan(retainedCount)); retainedCount += take;
            total += count;
            if (total > 1024 * 1024) throw new ProbeFailure("probe-discovery-diagnostics-capacity");
        }
        journal.Write(name, "discovery-stderr-eof", new { bytes = total, boundedPrivateBase64 = Convert.ToBase64String(retained, 0, retainedCount) });
    }

    public void Dispose() => _hostMapReservation.Dispose();
}
