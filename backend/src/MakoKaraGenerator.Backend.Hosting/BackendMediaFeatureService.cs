using System.Security.Cryptography;
using System.Text;
using MakoKaraGenerator.Core.Media;
using MakoKaraGenerator.Media;
using MakoKaraGenerator.Processes.MediaEnvelope;
using MakoKaraGenerator.Rendering.Abstractions;

namespace MakoKaraGenerator.Backend.Hosting;

/// <summary>One fixed analysis observation for a background source; never mutates in place.</summary>
/// <param name="Source">The inspected accepted file source; null when the source could not be inspected.</param>
/// <param name="Request">The analysis attempt identity; a retried attempt after a failure gets a new identity.</param>
/// <param name="Artifact">The published track when available.</param>
/// <param name="ArtifactPath">The private read-only artifact file when available.</param>
/// <param name="Failure">The fixed failure when decoding or building failed after inspection.</param>
internal sealed record BackendMediaFeatureResolution(MediaFeatureFileSource? Source, MediaPreparationRequestId Request,
    BackendMediaFeatureArtifactReference? Artifact, string? ArtifactPath, MediaFailure? Failure);

/// <summary>
/// Host-local background media-feature analysis. Decoding runs only in the reviewed native helper inside its
/// systemd envelope; this service owns the private source copy, the single analysis slot and the artifact store.
/// </summary>
public sealed class BackendMediaFeatureService : IAsyncDisposable
{
    private const long NativeBytes = 128L * 1024 * 1024;
    private const string Owner = "host-media-features";
    private static readonly TimeSpan InspectionWait = TimeSpan.FromSeconds(45);
    // A failed attempt is retried lazily by the next caller once its delay elapsed; the last failure stays fixed for the run.
    internal static readonly TimeSpan[] RetryDelays = [TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(10)];
    private readonly string _tools, _sources;
    private readonly TimeProvider _time;
    private readonly BackendMediaFeatureArtifactStore _store;
    private readonly MediaFeatureTrackCodec _codec = new(new MediaMomentKernel(), new MediaAbValueValidator());
    private readonly EnvelopeQuarantineStore _quarantine = new();
    private readonly SemaphoreSlim _analysisSlot = new(1, 1);
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly List<Task> _jobs = [];
    private readonly CancellationTokenSource _stop = new();
    private Task<MediaPresentationDeployment>? _deployment;
    private long _generation;

    private BackendMediaFeatureService(string stateRoot, string tools, TimeProvider time)
    {
        _tools = tools; _time = time;
        _store = new BackendMediaFeatureArtifactStore(stateRoot);
        // No durable job ledger owns earlier artifacts; reclaim them instead of charging the quota.
        _store.Prune([]);
        _sources = Path.Combine(Path.GetFullPath(stateRoot), "media-feature-sources");
        if (Directory.Exists(_sources)) BackendPreparedProjectRuntimeInput.DeletePreparedDirectory(_sources);
        Directory.CreateDirectory(_sources);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(_sources, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    /// <summary>Creates the service only when the shipped native tools exist on a supported host.</summary>
    /// <param name="stateRoot">The private Server state root.</param>
    /// <param name="toolDirectory">The native tool directory; defaults to the application media-tools directory.</param>
    public static BackendMediaFeatureService? TryCreate(string stateRoot, string? toolDirectory = null)
    {
        string tools = toolDirectory ?? Path.Combine(AppContext.BaseDirectory, "media-tools");
        if (!MediaPresentationDeploymentDiscovery.IsInstalled(tools)) return null;
        return new BackendMediaFeatureService(stateRoot, tools, TimeProvider.System);
    }

    /// <summary>Test seam: an explicit tool directory, installed or not, and a controllable clock for retry delays.</summary>
    internal static BackendMediaFeatureService Create(string stateRoot, string tools, TimeProvider time) => new(stateRoot, tools, time);

    /// <summary>Monotonic counter advanced whenever any analysis reaches a new fixed state.</summary>
    public long Generation => Interlocked.Read(ref _generation);

    /// <summary>Raised after <see cref="Generation"/> advances.</summary>
    public event Action? Changed;

    /// <summary>
    /// Starts, joins or retries analysis of one authorized private file. A failed attempt is replaced by a new attempt
    /// on the first call after its retry delay; earlier callers keep the fixed failure they observed.
    /// </summary>
    /// <param name="trustedPath">A private, hash-verified local copy that remains valid until this call returns.</param>
    /// <param name="content">The exact verified content identity.</param>
    /// <param name="kind">Still or video.</param>
    /// <param name="completionWait">
    /// How long to wait for the final artifact or failure: <see cref="Timeout.InfiniteTimeSpan"/> for final consumers
    /// such as export, zero to wait for inspection only; after a bounded wait the caller still waits for inspection.
    /// </param>
    /// <param name="cancellationToken">Cancels only this caller's wait.</param>
    internal async Task<BackendMediaFeatureResolution> ResolveAsync(string trustedPath, MediaFileContent content,
        MediaSourceKind kind, TimeSpan completionWait, CancellationToken cancellationToken)
    {
        if (completionWait < TimeSpan.Zero && completionWait != Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(completionWait));
        string key = content.Sha256.LowerHex + ":" + content.ByteLength.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + kind;
        Entry entry; Entry? previous = null; bool created = false;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_stop.IsCancellationRequested, this);
            if (!_entries.TryGetValue(key, out entry!) || RetryDue(entry))
            {
                previous = entry;
                entry = new Entry(RequestId(key), previous is null ? 0 : previous.Attempt + 1);
                _entries[key] = entry; created = true;
            }
        }
        if (created)
        {
            // The copy must complete before this caller's private input can be released, so it is awaited here.
            string? copy = null;
            try { copy = await CopyAsync(trustedPath, content, cancellationToken).ConfigureAwait(false); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or MediaAbException or OperationCanceledException)
            {
                // This caller's copy failed, not the analysis: restore the earlier attempt so its retry budget is kept.
                lock (_gate)
                {
                    if (_entries.TryGetValue(key, out Entry? current) && ReferenceEquals(current, entry))
                    {
                        if (previous is null) _entries.Remove(key);
                        else _entries[key] = previous;
                    }
                }
                entry.Inspected.TrySetResult(null); entry.Completed.TrySetResult();
                throw;
            }
            Task job = Task.Run(() => RunAsync(entry, copy, content, kind), CancellationToken.None);
            lock (_gate) { _jobs.RemoveAll(task => task.IsCompleted); _jobs.Add(job); }
        }
        try
        {
            if (completionWait == Timeout.InfiniteTimeSpan) await entry.Completed.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            else
            {
                if (completionWait > TimeSpan.Zero)
                    try { await entry.Completed.Task.WaitAsync(completionWait, cancellationToken).ConfigureAwait(false); }
                    catch (TimeoutException) { }
                if (!entry.Completed.Task.IsCompleted)
                    await entry.Inspected.Task.WaitAsync(InspectionWait, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (TimeoutException) { }
        return entry.Snapshot(_store);
    }

    // Under _gate. Only a finished attempt without an artifact is retried, at most RetryDelays.Length times.
    private bool RetryDue(Entry entry) => entry.FailedAt is { } failed && entry.Attempt < RetryDelays.Length
        && _time.GetElapsedTime(failed) >= RetryDelays[entry.Attempt];

    private async Task RunAsync(Entry entry, string copy, MediaFileContent content, MediaSourceKind kind)
    {
        CancellationToken token = _stop.Token;
        try
        {
            await _analysisSlot.WaitAsync(token).ConfigureAwait(false);
            try
            {
                MediaPresentationDeployment deployment = await DeploymentAsync(token).ConfigureAwait(false);
                await using var file = new MediaEnvelopeFileLease(new OwnedCopyLease(copy, content));
                IEnvelopeBackend backend = OperatingSystem.IsWindows()
                    ? new WindowsJobEnvelopeBackend() : new LinuxSystemdEnvelopeBackend();
                var runner = new MediaEnvelopeRunner(backend, _quarantine, NativeBytes);
                var decoder = new MediaPresentationDecoder(deployment, runner);
                var decodeBudget = new MediaWorkingSetBudget(MediaAbCapacityV1.MaximumDecodeWorkingBytes);
                MediaFeatureFileSource? source = await InspectAsync(decoder, file, kind, decodeBudget, token).ConfigureAwait(false);
                entry.SetSource(source);
                Advance();
                if (source is null) return;
                try
                {
                    BackendMediaFeatureArtifactReference reference = await BuildAsync(decoder, file, source, decodeBudget, token).ConfigureAwait(false);
                    entry.SetArtifact(reference);
                }
                catch (Exception error) when (!token.IsCancellationRequested)
                {
                    entry.SetFailure(Failure(entry.Request, error));
                }
                Advance();
            }
            finally { _analysisSlot.Release(); }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            entry.SetSource(null);
            if (OperatingSystem.IsWindows() && error is MediaAbException { Failure: MediaAbFailure.CapacityExceeded })
                entry.SetFailure(Failure(entry.Request, error));
            Advance();
        }
        finally
        {
            if (!token.IsCancellationRequested) entry.FinishAttempt(_time.GetTimestamp());
            entry.Inspected.TrySetResult(null); entry.Completed.TrySetResult();
            try { BackendPreparedProjectRuntimeInput.DeletePreparedDirectory(Path.GetDirectoryName(copy)!); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private static async Task<MediaFeatureFileSource?> InspectAsync(MediaPresentationDecoder decoder, MediaEnvelopeFileLease file,
        MediaSourceKind kind, MediaWorkingSetBudget budget, CancellationToken token)
    {
        int[] streams = kind == MediaSourceKind.Still ? [0] : [0, 1, 2];
        foreach (int stream in streams)
        {
            foreach (TrustedMediaColorInterpretation? missing in ColorCandidates(kind))
            {
                token.ThrowIfCancellationRequested();
                try { return await decoder.InspectAsync(file, new(kind, stream, missing), budget, token).ConfigureAwait(false); }
                catch (MediaAbException error) when (error.Failure == MediaAbFailure.ColorUnsupported) { }
                catch (MediaAbException error) when (OperatingSystem.IsWindows() && error.Failure == MediaAbFailure.CapacityExceeded) { throw; }
                catch (MediaAbException) { break; }
            }
        }
        return null;
    }

    // Untagged sources need an explicit internal interpretation. Each candidate must still agree with every explicit tag.
    private static IEnumerable<TrustedMediaColorInterpretation?> ColorCandidates(MediaSourceKind kind)
    {
        yield return null;
        MediaDigest authority = Digest("mako.media.untagged-default-interpretation.v1");
        if (kind == MediaSourceKind.Still)
        {
            yield return new(MediaTransfer.Srgb, MediaMatrix.Rgb, MediaRange.Full, MediaAlpha.Opaque, MediaChromaLocation.None, authority);
            yield return new(MediaTransfer.Srgb, MediaMatrix.Rgb, MediaRange.Full, MediaAlpha.Straight, MediaChromaLocation.None, authority);
            yield return new(MediaTransfer.Srgb, MediaMatrix.Bt601Jpeg, MediaRange.Full, MediaAlpha.Opaque, MediaChromaLocation.Center, authority);
            yield return new(MediaTransfer.Srgb, MediaMatrix.Bt601Jpeg, MediaRange.Full, MediaAlpha.Opaque, MediaChromaLocation.None, authority);
            yield break;
        }
        yield return new(MediaTransfer.Bt709, MediaMatrix.Bt709, MediaRange.Limited, MediaAlpha.Opaque, MediaChromaLocation.Left, authority);
        yield return new(MediaTransfer.Bt709, MediaMatrix.Bt709, MediaRange.Limited, MediaAlpha.Opaque, MediaChromaLocation.Center, authority);
        yield return new(MediaTransfer.Bt709, MediaMatrix.Bt709, MediaRange.Limited, MediaAlpha.Opaque, MediaChromaLocation.None, authority);
        yield return new(MediaTransfer.Bt709, MediaMatrix.Bt709, MediaRange.Full, MediaAlpha.Opaque, MediaChromaLocation.Left, authority);
        yield return new(MediaTransfer.Srgb, MediaMatrix.Rgb, MediaRange.Full, MediaAlpha.Opaque, MediaChromaLocation.None, authority);
    }

    private async Task<BackendMediaFeatureArtifactReference> BuildAsync(MediaPresentationDecoder decoder, MediaEnvelopeFileLease file,
        MediaFeatureFileSource source, MediaWorkingSetBudget decodeBudget, CancellationToken token)
    {
        string job = "job-" + Guid.NewGuid().ToString("N");
        long frames = source.Kind == MediaSourceKind.Still ? 1 : MediaAbCapacityV1.MaximumFrames;
        BackendMediaFeatureArtifactStore.Reservation reservation = _store.TryReserve(Owner, job, source, frames)
            ?? throw new MediaAbException(MediaAbFailure.CapacityExceeded, "Media feature storage quota is exhausted.");
        try
        {
            BackendMediaFeatureArtifactStore.Prepared prepared;
            using (reservation.AcquireProducer())
            {
                await using IMediaFeatureTrackBuilder builder = _codec.CreateBuilder(source, reservation,
                    new MediaWorkingSetBudget(MediaAbCapacityV1.MaximumBuilderWorkingBytes));
                await using var lease = new MediaPresentationSourceLease(file, source);
                PresentationIndexSummary summary = await decoder.DecodeAsync(lease, builder, decodeBudget, token).ConfigureAwait(false);
                await using IMediaFeatureTrackWriteSource writeSource = await builder.CompleteAsync(summary, token).ConfigureAwait(false);
                prepared = await _store.PrepareAsync(reservation, writeSource, _codec,
                    new MediaWorkingSetBudget(MediaAbCapacityV1.MaximumBuilderWorkingBytes),
                    new MediaWorkingSetBudget(MediaAbCapacityV1.MaximumRuntimeCpuCacheBytes), token).ConfigureAwait(false);
            }
            return _store.Publish(prepared);
        }
        catch (Exception primary) { reservation.RecordFailure(primary); throw; }
        finally { await reservation.DisposeAsync().ConfigureAwait(false); }
    }

    private Task<MediaPresentationDeployment> DeploymentAsync(CancellationToken token)
    {
        lock (_gate)
        {
            if (_deployment is null || _deployment.IsFaulted || _deployment.IsCanceled)
                _deployment = MediaPresentationDeploymentDiscovery.DiscoverAsync(_tools, token);
            return _deployment;
        }
    }

    private async Task<string> CopyAsync(string trustedPath, MediaFileContent content, CancellationToken token)
    {
        string directory = Path.Combine(_sources, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        string copy = Path.Combine(directory, "source");
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var input = new FileStream(trustedPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var output = new FileStream(copy, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                byte[] buffer = new byte[64 * 1024]; long length = 0; int read;
                while ((read = await input.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
                {
                    length = checked(length + read);
                    if (length > content.ByteLength) throw SourceChanged();
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                }
                if (length != content.ByteLength) throw SourceChanged();
            }
            if (Convert.ToHexStringLower(hash.GetHashAndReset()) != content.Sha256.LowerHex) throw SourceChanged();
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(copy, UnixFileMode.UserRead);
            return copy;
        }
        catch
        {
            BackendPreparedProjectRuntimeInput.DeletePreparedDirectory(directory);
            throw;
        }
    }

    private void Advance()
    {
        Interlocked.Increment(ref _generation);
        try { Changed?.Invoke(); } catch (Exception) { /* Observers cannot fail analysis. */ }
    }

    private static MediaFailure Failure(MediaPreparationRequestId request, Exception error)
    {
        string code = error is MediaAbException media ? "media-" + media.Failure.ToString().ToLowerInvariant() : "media-analysis-failed";
        string detail = error is MediaAbException ? "背景媒体特征分析失败。" : "背景媒体特征分析异常终止。";
        return MediaFailure.Create(new(Digest("mako.media.failure.v1\n" + request.Digest.LowerHex + "\n" + code)), code, detail);
    }

    private static MediaPreparationRequestId RequestId(string key) => new(Digest("mako.media.request.v1\n" + key + "\n" + Guid.NewGuid().ToString("N")));
    private static MediaDigest Digest(string text) => new(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text))));
    private static MediaAbException SourceChanged() => new(MediaAbFailure.SourceChanged, "Background media changed while copied for analysis.");

    /// <summary>Stops new analyses and joins running jobs; native envelopes are killed through their owners.</summary>
    public async ValueTask DisposeAsync()
    {
        Task[] jobs;
        lock (_gate)
        {
            if (_stop.IsCancellationRequested) return;
            _stop.Cancel(); jobs = [.. _jobs];
        }
        try { await Task.WhenAll(jobs).WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false); }
        catch (Exception) { /* Shutdown continues; retained envelopes remain systemd-owned. */ }
    }

    private sealed class Entry(MediaPreparationRequestId request, int attempt)
    {
        private readonly object _gate = new();
        private MediaFeatureFileSource? _source;
        private BackendMediaFeatureArtifactReference? _artifact;
        private MediaFailure? _failure;
        private long? _failedAt;
        internal MediaPreparationRequestId Request { get; } = request;
        internal int Attempt { get; } = attempt;
        /// <summary>Timestamp of the finished attempt when it produced no artifact.</summary>
        internal long? FailedAt { get { lock (_gate) return _failedAt; } }
        internal void FinishAttempt(long timestamp) { lock (_gate) if (_artifact is null) _failedAt = timestamp; }
        internal TaskCompletionSource<MediaFeatureFileSource?> Inspected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void SetSource(MediaFeatureFileSource? source) { lock (_gate) _source ??= source; Inspected.TrySetResult(source); }
        internal void SetArtifact(BackendMediaFeatureArtifactReference artifact) { lock (_gate) _artifact = artifact; }
        internal void SetFailure(MediaFailure failure) { lock (_gate) _failure = failure; }
        internal BackendMediaFeatureResolution Snapshot(BackendMediaFeatureArtifactStore store)
        {
            lock (_gate) return new(_source, Request, _artifact, _artifact is null ? null : store.ArtifactFilePath(_artifact), _failure);
        }
    }

    private sealed class OwnedCopyLease : IMediaFileLease
    {
        private readonly FileStream _lock;
        internal OwnedCopyLease(string path, MediaFileContent content)
        {
            TrustedLocalPath = path; Content = content;
            _lock = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1);
        }
        public MediaFileContent Content { get; }
        public string TrustedLocalPath { get; }
        public ValueTask DisposeAsync() { _lock.Dispose(); return ValueTask.CompletedTask; }
    }
}
