using MakoKaraGenerator.Core.Media;
using MakoKaraGenerator.Media;

namespace MakoKaraGenerator.Backend.Hosting;

internal sealed partial class BackendMediaFeatureArtifactStore
{
    /// <summary>Imports only after the trusted executor has attested actual process, writer and pump drain.</summary>
    /// <remarks>No Worker payload, flag, terminal message or candidate metadata can attest this lifetime boundary.</remarks>
    internal async ValueTask<Prepared> PrepareWorkerCandidateAsync(Reservation reservation, WorkerWriteScope drainedScope,
        MediaArtifactIdentity identity, IMediaFeatureTrackCodec codec, IMediaWorkingSetBudget readerBudget,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reservation); ArgumentNullException.ThrowIfNull(drainedScope);
        ArgumentNullException.ThrowIfNull(codec);
        using IDisposable operation = reservation.BeginWorkerPrepare(this, drainedScope);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            _values.ValidateArtifactIdentity(reservation.Source, identity);
            Require(identity.FrameCount <= reservation.MaximumFrames, "Worker candidate exceeds its admitted frame count.");
            _hooks?.BeforeCandidateValidation?.Invoke();
            ValidateWorkerStage(drainedScope.Grant, identity);
            // The only opening here is FileAccess.Read / FileShare.Read, through the original full codec gate.
            await ValidateFileAsync(Path.Combine(reservation.Stage, "candidate.mft"), reservation.Source, identity,
                codec, readerBudget, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var reference = new BackendMediaFeatureArtifactReference(reservation.ArtifactId, reservation.JobId,
                reservation.OwnerClientId, reservation.Source, identity);
            reservation.MarkPrepared();
            return new Prepared(this, reservation, reference);
        }
        catch (Exception primary)
        {
            reservation.RecordFailure(primary); reservation.MarkFailed(); throw;
        }
    }

    private static void ValidateWorkerStage(WorkerStageGrant grant, MediaArtifactIdentity identity)
    {
        string[] files = BoundedChildren(grant.StageDirectory, 4);
        Require(files.Length == 4, "Worker stage requires all three scratch sections and exactly one candidate.");
        long bytes = 0;
        foreach (string path in files)
        {
            (long ceiling, long exact) = Path.GetFileName(path) switch
            {
                "intervals.tmp" => (grant.IntervalMaximumBytes, checked(identity.FrameCount * MediaTrackLayoutV1.IntervalBytes)),
                "grids.tmp" => (grant.GridMaximumBytes, checked(identity.FrameCount * MediaTrackLayoutV1.FrameGridBytes)),
                "prefixes.tmp" => (grant.PrefixMaximumBytes, checked(MediaTrackLayoutV1.GetPrefixCount(grant.Source.Kind, identity.FrameCount) * MediaTrackLayoutV1.PrefixEntryBytes)),
                "candidate.mft" => (grant.CandidateMaximumBytes, identity.ByteLength),
                _ => throw Invalid("Worker stage contains an extra or renamed file."),
            };
            long length = MeasureFile(path); // Rejects links, reparse points, directories and global file overgrowth.
            Require(length <= ceiling && length == exact, "Worker stage file differs from its exact identity or grant ceiling.");
            bytes = checked(bytes + length);
        }
        Require(bytes <= grant.ReservedByteBudget, "Worker stage exceeds its combined reservation.");
    }

    internal sealed partial class Reservation
    {
        private WriteMode _mode;
        private WorkerWriteScope? _workerScope;
        private bool _workerDrained;

        /// <summary>Claims the existing reservation once, mutually exclusively with the in-process producer.</summary>
        internal WorkerWriteScope BeginWorkerWrite()
        {
            lock (_lifetime)
            {
                Require(!_closing && _mode == WriteMode.None && !_producerAcquired && _state == State.Building,
                    "Media reservation already has a producer or is closing.");
                var grant = new WorkerStageGrant(this);
                var scope = new WorkerWriteScope(this, grant);
                _mode = WriteMode.Worker; _producerAcquired = true; _workerScope = scope;
                _producers++; _operations++;
                return scope;
            }
        }

        internal IDisposable BeginWorkerPrepare(BackendMediaFeatureArtifactStore owner, WorkerWriteScope scope)
        {
            lock (_lifetime)
            {
                Require(owner == _owner && !_closing && _mode == WriteMode.Worker && ReferenceEquals(_workerScope, scope) &&
                    _workerDrained && _state == State.Building && _producers == 0 && _operations == 0,
                    "Worker import requires this reservation's once-drained executor scope.");
                _state = State.Preparing; _operations++;
                return new Operation(this, producer: false);
            }
        }

        internal void AttestExecutorDrained(WorkerWriteScope scope)
        {
            lock (_lifetime)
            {
                Require(_mode == WriteMode.Worker && ReferenceEquals(_workerScope, scope), "Worker drain has the wrong reservation owner.");
                if (_workerDrained) return;
                // Closing is allowed: DisposeAsync is waiting on this producer's real drain.
                scope.Grant.SealAfterExecutorDrain();
                _workerDrained = true;
                ReleaseOperation(producer: true);
            }
        }

        private enum WriteMode { None, InProcess, Worker }
    }

    /// <summary>Host-only producer hold; deliberately not IDisposable, so leaving a using block cannot fake process drain.</summary>
    internal sealed class WorkerWriteScope
    {
        private readonly Reservation _reservation;
        internal WorkerWriteScope(Reservation reservation, WorkerStageGrant grant) { _reservation = reservation; Grant = grant; }
        internal WorkerStageGrant Grant { get; }

        /// <summary>Trusted executor calls only after all of its actual processes, native callbacks, writers and pipes have drained.</summary>
        /// <remarks>This method has no wire representation and takes no external success/drained flag. On failure, first RecordFailure on the reservation.</remarks>
        internal void AttestExecutorDrained() => _reservation.AttestExecutorDrained(this);
    }

    /// <summary>Once-issued private file grant; never contains the store root or a publication path.</summary>
    /// <remarks>Its local writer is a bounded file adapter for this finite slice. Worker IPC must later map these exact ceilings,
    /// not serialize a Reservation, scope or drain attestation. This does not claim any Worker process integration.</remarks>
    internal sealed class WorkerStageGrant
    {
        private readonly object _writerGate = new();
        private readonly WorkerStageDrain _writerDrain = new();
        private bool _writerClaimed, _writerStarted, _sealed;

        internal WorkerStageGrant(Reservation reservation)
        {
            StageDirectory = reservation.Stage; JobId = reservation.JobId; ArtifactId = reservation.ArtifactId;
            Source = reservation.Source; MaximumFrames = reservation.MaximumFrames; ReservedByteBudget = reservation.ReservedByteBudget;
            IntervalMaximumBytes = checked(MaximumFrames * MediaTrackLayoutV1.IntervalBytes);
            GridMaximumBytes = checked(MaximumFrames * MediaTrackLayoutV1.FrameGridBytes);
            PrefixMaximumBytes = checked(MediaTrackLayoutV1.GetPrefixCount(Source.Kind, MaximumFrames) * MediaTrackLayoutV1.PrefixEntryBytes);
            CandidateMaximumBytes = MediaTrackLayoutV1.GetArtifactBytes(Source.Kind, MaximumFrames);
            GrantId = Guid.NewGuid().ToString("N");
            Require(checked(IntervalMaximumBytes + GridMaximumBytes + PrefixMaximumBytes + CandidateMaximumBytes) == ReservedByteBudget,
                "Worker file ceilings must exactly cover the existing reservation.");
        }
        internal string GrantId { get; }
        internal string StageDirectory { get; }
        internal string JobId { get; }
        internal string ArtifactId { get; }
        internal MediaFeatureFileSource Source { get; }
        internal long MaximumFrames { get; }
        internal long IntervalMaximumBytes { get; }
        internal long GridMaximumBytes { get; }
        internal long PrefixMaximumBytes { get; }
        internal long CandidateMaximumBytes { get; }
        internal long ReservedByteBudget { get; }

        /// <summary>One local bounded writer view; it creates no quota reservation and never owns Host publication.</summary>
        internal WorkerStageWriter OpenLocalWriter(BackendMediaFeatureStoreHooks? hooks = null)
        {
            lock (_writerGate)
            {
                Require(!_sealed && !_writerClaimed, "Worker grant writer was consumed or sealed.");
                _writerClaimed = true;
                // Ownership of the writer transfers to the executor. Retain only its drain signal.
                var writer = new WorkerStageWriter(this, hooks, _writerDrain);
                _writerStarted = true;
                return writer;
            }
        }

        internal void SealAfterExecutorDrain()
        {
            lock (_writerGate)
            {
                Require(!_writerStarted || _writerDrain.Complete, "Local worker files still have live operations or writers.");
                _sealed = true;
            }
        }
    }

    /// <summary>A non-owning drain signal shared with the executor-owned writer.</summary>
    internal sealed class WorkerStageDrain
    {
        private bool _complete;
        internal bool Complete => Volatile.Read(ref _complete);
        internal void MarkComplete() => Volatile.Write(ref _complete, true);
    }

    /// <summary>A process-local, independently bounded four-file view over an already fully charged Host grant.</summary>
    internal sealed class WorkerStageWriter : IMediaTrackScratchReservation
    {
        private readonly WorkerStageGrant _grant;
        private readonly BackendMediaFeatureStoreHooks? _hooks;
        private readonly object _gate = new();
        private readonly SemaphoreSlim _dispose = new(1, 1);
        private readonly Dictionary<int, BackendMediaFeatureBoundedStream> _streams = [];
        private readonly long[] _growth = new long[4];
        private readonly WorkerStageDrain _drain;
        private bool _closing;

        internal WorkerStageWriter(WorkerStageGrant grant, BackendMediaFeatureStoreHooks? hooks, WorkerStageDrain drain)
        {
            _grant = grant; _hooks = hooks; _drain = drain;
            Require(BoundedChildren(grant.StageDirectory, 1).Length == 0, "A new Worker grant must start with an empty private stage.");
        }
        public long ReservedByteBudget => _grant.ReservedByteBudget;
        internal bool Drained => _drain.Complete;
        internal long AdmittedHighWaterBytes { get { lock (_gate) return _growth.Sum(); } }

        public ValueTask<Stream> OpenSectionAsync(MediaTrackSection section, long maximumBytes, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            (string name, long ceiling, int slot) = section switch
            {
                MediaTrackSection.Intervals => ("intervals.tmp", _grant.IntervalMaximumBytes, 0),
                MediaTrackSection.Grids => ("grids.tmp", _grant.GridMaximumBytes, 1),
                MediaTrackSection.Prefixes => ("prefixes.tmp", _grant.PrefixMaximumBytes, 2),
                _ => throw Invalid("Worker scratch section is unknown."),
            };
            Require(maximumBytes >= 0 && maximumBytes <= ceiling, "Worker section requested a ceiling beyond its grant.");
            return ValueTask.FromResult<Stream>(Open(name, maximumBytes, slot));
        }

        internal BackendMediaFeatureBoundedStream CreateCandidate()
        {
            lock (_gate)
            {
                Require(_streams.ContainsKey(0) && _streams.ContainsKey(1) && _streams.ContainsKey(2),
                    "Worker candidate requires all three existing scratch sections.");
                return Open("candidate.mft", _grant.CandidateMaximumBytes, 3);
            }
        }

        private BackendMediaFeatureBoundedStream Open(string name, long ceiling, int slot)
        {
            lock (_gate)
            {
                Require(!_closing && !_streams.ContainsKey(slot), "Worker file is closed or was already opened.");
                RejectLink(_grant.StageDirectory);
                var file = new FileStream(Path.Combine(_grant.StageDirectory, name), FileMode.CreateNew, FileAccess.ReadWrite,
                    FileShare.None, 1, FileOptions.Asynchronous | FileOptions.RandomAccess);
                var stream = new BackendMediaFeatureBoundedStream(file, ceiling, length => ChargeGrowth(slot, length), _hooks?.BeforeStreamWriteAsync);
                _streams.Add(slot, stream);
                return stream;
            }
        }

        private void ChargeGrowth(int slot, long length)
        {
            lock (_gate)
            {
                // Closing waits each stream I/O gate; a write already admitted by that gate must be allowed to finish.
                long next = Math.Max(_growth[slot], length);
                long total = checked(_growth.Sum() - _growth[slot] + next);
                if (total > ReservedByteBudget)
                    throw new MediaAbException(MediaAbFailure.CapacityExceeded, "Worker files exceed the combined grant.");
                _growth[slot] = next; // A partial/failed write never erases its already admitted high-water mark.
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _dispose.WaitAsync().ConfigureAwait(false);
            try
            {
                if (Drained) return;
                BackendMediaFeatureBoundedStream[] streams;
                lock (_gate) { _closing = true; streams = [.. _streams.Values]; }
                List<Exception>? failures = null;
                foreach (BackendMediaFeatureBoundedStream stream in streams)
                {
                    try { await stream.DisposeAsync().ConfigureAwait(false); }
                    catch (Exception failure) { (failures ??= []).Add(failure); }
                }
                if (failures is not null) throw new AggregateException("Worker stage writer cleanup failed.", failures);
                _drain.MarkComplete();
            }
            finally { _dispose.Release(); }
        }
    }
}
