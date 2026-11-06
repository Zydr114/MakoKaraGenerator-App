using MakoKaraGenerator.Core.Media;
using MakoKaraGenerator.Media;

namespace MakoKaraGenerator.Backend.Hosting;

/// <summary>A C-authorized reference; discovering matching content never creates this authority.</summary>
internal sealed record BackendMediaFeatureArtifactReference(string ArtifactId, string JobId, string OwnerClientId,
    MediaFeatureFileSource Source, MediaArtifactIdentity Identity);

/// <summary>Finite streaming MFT1 storage. The existing Job ledger remains the only task and recovery authority.</summary>
internal sealed partial class BackendMediaFeatureArtifactStore
{
    internal const long MaximumQuotaBytes = 2L * 1024 * 1024 * 1024;
    internal const int MaximumEntries = 1024;
    private readonly object _gate = new();
    private readonly string _root, _staging;
    private readonly long _quota;
    private readonly MediaAbValueValidator _values = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Reservation> _reservations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _orphans = new(StringComparer.Ordinal);
    private readonly BackendMediaFeatureStoreHooks? _hooks;
    private long _committedBytes, _reservedBytes, _orphanBytes;

    internal BackendMediaFeatureArtifactStore(string stateRoot, long quotaBytes = MaximumQuotaBytes,
        BackendMediaFeatureStoreHooks? hooks = null)
    {
        if (quotaBytes <= 0 || quotaBytes > MaximumQuotaBytes) throw new ArgumentOutOfRangeException(nameof(quotaBytes));
        ArgumentException.ThrowIfNullOrWhiteSpace(stateRoot);
        _quota = quotaBytes; _hooks = hooks;
        string state = Path.GetFullPath(stateRoot);
        EnsureDirectoryChain(state); _root = Path.Combine(state, "media-feature-artifacts");
        _staging = Path.Combine(_root, ".staging");
        EnsureDirectoryChain(_staging);
        ScanExisting();
    }

    internal long UsedBytes { get { lock (_gate) return checked(_committedBytes + _reservedBytes + _orphanBytes); } }

    internal Reservation? TryReserve(string ownerClientId, string jobId, MediaFeatureFileSource source, long maximumFrameCount)
    {
        ValidateOwner(ownerClientId); string guid = JobGuid(jobId); _values.ValidateSource(source);
        long bytes = MediaTrackLayoutV1.GetScratchAndCandidateBytes(source.Kind, maximumFrameCount);
        string artifact = "media-" + guid;
        lock (_gate)
        {
            if (_entries.ContainsKey(artifact) || _reservations.ContainsKey(artifact) || _orphans.ContainsKey(Path.Combine(_staging, guid)))
                throw Invalid("Media job already owns storage.");
            if (_entries.Count + _reservations.Count + _orphans.Count >= MaximumEntries || UsedBytes > _quota - bytes) return null;
            string stage = Path.Combine(_staging, guid);
            Require(!Directory.Exists(stage) && !File.Exists(stage), "Media staging identity already exists.");
            _reservedBytes = checked(_reservedBytes + bytes);
            var reservation = new Reservation(this, ownerClientId, jobId, artifact, source, maximumFrameCount, bytes, stage);
            try
            {
                Directory.CreateDirectory(stage); RejectLink(stage);
                _reservations.Add(artifact, reservation); return reservation;
            }
            catch (Exception primary)
            {
                ReleaseFailedStage(stage, bytes, primary); throw;
            }
        }
    }

    internal async ValueTask<Prepared> PrepareAsync(Reservation reservation, IMediaFeatureTrackWriteSource writeSource,
        IMediaFeatureTrackCodec codec, IMediaWorkingSetBudget writerBudget, IMediaWorkingSetBudget readerBudget,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(writeSource); ArgumentNullException.ThrowIfNull(codec);
        using IDisposable operation = reservation.BeginPrepare(this);
        Exception? primary = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Require(writeSource.Source == reservation.Source && writeSource.Presentation.FrameCount <= reservation.MaximumFrames,
                "Write source differs from the reserved source or frame capacity.");
            string candidate = Path.Combine(reservation.Stage, "candidate.mft");
            MediaArtifactIdentity identity;
            BackendMediaFeatureBoundedStream output = reservation.CreateCandidate(); Exception? writeFailure = null;
            try
            {
                identity = await codec.WriteCanonicalAsync(writeSource, output, writerBudget, cancellationToken).ConfigureAwait(false);
                _values.ValidateArtifactIdentity(reservation.Source, identity);
                Require(identity.FrameCount <= reservation.MaximumFrames && output.Length == identity.ByteLength,
                    "Candidate identity exceeds its reserved capacity.");
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                _hooks?.BeforeCandidateFlush?.Invoke();
                cancellationToken.ThrowIfCancellationRequested();
                output.FlushToDisk(); // Flush first, close writer, then validate only through a read-only handle.
            }
            catch (Exception failure) { writeFailure = failure; throw; }
            finally
            {
                try { await output.DisposeAsync().ConfigureAwait(false); }
                catch (Exception cleanup) when (writeFailure is not null) { writeFailure.Data["MediaWriterCleanupFailure"] = cleanup; }
            }
            _hooks?.BeforeCandidateValidation?.Invoke();
            await ValidateFileAsync(candidate, reservation.Source, identity, codec, readerBudget, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var reference = new BackendMediaFeatureArtifactReference(reservation.ArtifactId, reservation.JobId,
                reservation.OwnerClientId, reservation.Source, identity);
            reservation.MarkPrepared();
            return new Prepared(this, reservation, reference);
        }
        catch (Exception failure) { primary = failure; reservation.MarkFailed(); throw; }
        finally
        {
            // The operation scope drains here. The C owner then disposes B and its producer scope before cleanup.
            if (primary is not null) reservation.RecordFailure(primary);
        }
    }

    /// <summary>Caller already holds its CAS and Job commit gate. This phase is intentionally not cancellable.</summary>
    internal BackendMediaFeatureArtifactReference Publish(Prepared prepared)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        Reservation reservation = prepared.Reservation;
        lock (_gate)
        {
            Require(prepared.Owner == this && !prepared.Complete && _reservations.GetValueOrDefault(reservation.ArtifactId) == reservation,
                "Prepared media candidate has the wrong owner or was consumed.");
            using IDisposable publish = reservation.BeginPublish(this);
            try
            {
                ValidateReference(prepared.Reference);
                string[] before = ValidateStage(reservation.Stage, requireCandidate: true);
                foreach (string path in before)
                    if (!path.EndsWith("candidate.mft", StringComparison.Ordinal)) DeleteFile(path);
                string candidate = Path.Combine(reservation.Stage, "candidate.mft");
                Require(MeasureFile(candidate) == prepared.Reference.Identity.ByteLength, "Prepared candidate length changed.");
                File.Move(candidate, Path.Combine(reservation.Stage, "track.mft"));
                string target = BundlePath(prepared.Reference.ArtifactId);
                var entry = new Entry(target, prepared.Reference.Identity.ByteLength) { Reference = prepared.Reference, Verified = true };
                _entries.EnsureCapacity(checked(_entries.Count + 1));
                _hooks?.BeforePublish?.Invoke();
                Directory.Move(reservation.Stage, target);
                _entries.Add(prepared.Reference.ArtifactId, entry);
                _committedBytes = checked(_committedBytes + entry.Bytes);
                _reservedBytes -= reservation.ReservedByteBudget;
                _reservations.Remove(reservation.ArtifactId);
                reservation.MarkCommitted(); prepared.Complete = true;
                return prepared.Reference;
            }
            catch (Exception primary) { reservation.RecordFailure(primary); reservation.MarkFailed(); throw; }
        }
    }

    internal async ValueTask<ReadLease> AcquireReadAsync(string ownerClientId, BackendMediaFeatureArtifactReference reference,
        IMediaFeatureTrackCodec codec, IMediaWorkingSetBudget readerBudget, CancellationToken cancellationToken)
    {
        ValidateOwner(ownerClientId); ValidateReference(reference); ArgumentNullException.ThrowIfNull(codec);
        Entry entry;
        lock (_gate)
        {
            if (ownerClientId != reference.OwnerClientId || !_entries.TryGetValue(reference.ArtifactId, out entry!) ||
                !entry.Verified || entry.Expired || entry.Reference != reference)
                throw Invalid("Media reference is not authorized or retained.");
            entry.Readers = checked(entry.Readers + 1);
        }
        try
        {
            IMediaFeatureTrackReader reader = await OpenReaderAsync(entry.Path, reference, codec, readerBudget, cancellationToken).ConfigureAwait(false);
            return new ReadLease(this, entry, reference, reader);
        }
        catch (Exception primary)
        {
            try { ReleaseReader(reference.ArtifactId, entry); } catch (Exception cleanup) { primary.Data["MediaReadCleanupFailure"] = cleanup; }
            throw;
        }
    }

    /// <summary>Only C may supply this exact reference from a succeeded entry in its existing authoritative ledger.</summary>
    internal async ValueTask<bool> RecoverAsync(BackendMediaFeatureArtifactReference registered, IMediaFeatureTrackCodec codec,
        IMediaWorkingSetBudget readerBudget, CancellationToken cancellationToken)
    {
        ValidateReference(registered); ArgumentNullException.ThrowIfNull(codec); Entry entry;
        lock (_gate)
        {
            if (!_entries.TryGetValue(registered.ArtifactId, out entry!) || entry.Expired || entry.Missing) return false;
            Require(entry.Reference is null || entry.Reference == registered, "Recovery cannot replace an already bound artifact identity.");
            entry.Reference ??= registered; // Claim exact source, owner and identity before releasing the gate.
            entry.Readers = checked(entry.Readers + 1);
        }
        Exception? failure = null;
        try
        {
            await using (IMediaFeatureTrackReader reader = await OpenReaderAsync(entry.Path, registered, codec, readerBudget, cancellationToken).ConfigureAwait(false)) { }
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                Require(entry.Reference == registered, "Recovered artifact identity changed.");
                if (entry.Expired) return false;
                entry.Verified = true;
            }
            return true;
        }
        catch (Exception error) { failure = error; throw; }
        finally
        {
            try { ReleaseReader(registered.ArtifactId, entry); }
            catch (Exception cleanup) when (failure is not null) { failure.Data["MediaRecoveryCleanupFailure"] = cleanup; }
        }
    }

    /// <summary>The private read-only track file of a published, still registered reference.</summary>
    internal string ArtifactFilePath(BackendMediaFeatureArtifactReference reference)
    {
        ValidateReference(reference);
        lock (_gate)
        {
            Require(_entries.TryGetValue(reference.ArtifactId, out Entry? entry) && entry.Verified && !entry.Expired
                && entry.Reference == reference, "Media reference is not authorized or retained.");
            return Path.Combine(entry!.Path, "track.mft");
        }
    }

    internal void Prune(IEnumerable<BackendMediaFeatureArtifactReference> retained)
    {
        ArgumentNullException.ThrowIfNull(retained);
        var keep = new Dictionary<string, BackendMediaFeatureArtifactReference>(StringComparer.Ordinal);
        foreach (BackendMediaFeatureArtifactReference reference in retained)
        {
            ValidateReference(reference);
            if (keep.TryGetValue(reference.ArtifactId, out BackendMediaFeatureArtifactReference? prior))
                Require(prior == reference, "Retained media references disagree about the same identity.");
            else { Require(keep.Count < MaximumEntries, "Retained media reference set exceeds its fixed bound."); keep.Add(reference.ArtifactId, reference); }
        }
        lock (_gate)
        {
            // Validate the complete snapshot before expiring or deleting any entry.
            foreach ((string id, BackendMediaFeatureArtifactReference reference) in keep)
                if (_entries.TryGetValue(id, out Entry? entry))
                    Require(entry.Reference is null || entry.Reference == reference, "Retention cannot replace a registered media identity.");
            foreach ((string id, Entry entry) in _entries.ToArray())
            {
                entry.Expired = entry.Missing || !keep.ContainsKey(id);
                if (entry.Expired && entry.Readers == 0) TryDeleteEntry(id, entry);
            }
            foreach ((string stage, long bytes) in _orphans.ToArray())
            {
                try { DeleteStage(stage); _orphans.Remove(stage); _orphanBytes -= bytes; }
                catch (Exception error) when (IsFileFailure(error)) { /* Keep the full residual charge until deletion succeeds. */ }
            }
        }
    }

    private void ReleaseReader(string id, Entry entry)
    {
        lock (_gate)
        {
            Require(_entries.GetValueOrDefault(id) == entry && entry.Readers > 0, "Media lease pin does not match its registered entry.");
            entry.Readers--;
            if (entry.Expired && entry.Readers == 0) TryDeleteEntry(id, entry);
        }
    }

    private void TryDeleteEntry(string id, Entry entry)
    {
        try
        {
            DeleteBundleDirectory(entry.Path); _entries.Remove(id); _committedBytes -= entry.Bytes;
        }
        catch (Exception error) when (IsFileFailure(error)) { /* Expired bytes remain charged for a later prune. */ }
    }

    private static async ValueTask<IMediaFeatureTrackReader> OpenReaderAsync(string directory, BackendMediaFeatureArtifactReference reference,
        IMediaFeatureTrackCodec codec, IMediaWorkingSetBudget budget, CancellationToken token)
    {
        ValidateBundle(directory);
        return await OpenValidatedFileAsync(Path.Combine(directory, "track.mft"), reference.Source, reference.Identity, codec, budget, token).ConfigureAwait(false);
    }

    private static async ValueTask ValidateFileAsync(string path, MediaFeatureFileSource source, MediaArtifactIdentity identity,
        IMediaFeatureTrackCodec codec, IMediaWorkingSetBudget budget, CancellationToken token)
    {
        await using IMediaFeatureTrackReader reader = await OpenValidatedFileAsync(path, source, identity, codec, budget, token).ConfigureAwait(false);
    }

    private static async ValueTask<IMediaFeatureTrackReader> OpenValidatedFileAsync(string path, MediaFeatureFileSource source,
        MediaArtifactIdentity identity, IMediaFeatureTrackCodec codec, IMediaWorkingSetBudget budget, CancellationToken token)
    {
        Require(MeasureFile(path) == identity.ByteLength, "Media file length differs from the registered identity.");
        FileStream input = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.Asynchronous | FileOptions.RandomAccess);
        try { return await codec.OpenValidatedAsync(input, source, identity, budget, token).ConfigureAwait(false); }
        catch (Exception primary)
        {
            try { await input.DisposeAsync().ConfigureAwait(false); } catch (Exception cleanup) { primary.Data["MediaInputCleanupFailure"] = cleanup; }
            throw;
        }
    }

    private void ValidateReference(BackendMediaFeatureArtifactReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference); ValidateOwner(reference.OwnerClientId);
        Require(reference.ArtifactId == "media-" + JobGuid(reference.JobId), "Media artifact and Job identities disagree.");
        _values.ValidateArtifactIdentity(reference.Source, reference.Identity); // Includes current Core profile strings.
    }

    private static void ValidateOwner(string owner) => Require(!string.IsNullOrWhiteSpace(owner) && owner.Length <= 256 && !owner.Any(char.IsControl), "Media owner identity is invalid.");
    private static string JobGuid(string job)
    {
        Require(job is { Length: 36 } && job.StartsWith("job-", StringComparison.Ordinal) && IsGuid(job[4..]), "Media Job identity is invalid.");
        return job[4..];
    }
    private static bool IsGuid(string value) => value.Length == 32 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private string BundlePath(string artifact)
    {
        Require(artifact is { Length: 38 } && artifact.StartsWith("media-", StringComparison.Ordinal) && IsGuid(artifact[6..]), "Media artifact identity is invalid.");
        return Path.Combine(_root, artifact);
    }
    private static MediaAbException Invalid(string detail) => new(MediaAbFailure.ArtifactInvalid, detail);
    private static void Require(bool condition, string detail) { if (!condition) throw Invalid(detail); }
    private static bool IsFileFailure(Exception error) => error is IOException or UnauthorizedAccessException;
    internal sealed class Entry(string path, long bytes)
    {
        internal string Path { get; } = path;
        internal long Bytes { get; } = bytes;
        internal BackendMediaFeatureArtifactReference? Reference;
        internal int Readers;
        internal bool Verified, Expired, Missing;
    }
}

/// <summary>Deterministic file-boundary failure hooks for focused tests, never HTTP-controlled.</summary>
internal sealed class BackendMediaFeatureStoreHooks
{
    internal Action? BeforeCandidateFlush { get; init; }
    internal Action? BeforeCandidateValidation { get; init; }
    internal Action? BeforePublish { get; init; }
    internal Action<string>? BeforeDeleteFile { get; init; }
    internal Func<CancellationToken, ValueTask>? BeforeStreamWriteAsync { get; init; }
}
