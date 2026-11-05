using MakoKaraGenerator.Core.Media;
using MakoKaraGenerator.Media;

namespace MakoKaraGenerator.Backend.Hosting;

internal sealed partial class BackendMediaFeatureArtifactStore
{
    /// <summary>C holds AcquireProducer from before handing scratch to B until decoder, builder and write source drain.</summary>
    internal sealed partial class Reservation : IMediaTrackScratchReservation
    {
        private readonly BackendMediaFeatureArtifactStore _owner;
        private readonly object _lifetime = new();
        private readonly Dictionary<MediaTrackSection, BackendMediaFeatureBoundedStream> _sections = [];
        private readonly long _intervalLimit, _gridLimit, _prefixLimit, _candidateLimit;
        private readonly long[] _growth = new long[4];
        private BackendMediaFeatureBoundedStream? _candidate;
        private int _operations, _producers;
        private bool _producerAcquired, _closing;
        private State _state;
        private TaskCompletionSource? _drained, _disposed;
        private Exception? _primary;

        internal Reservation(BackendMediaFeatureArtifactStore owner, string client, string job, string artifact,
            MediaFeatureFileSource source, long maximumFrames, long bytes, string stage)
        {
            _owner = owner; OwnerClientId = client; JobId = job; ArtifactId = artifact; Source = source;
            MaximumFrames = maximumFrames; ReservedByteBudget = bytes; Stage = stage;
            _intervalLimit = checked(maximumFrames * MediaTrackLayoutV1.IntervalBytes);
            _gridLimit = checked(maximumFrames * MediaTrackLayoutV1.FrameGridBytes);
            _prefixLimit = checked(MediaTrackLayoutV1.GetPrefixCount(source.Kind, maximumFrames) * MediaTrackLayoutV1.PrefixEntryBytes);
            _candidateLimit = MediaTrackLayoutV1.GetArtifactBytes(source.Kind, maximumFrames);
        }

        internal string OwnerClientId { get; }
        internal string JobId { get; }
        internal string ArtifactId { get; }
        internal string Stage { get; }
        internal MediaFeatureFileSource Source { get; }
        internal long MaximumFrames { get; }
        public long ReservedByteBudget { get; }

        /// <summary>Single producer ownership scope; release only after every B callback, copy and disposal has completed.</summary>
        internal IDisposable AcquireProducer()
        {
            lock (_lifetime)
            {
                Require(!_closing && !_producerAcquired && _mode == WriteMode.None && _state == State.Building, "Media producer scope is closed or already acquired.");
                _mode = WriteMode.InProcess; _producerAcquired = true; _producers++; _operations++; return new Operation(this, producer: true);
            }
        }

        public ValueTask<Stream> OpenSectionAsync(MediaTrackSection section, long maximumBytes, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_lifetime)
            {
                Require(_mode == WriteMode.InProcess && _state == State.Building && _producers == 1 && !_sections.ContainsKey(section),
                    "Scratch section requires the live producer and may open only once.");
                (string name, long limit, int slot) = section switch
                {
                    MediaTrackSection.Intervals => ("intervals.tmp", _intervalLimit, 0),
                    MediaTrackSection.Grids => ("grids.tmp", _gridLimit, 1),
                    MediaTrackSection.Prefixes => ("prefixes.tmp", _prefixLimit, 2),
                    _ => throw Invalid("Scratch section kind is unknown."),
                };
                Require(maximumBytes >= 0 && maximumBytes <= limit, "Scratch section requested capacity exceeds its reservation.");
                var stream = OpenBounded(name, maximumBytes, slot);
                _sections.Add(section, stream); return ValueTask.FromResult<Stream>(stream);
            }
        }

        internal IDisposable BeginPrepare(BackendMediaFeatureArtifactStore owner)
        {
            lock (_lifetime)
            {
                Require(owner == _owner && !_closing && _mode == WriteMode.InProcess && _state == State.Building && _producers == 1 && _sections.Count == 3,
                    "Media reservation is not owned and ready for one preparation.");
                _state = State.Preparing; _operations++; return new Operation(this, producer: false);
            }
        }

        internal BackendMediaFeatureBoundedStream CreateCandidate()
        {
            lock (_lifetime)
            {
                Require(_mode == WriteMode.InProcess && _state == State.Preparing && _candidate is null, "Candidate can be created only once while preparing.");
                _candidate = OpenBounded("candidate.mft", _candidateLimit, 3); return _candidate;
            }
        }

        private BackendMediaFeatureBoundedStream OpenBounded(string name, long limit, int slot)
        {
            RejectLink(Stage);
            FileStream input = new(Path.Combine(Stage, name), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                1, FileOptions.Asynchronous | FileOptions.RandomAccess);
            return new BackendMediaFeatureBoundedStream(input, limit, length => ChargeGrowth(slot, length), _owner._hooks?.BeforeStreamWriteAsync);
        }

        private void ChargeGrowth(int slot, long length)
        {
            lock (_lifetime)
            {
                // Each stream holds its own I/O gate. The producer/prepare scope prevents concurrent cleanup.
                Require(_state is State.Building or State.Preparing, "Media storage growth is closed.");
                long next = Math.Max(_growth[slot], length);
                long total = checked(_growth.Sum() - _growth[slot] + next);
                if (total > ReservedByteBudget)
                    throw new MediaAbException(MediaAbFailure.CapacityExceeded, "Media files exceed the combined scratch and candidate reservation.");
                _growth[slot] = next; // Keep the admitted high-water mark after partial or failed writes.
            }
        }

        internal IDisposable BeginPublish(BackendMediaFeatureArtifactStore owner)
        {
            lock (_lifetime)
            {
                Require(owner == _owner && !_closing && _state == State.Prepared && _operations == 0 && _producers == 0 &&
                    (_mode == WriteMode.Worker ? _workerDrained :
                        _mode == WriteMode.InProcess && _sections.Values.All(stream => stream.Closed) && _candidate is { Closed: true }),
                    "Publishing requires all producer operations and section streams to drain.");
                _state = State.Publishing; _operations++; return new Operation(this, producer: false);
            }
        }

        internal void MarkPrepared() { lock (_lifetime) { Require(_state == State.Preparing, "Media preparation state is invalid."); _state = State.Prepared; } }
        internal void MarkCommitted() { lock (_lifetime) _state = State.Committed; }
        internal void MarkFailed() { lock (_lifetime) if (_state != State.Committed) _state = State.Failed; }
        internal void RecordFailure(Exception error) { lock (_lifetime) _primary ??= error; }

        private void ReleaseOperation(bool producer)
        {
            lock (_lifetime)
            {
                if (producer) _producers--;
                _operations--; if (_operations == 0) _drained?.TrySetResult();
            }
        }

        public async ValueTask DisposeAsync()
        {
            Task wait; TaskCompletionSource complete; bool ownsDisposal;
            lock (_lifetime)
            {
                ownsDisposal = _disposed is null;
                _disposed ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
                complete = _disposed;
                if (!ownsDisposal) wait = complete.Task;
                else
                {
                    _closing = true;
                    if (_operations == 0) wait = Task.CompletedTask;
                    else { _drained ??= new(TaskCreationOptions.RunContinuationsAsynchronously); wait = _drained.Task; }
                }
            }
            if (!ownsDisposal) { await wait.ConfigureAwait(false); return; }
            try
            {
                await wait.ConfigureAwait(false);
                Exception? cleanupFailure = null;
                BackendMediaFeatureBoundedStream[] streams;
                lock (_lifetime) streams = _candidate is null ? [.. _sections.Values] : [.. _sections.Values, _candidate];
                foreach (BackendMediaFeatureBoundedStream stream in streams)
                {
                    try { await stream.DisposeAsync().ConfigureAwait(false); }
                    catch (Exception failure) { cleanupFailure ??= failure; }
                }
                bool committed; lock (_lifetime) committed = _state == State.Committed;
                if (!committed)
                {
                    try { _owner.ReleaseReservation(this); }
                    catch (Exception failure) { cleanupFailure ??= failure; }
                }
                lock (_lifetime) { _sections.Clear(); _candidate = null; _state = State.Disposed; }
                if (cleanupFailure is not null)
                {
                    if (_primary is not null) _primary.Data["MediaReservationCleanupFailure"] = cleanupFailure;
                    else System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
                }
                complete.TrySetResult();
            }
            catch (Exception error) { complete.TrySetException(error); throw; }
        }

        private sealed class Operation(Reservation owner, bool producer) : IDisposable
        {
            private Reservation? _owner = owner;
            public void Dispose() => Interlocked.Exchange(ref _owner, null)?.ReleaseOperation(producer);
        }
        private enum State { Building, Preparing, Prepared, Publishing, Committed, Failed, Disposed }
    }

    internal sealed class Prepared : IAsyncDisposable
    {
        internal Prepared(BackendMediaFeatureArtifactStore owner, Reservation reservation, BackendMediaFeatureArtifactReference reference)
        { Owner = owner; Reservation = reservation; Reference = reference; }
        internal BackendMediaFeatureArtifactStore Owner { get; }
        internal Reservation Reservation { get; }
        internal BackendMediaFeatureArtifactReference Reference { get; }
        internal bool Complete { get; set; }
        public ValueTask DisposeAsync() => Reservation.DisposeAsync();
    }

    internal sealed class ReadLease : IAsyncDisposable
    {
        private readonly BackendMediaFeatureArtifactStore _owner;
        private readonly Entry _entry;
        private readonly SemaphoreSlim _dispose = new(1, 1);
        private bool _disposed;
        internal ReadLease(BackendMediaFeatureArtifactStore owner, Entry entry, BackendMediaFeatureArtifactReference reference,
            IMediaFeatureTrackReader reader) { _owner = owner; _entry = entry; Reference = reference; Reader = reader; }
        internal BackendMediaFeatureArtifactReference Reference { get; }
        internal IMediaFeatureTrackReader Reader { get; }
        public async ValueTask DisposeAsync()
        {
            await _dispose.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_disposed) return; _disposed = true; Exception? primary = null;
                try { await Reader.DisposeAsync().ConfigureAwait(false); }
                catch (Exception failure) { primary = failure; throw; }
                finally
                {
                    try { _owner.ReleaseReader(Reference.ArtifactId, _entry); }
                    catch (Exception cleanup) when (primary is not null) { primary.Data["MediaLeaseCleanupFailure"] = cleanup; }
                }
            }
            finally { _dispose.Release(); }
        }
    }
}
