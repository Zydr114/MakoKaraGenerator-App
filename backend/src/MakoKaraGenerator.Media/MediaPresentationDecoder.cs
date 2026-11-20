using MakoKaraGenerator.Core.Media;
using MakoKaraGenerator.Processes.MediaEnvelope;
using MakoKaraGenerator.Rendering.Abstractions;

namespace MakoKaraGenerator.Media;

/// <summary>Controlled first-frame inspector and complete same-chain presentation decoder.</summary>
/// <remarks>No native work starts without a hard envelope and retained source hold. The existing legacy video provider is unchanged.</remarks>
public sealed class MediaPresentationDecoder : IMediaPresentationInspector, IMediaPresentationDecoder
{
    private readonly MediaPresentationDeployment _deployment;
    private readonly IMediaPresentationNativeRunner _runner;
    private const long CommonManagedBytes = DecodeOperationBudget.TransportReservationPerProducer + 64 * 1024 + 4096 + 64 * 1024 + 128 * 1024;

    /// <summary>Creates one immutable controlled decoder service.</summary>
    /// <param name="deployment">Pinned gate helper and complete actual library closure.</param>
    /// <param name="runner">Hard native envelope with atomic owner admission and source quarantine.</param>
    public MediaPresentationDecoder(MediaPresentationDeployment deployment, IMediaPresentationNativeRunner runner)
    { _deployment = deployment ?? throw new ArgumentNullException(nameof(deployment)); _runner = runner ?? throw new ArgumentNullException(nameof(runner)); }

    /// <inheritdoc />
    public async ValueTask<MediaFeatureFileSource> InspectAsync(IMediaFileLease source, MediaPresentationInspectionRequest request,
        IMediaWorkingSetBudget decodeBudget, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request); IMediaRetainableFileLease retained = RequireRetainable(source);
        if (request.Kind is not (MediaSourceKind.Still or MediaSourceKind.Video) || request.StreamIndex < 0 || request.StreamIndex >= 128 || request.Kind == MediaSourceKind.Still && request.StreamIndex != 0)
            throw new MediaAbException(MediaAbFailure.ProfileUnsupported, "Inspection requires a supported file kind and exact selected stream index.");
        await using IAsyncDisposable sourceHold = retained.AcquireHold();
        int family; MediaStillStructure? still;
        await _deployment.VerifyFilesAsync(decodeBudget, cancellationToken).ConfigureAwait(false);
        await using (FileStream file = OpenSource(source))
        {
            family = MediaStillStructure.DetectFamily(file, request.Kind);
            still = request.Kind == MediaSourceKind.Still ? MediaStillStructure.Read(file, family, decodeBudget, cancellationToken) : null;
        }
        MediaFeatureFileSource? accepted = null;
        long managedPeak = checked(CommonManagedBytes + _deployment.WireCapacityBytes + MediaContainerTimingAuthority.MaximumWorkingBytes + MediaStillStructure.ScratchBytes + 512 * 1024);
        await _runner.RunAsync(retained, decodeBudget, _deployment.Command(source, family, request.StreamIndex, true), managedPeak,
            async (session, operation, token) =>
            {
                var managed = new MediaPresentationManagedBudget(operation);
                using var wire = new MediaPresentationWire(_deployment.Files, managed, _deployment.RequireExactClosure);
                using IMediaMemoryReservation small = managed.Reserve(128 * 1024, MediaWorkingSetPurpose.Metadata);
                byte[] scratch = new byte[64 * 1024]; byte[] ack = [(byte)'S'];
                await using FileStream file = OpenSource(source);
                await MediaPresentationSourceVerification.VerifyAsync(file, source.Content, scratch, token).ConfigureAwait(false);
                object first = await wire.ReadNextAsync(session.Metadata, token).ConfigureAwait(false);
                if (first is not MediaNativePresentationMetadata frame || frame.Ordinal != 0 || frame.Family != family)
                    throw Invalid("Native inspection did not produce the exact selected first presentation frame.");
                MediaPresentationDecodePump.ValidatePlaneLayout(frame);
                accepted = MediaPresentationInterpretation.Create(source.Content, request, frame, still, _deployment.Tool);
                if (request.Kind == MediaSourceKind.Video)
                {
                    using MediaContainerTimingAuthority timing = family == 1 ? MediaContainerTimingAuthority.ReadMov(file, frame.StreamId, managed, token)
                        : MediaContainerTimingAuthority.ReadMatroska(file, MediaContainerTimingAuthority.ReadMatroskaTrackNumber(file, frame.PacketPosition), managed, token);
                    _ = timing.VerifyPacket(file, frame.PacketPosition, frame.PacketBytes, frame.Pts, frame.PacketHash, token);
                }
                else if (frame.PacketPosition != 0 || frame.PacketBytes != source.Content.ByteLength || frame.PacketHash != source.Content.Sha256)
                    throw Invalid("Native still inspection does not bind exactly the complete structurally validated file.");
                await session.Input.WriteAsync(ack, token).ConfigureAwait(false); await session.Input.FlushAsync(token).ConfigureAwait(false);
                object final = await wire.ReadNextAsync(session.Metadata, token).ConfigureAwait(false);
                if (final is not MediaNativePresentationComplete complete || complete.FullyFlushed || complete.Frames != 1 || complete.Packets < 1 || complete.Packets > MediaAbCapacityV1.MaximumFrames)
                    throw Invalid("Native first-frame inspection completion is malformed or claims full decode flush.");
                await MediaPresentationWire.RequireEofAsync(session.Output, session.Metadata, token).ConfigureAwait(false);
                await MediaPresentationSourceVerification.VerifyAsync(file, source.Content, scratch, token).ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);
        await _deployment.VerifyFilesAsync(decodeBudget, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested(); return accepted ?? throw Invalid("Inspection completed without an accepted first-frame descriptor.");
    }

    /// <inheritdoc />
    public async ValueTask<PresentationIndexSummary> DecodeAsync(IMediaSourceLease source, IMediaPresentationSink sink,
        IMediaWorkingSetBudget decodeBudget, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sink); IMediaRetainableFileLease retained = RequireRetainable(source);
        new MediaAbValueValidator().ValidateSource(source.Source);
        if (source.Content != source.Source.Content || source.Source.Tool.Digest != _deployment.Tool.Digest)
            throw new MediaAbException(MediaAbFailure.PresentationMismatch, "Decode source content or controlled tool identity differs from its accepted source.");
        await using IAsyncDisposable hold = retained.AcquireHold();
        await _deployment.VerifyFilesAsync(decodeBudget, cancellationToken).ConfigureAwait(false);
        int family; MediaStillStructure? still;
        await using (FileStream file = OpenSource(source))
        {
            family = MediaStillStructure.DetectFamily(file, source.Source.Kind);
            still = source.Source.Kind == MediaSourceKind.Still ? MediaStillStructure.Read(file, family, decodeBudget, cancellationToken) : null;
        }
        long encodedPixels = checked((long)source.Source.Presentation.EncodedWidth * source.Source.Presentation.EncodedHeight);
        // Every allowed native representation is at most 8 bytes per encoded pixel. Admission is conservative; no fidelity is lowered.
        long managedPeak = checked(CommonManagedBytes + _deployment.WireCapacityBytes + MediaContainerTimingAuthority.MaximumWorkingBytes +
            4L * 1024 * 1024 + encodedPixels * 24 + 1024 * 1024);
        PresentationIndexSummary? summary = null;
        await _runner.RunAsync(retained, decodeBudget, _deployment.Command(source, family, source.Source.Presentation.StreamIndex, false), managedPeak,
            async (session, operation, token) =>
            {
                var managed = new MediaPresentationManagedBudget(operation);
                using IMediaMemoryReservation hashCharge = managed.Reserve(64 * 1024 + 4096, MediaWorkingSetPurpose.Metadata);
                byte[] scratch = new byte[64 * 1024];
                await using FileStream file = OpenSource(source);
                await MediaPresentationSourceVerification.VerifyAsync(file, source.Content, scratch, token).ConfigureAwait(false);
                summary = await MediaPresentationDecodePump.RunAsync(source.Source, file, still, session, _deployment, sink, operation, token).ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);
        await _deployment.VerifyFilesAsync(decodeBudget, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested(); return summary ?? throw Invalid("Native decode completed without a complete presentation index.");
    }

    /// <inheritdoc />
    public ValueTask<IMediaPresentationFrameSource> OpenIndexedAsync(IMediaSourceLease source, IMediaPresentationIndex validatedIndex,
        IMediaWorkingSetBudget decodeBudget, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); ArgumentNullException.ThrowIfNull(validatedIndex);
        IMediaRetainableFileLease retained = RequireRetainable(source); new MediaAbValueValidator().ValidateSource(source.Source);
        Mft1Wire.ValidateSummary(source.Source, validatedIndex.Summary);
        return ValueTask.FromResult<IMediaPresentationFrameSource>(new ExactSequentialSource(this, source, retained.AcquireHold(), validatedIndex, decodeBudget));
    }

    private sealed class ExactSequentialSource(MediaPresentationDecoder decoder, IMediaSourceLease source, IAsyncDisposable hold,
        IMediaPresentationIndex index, IMediaWorkingSetBudget budget) : IMediaPresentationFrameSource
    {
        private readonly object _admission = new();
        private TaskCompletionSource? _active;
        private bool _disposeRequested;
        private Task? _disposeCompletion;
        public PresentationIndexSummary Index => index.Summary;
        public async ValueTask ReadFrameAsync(PresentationFrameId id, Func<PresentationFrameBorrow, CancellationToken, ValueTask> consume, CancellationToken token)
        {
            TaskCompletionSource completion;
            lock (_admission)
            {
                // Admission and stop-admission are one decision. A read cannot
                // pass disposal and then acquire the source after its hold left.
                ObjectDisposedException.ThrowIf(_disposeRequested, this);
                ArgumentNullException.ThrowIfNull(consume); token.ThrowIfCancellationRequested();
                if (id.Index < 0 || id.Index >= Index.FrameCount) throw Invalid("Requested exact presentation ordinal is outside the validated index.");
                if (_active is not null) throw Invalid("Exact presentation frame reader is single-flight.");
                completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _active = completion;
            }
            try
            {
                var sink = new ExactSink(source.Source, index, id, consume);
                PresentationIndexSummary actual = await decoder.DecodeAsync(source, sink, budget, token).ConfigureAwait(false);
                if (actual != Index || !sink.Consumed) throw new MediaAbException(MediaAbFailure.PresentationMismatch, "Complete sequential decode does not match the validated exact presentation index.");
            }
            finally
            {
                // DecodeAsync has joined its callback, native runner, post-read
                // verification and operation hold before this completion signal.
                lock (_admission) _active = null;
                completion.TrySetResult();
            }
        }
        public ValueTask DisposeAsync()
        {
            Task completion; Task active = Task.CompletedTask;
            TaskCompletionSource? begin = null;
            lock (_admission)
            {
                if (_disposeCompletion is null)
                {
                    begin = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    active = _active?.Task ?? Task.CompletedTask;
                    _disposeRequested = true;
                    _disposeCompletion = begin.Task;
                }
                completion = _disposeCompletion;
            }
            // Never call external hold disposal while holding the admission lock.
            // Publishing the shared task first also covers synchronous callbacks.
            if (begin is not null) _ = CompleteDisposalAsync(active, begin);
            return new(completion);
        }
        private async Task CompleteDisposalAsync(Task active, TaskCompletionSource completion)
        {
            try
            {
                await active.ConfigureAwait(false);
                await hold.DisposeAsync().ConfigureAwait(false);
                completion.TrySetResult();
            }
            catch (Exception error) { completion.TrySetException(error); }
        }
    }

    private sealed class ExactSink(MediaFeatureFileSource source, IMediaPresentationIndex index, PresentationFrameId target,
        Func<PresentationFrameBorrow, CancellationToken, ValueTask> consume) : IMediaPresentationSink
    {
        internal bool Consumed { get; private set; }
        public async ValueTask OnFrameAsync(PresentationFrameBorrow frame, CancellationToken token)
        {
            PresentationInterval expected = await index.ReadIntervalAsync(frame.Observation.Id, token).ConfigureAwait(false);
            RationalTimestamp? pts = expected is PresentationInterval.Timed timed ? timed.RawStart : null;
            if (expected.Id != frame.Observation.Id || frame.SourceKey != source.Key || frame.ProfileDigest != source.Profile.Digest ||
                frame.Observation.RawPts != pts || frame.Observation.Width != source.Presentation.CanonicalWidth || frame.Observation.Height != source.Presentation.CanonicalHeight ||
                frame.Observation.ColorInterpretationDigest != source.Presentation.Color.Digest)
                throw new MediaAbException(MediaAbFailure.PresentationMismatch, "Sequential exact-frame observation disagrees with its validated index.");
            if (frame.Observation.Id == target) { if (Consumed) throw Invalid("Exact target presentation was published more than once."); await consume(frame, token).ConfigureAwait(false); Consumed = true; }
        }
        public async ValueTask OnIntervalAsync(PresentationInterval interval, CancellationToken token)
        { if (await index.ReadIntervalAsync(interval.Id, token).ConfigureAwait(false) != interval) throw new MediaAbException(MediaAbFailure.PresentationMismatch, "Sequential exact interval disagrees with its validated index."); }
    }

    private static IMediaRetainableFileLease RequireRetainable(IMediaFileLease source)
    { ArgumentNullException.ThrowIfNull(source); return source as IMediaRetainableFileLease ?? throw new MediaAbException(MediaAbFailure.CapacityUnsupported, "Native presentation requires an owner-transferred retainable source lease for verified drain and quarantine."); }
    private static FileStream OpenSource(IMediaFileLease source)
    {
        try { return new(source.TrustedLocalPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.Asynchronous | FileOptions.RandomAccess); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { throw new MediaAbException(MediaAbFailure.SourceChanged, "Authorized media source cannot be opened with its pinned local read policy."); }
    }
    private static MediaAbException Invalid(string detail) => new(MediaAbFailure.PresentationInvalid, detail);

}
