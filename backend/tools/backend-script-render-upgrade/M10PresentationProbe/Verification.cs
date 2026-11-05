using System.Buffers.Binary;
using System.Security.Cryptography;
using MakoKaraGenerator.Core.Media;
using MakoKaraGenerator.Media;
using MakoKaraGenerator.Processes.MediaEnvelope;
using MakoKaraGenerator.Rendering.Abstractions;

namespace M10.Presentation.Probe;

internal sealed class Verification(ClosureManifest manifest, string fixtureRoot, FixtureSet fixtures,
    EvidenceJournal journal, long nativeBytes)
{
    private readonly MediaPresentationDeployment _deployment = manifest.Deployment();

    internal async Task RunAsync(CancellationToken token)
    {
        foreach (FixtureCase item in fixtures.Cases)
        {
            await RunOneAsync(item, cancelDuringBorrow: false, token).ConfigureAwait(false);
            await RunOneAsync(item, cancelDuringBorrow: true, token).ConfigureAwait(false);
        }
    }

    private async Task RunOneAsync(FixtureCase item, bool cancelDuringBorrow, CancellationToken caller)
    {
        string name = "verify-" + item.Name + (cancelDuringBorrow ? "-cancel-borrow" : "-complete");
        var raw = new PrivateFixtureLease(ProbeFiles.Child(fixtureRoot, item.File), item);
        await using var file = new MediaEnvelopeFileLease(raw);
        var budget = new MediaWorkingSetBudget(MediaAbCapacityV1.MaximumDecodeWorkingBytes);
        var quarantine = new JournalQuarantine(journal, name);
        var backend = new AuditedEnvelopeBackend(new LinuxSystemdEnvelopeBackend(), journal, name, (ulong)nativeBytes);
        var runner = new MediaEnvelopeRunner(backend, quarantine, nativeBytes);
        var decoder = new MediaPresentationDecoder(_deployment, runner);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(caller);
        timeout.CancelAfter(TimeSpan.FromSeconds(50));
        MediaFeatureFileSource accepted;
        try
        {
            accepted = await decoder.InspectAsync(file, new(item.Kind, item.StreamIndex, item.Override),
                budget, timeout.Token).ConfigureAwait(false);
        }
        catch
        {
            if (quarantine.Count != 0 || runner.RetainedOperationCount != 0) Program.RetainFailure(quarantine, runner, file);
            throw;
        }
        AuditDrain.Require(backend, quarantine, journal, name, budget.LiveBytes, runner.RetainedOperationCount);
        RequireDescriptor(accepted.Presentation, item);
        journal.Write(name, "inspected-source-accepted", new { accepted.Key, accepted.Content,
            accepted.Presentation, accepted.Tool, budget = budget.GetSnapshot() });
        await using var source = new MediaPresentationSourceLease(file, accepted);
        if (cancelDuringBorrow)
        {
            await CancelBorrowAsync(decoder, source, file, raw, budget, backend, runner, quarantine, name, caller).ConfigureAwait(false);
        }
        else
        {
            using var sink = new OracleSink(item, accepted, fixtureRoot, journal, name, budget, timeout.Token);
            PresentationIndexSummary summary;
            try
            {
                summary = await decoder.DecodeAsync(source, sink, budget, timeout.Token).ConfigureAwait(false);
            }
            catch
            {
                if (quarantine.Count != 0 || runner.RetainedOperationCount != 0) Program.RetainFailure(quarantine, runner, source);
                throw;
            }
            sink.Complete(summary);
            // The sink owns one bounded oracle scratch reservation, released before budget-zero assertion.
            sink.Dispose();
            AuditDrain.Require(backend, quarantine, journal, name, budget.LiveBytes, runner.RetainedOperationCount);
            journal.Write(name, "complete-source-verified", new { summary, budget = budget.GetSnapshot(),
                loadedManifestProvenance = manifest.Provenance, toolDigest = accepted.Tool.Digest });
        }
        await source.DisposeAsync().ConfigureAwait(false);
        await file.DisposeAsync().ConfigureAwait(false);
        if (!raw.Released || budget.LiveBytes != 0 || quarantine.Count != 0 || runner.RetainedOperationCount != 0)
            throw new ProbeFailure("probe-final-source-budget-or-quarantine-live");
        journal.Write(name, "verified-source-finally-released", new { raw.Released, budget.LiveBytes });
    }

    private async Task CancelBorrowAsync(MediaPresentationDecoder decoder, MediaPresentationSourceLease source,
        MediaEnvelopeFileLease file, PrivateFixtureLease raw, MediaWorkingSetBudget budget,
        AuditedEnvelopeBackend backend, MediaEnvelopeRunner runner,
        JournalQuarantine quarantine, string name, CancellationToken caller)
    {
        using var original = CancellationTokenSource.CreateLinkedTokenSource(caller);
        original.CancelAfter(TimeSpan.FromSeconds(40));
        var sink = new HeldBorrowSink(journal, name);
        Task<PresentationIndexSummary> decode = decoder.DecodeAsync(source, sink, budget, original.Token).AsTask();
        Exception? outcome = null;
        bool killedWhileHeld = false;
        try
        {
            using var enteredDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await sink.Entered.Task.WaitAsync(enteredDeadline.Token).ConfigureAwait(false);
            var session = backend.Opened ?? throw new ProbeFailure("probe-cancel-session-missing");
            if (session.KillRequests != 0) throw new ProbeFailure("probe-cancel-kill-preceded-request");
            // Drop both importing owners while the actual decoder callback is live. The producer's holds
            // must independently retain the underlying authorized source until callback/native drain.
            await source.DisposeAsync().ConfigureAwait(false);
            await file.DisposeAsync().ConfigureAwait(false);
            if (raw.Released) throw new ProbeFailure("probe-source-released-during-borrow");
            original.Cancel();
            journal.Write(name, "original-caller-cancel-requested");
            using var killDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            await session.KillCompleted.Task.WaitAsync(killDeadline.Token).ConfigureAwait(false);
            if (!sink.IsHeld || decode.IsCompleted || budget.LiveBytes == 0 || raw.Released)
                throw new ProbeFailure("probe-borrow-or-reservation-released-before-consumer");
            sink.RequireUnchanged();
            if (!sink.CallbackToken.IsCancellationRequested) throw new ProbeFailure("probe-callback-cancellation-not-propagated");
            killedWhileHeld = true;
            journal.Write(name, "real-native-killed-while-borrow-held", new { sink.IsHeld, budget.LiveBytes,
                session.Identity, session.KillRequests });
        }
        finally
        {
            original.Cancel();
            sink.Release.TrySetResult();
            try { await decode.ConfigureAwait(false); }
            catch (Exception error) { outcome = error; }
            if (quarantine.Count != 0 || runner.RetainedOperationCount != 0)
                Program.RetainFailure(quarantine, runner, source, sink);
        }
        if (!killedWhileHeld || outcome is not OperationCanceledException canceled || canceled.CancellationToken != original.Token)
            throw new ProbeFailure("probe-original-cancellation-not-preserved");
        if (!raw.Released) throw new ProbeFailure("probe-source-retained-after-canceled-drain");
        AuditDrain.Require(backend, quarantine, journal, name, budget.LiveBytes, runner.RetainedOperationCount);
        journal.Write(name, "cancel-borrow-drained", new { callerTokenPreserved = true,
            callbackCancellationPropagated = true, budget = budget.GetSnapshot() });
    }

    private static void RequireDescriptor(MediaFilePresentationDescriptor value, FixtureCase item)
    {
        MediaColorInterpretation color = value.Color;
        if (value.StreamIndex != item.StreamIndex || value.EncodedWidth != item.Width || value.EncodedHeight != item.Height
            || value.CanonicalWidth != item.CanonicalWidth || value.CanonicalHeight != item.CanonicalHeight
            || value.SarNumerator != item.SarNumerator || value.SarDenominator != item.SarDenominator
            || value.Rotation != item.Rotation || value.MirrorHorizontallyBeforeRotation != item.Mirror
            || color.Transfer != item.Transfer || color.Matrix != item.Matrix || color.Range != item.Range
            || color.Alpha != item.Alpha || color.ChromaLocation != item.ChromaLocation
            || color.NativePixelFormat != item.NativePixelFormat || color.FromTrustedMissingTagOverride != item.MissingTagOverride)
            throw new ProbeFailure("probe-descriptor-color-or-geometry-mismatch");
    }
}

internal sealed class OracleSink : IMediaPresentationSink, IDisposable
{
    private readonly FixtureCase _item;
    private readonly MediaFeatureFileSource _accepted;
    private readonly EvidenceJournal _journal;
    private readonly string _name;
    private readonly CancellationToken _original;
    private CancellationToken? _callbackToken;
    private readonly FileStream _pixels;
    private readonly byte[] _scratch;
    private readonly IMediaMemoryReservation _reservation;
    private int _frames, _intervals;
    private readonly IncrementalHash _indexHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private bool _disposed;

    internal OracleSink(FixtureCase item, MediaFeatureFileSource accepted, string root, EvidenceJournal journal, string name,
        MediaWorkingSetBudget budget, CancellationToken original)
    {
        _item = item; _accepted = accepted; _journal = journal; _name = name; _original = original;
        _reservation = budget.Reserve(8192, MediaWorkingSetPurpose.CodecValidation);
        FileStream? opened = null;
        try
        {
            _scratch = new byte[4096];
            opened = new(ProbeFiles.Child(root, item.PixelsFile), FileMode.Open, FileAccess.Read, FileShare.Read, 1);
            if (opened.Length != item.PixelsByteLength || ProbeFiles.Hash(opened) != item.PixelsSha256)
                throw new ProbeFailure("probe-pixel-oracle-content-changed");
            _pixels = opened;
        }
        catch { opened?.Dispose(); _reservation.Dispose(); throw; }
    }

    public ValueTask OnFrameAsync(PresentationFrameBorrow frame, CancellationToken cancellationToken)
    {
        RequireToken(cancellationToken);
        if (_frames != _intervals || _frames >= _item.Frames.Length || frame.Observation.Id.Index != _frames
            || frame.Observation.Width != _item.CanonicalWidth || frame.Observation.Height != _item.CanonicalHeight
            || frame.LinearPremulRgba.Length != _item.CanonicalWidth * _item.CanonicalHeight * 4
            || frame.SourceKey != _accepted.Key || frame.ProfileDigest != _accepted.Profile.Digest
            || frame.Observation.ColorInterpretationDigest != _accepted.Presentation.Color.Digest)
            throw new ProbeFailure("probe-frame-count-order-or-geometry-mismatch");
        ExpectedFrame expected = _item.Frames[_frames];
        if (expected.Pts is null ? frame.Observation.RawPts is not null
            : frame.Observation.RawPts is not { } pts || !expected.Pts.EqualsTime(pts))
            throw new ProbeFailure("probe-raw-pts-mismatch");
        ReadOnlySpan<float> actual = frame.LinearPremulRgba.Span;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        double maxError = 0;
        for (int offset = 0; offset < actual.Length;)
        {
            int take = Math.Min(_scratch.Length / 4, actual.Length - offset);
            _pixels.ReadExactly(_scratch.AsSpan(0, take * 4));
            for (int i = 0; i < take; i++)
            {
                float expectedValue = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(_scratch.AsSpan(i * 4)));
                float observed = actual[offset + i];
                if (!float.IsFinite(observed) || !float.IsFinite(expectedValue)) throw new ProbeFailure("probe-pixels-not-finite");
                double error = Math.Abs((double)observed - expectedValue);
                maxError = Math.Max(maxError, error);
                if (error > _item.PixelAbsoluteTolerance) throw new ProbeFailure("probe-full-frame-pixels-mismatch");
                BinaryPrimitives.WriteInt32LittleEndian(_scratch.AsSpan(i * 4), BitConverter.SingleToInt32Bits(observed));
            }
            hash.AppendData(_scratch.AsSpan(0, take * 4));
            offset += take;
        }
        _journal.Write(_name, "full-frame-pixels-and-pts-verified", new { ordinal = _frames,
            frame.Observation, canonicalPixelsSha256 = Convert.ToHexStringLower(hash.GetHashAndReset()),
            maxAbsoluteError = maxError, _item.PixelOracle, _item.PixelAbsoluteTolerance });
        _frames++;
        return ValueTask.CompletedTask;
    }

    public ValueTask OnIntervalAsync(PresentationInterval interval, CancellationToken cancellationToken)
    {
        RequireToken(cancellationToken);
        if (_frames != _intervals + 1 || interval.Id.Index != _intervals) throw new ProbeFailure("probe-interval-order-mismatch");
        ExpectedFrame expected = _item.Frames[_intervals];
        if (_item.Kind == MediaSourceKind.Still)
        {
            if (interval is not PresentationInterval.Timeless) throw new ProbeFailure("probe-still-invented-time");
        }
        else
        {
            PresentationEndAuthority authority = _intervals == _item.Frames.Length - 1
                ? _item.TailAuthority : PresentationEndAuthority.NextPts;
            if (interval is not PresentationInterval.Timed timed || expected.Pts is null || expected.End is null
                || !expected.Pts.EqualsTime(timed.RawStart) || !expected.End.EqualsTime(timed.RawEnd)
                || timed.StartUs != expected.StartUs || timed.EndUs != expected.EndUs || timed.EndAuthority != authority)
                throw new ProbeFailure("probe-interval-or-tail-authority-mismatch");
        }
        // Independent fixed 64-byte interval encoding: no product codec is called.
        Span<byte> record = stackalloc byte[64]; record.Clear();
        if (interval is PresentationInterval.Timed value)
        {
            BinaryPrimitives.WriteInt64LittleEndian(record, value.RawStart.Ticks);
            BinaryPrimitives.WriteInt32LittleEndian(record[8..], value.RawStart.TimeBase.Numerator);
            BinaryPrimitives.WriteInt32LittleEndian(record[12..], value.RawStart.TimeBase.Denominator);
            BinaryPrimitives.WriteInt64LittleEndian(record[16..], value.RawEnd.Ticks);
            BinaryPrimitives.WriteInt32LittleEndian(record[24..], value.RawEnd.TimeBase.Numerator);
            BinaryPrimitives.WriteInt32LittleEndian(record[28..], value.RawEnd.TimeBase.Denominator);
            BinaryPrimitives.WriteInt64LittleEndian(record[32..], value.StartUs);
            BinaryPrimitives.WriteInt64LittleEndian(record[40..], value.EndUs);
            BinaryPrimitives.WriteInt32LittleEndian(record[48..], (int)value.EndAuthority);
        }
        else BinaryPrimitives.WriteInt32LittleEndian(record[48..], (int)PresentationEndAuthority.Timeless);
        _indexHash.AppendData(record);
        _journal.Write(_name, "exact-interval-verified", interval);
        _intervals++;
        return ValueTask.CompletedTask;
    }

    internal void Complete(PresentationIndexSummary summary)
    {
        if (_frames != _item.Frames.Length || _frames != _intervals || summary.FrameCount != _frames
            || summary.VideoDurationUs != _item.VideoDurationUs || _pixels.Position != _pixels.Length
            || summary.SourceKey != _accepted.Key || summary.ProfileDigest != _accepted.Profile.Digest
            || summary.SourceDescriptorDigest != _accepted.Presentation.Digest
            || summary.IndexDigest.LowerHex != Convert.ToHexStringLower(_indexHash.GetHashAndReset()))
            throw new ProbeFailure("probe-drained-summary-or-oracle-tail-mismatch");
    }

    private void RequireToken(CancellationToken token)
    {
        _original.ThrowIfCancellationRequested();
        if (!token.CanBeCanceled || (_callbackToken is { } actual && token != actual))
            throw new ProbeFailure("probe-callback-token-invalid");
        _callbackToken ??= token; token.ThrowIfCancellationRequested();
    }
    public void Dispose()
    { if (_disposed) return; _disposed = true; _pixels.Dispose(); _indexHash.Dispose(); _reservation.Dispose(); }
}

internal sealed class HeldBorrowSink(EvidenceJournal journal, string name) : IMediaPresentationSink
{
    internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ReadOnlyMemory<float> _borrow;
    private ulong _pixelSignature;
    internal bool IsHeld { get; private set; }
    internal CancellationToken CallbackToken { get; private set; }

    public async ValueTask OnFrameAsync(PresentationFrameBorrow frame, CancellationToken cancellationToken)
    {
        if (!cancellationToken.CanBeCanceled || cancellationToken.IsCancellationRequested)
            throw new ProbeFailure("probe-callback-token-invalid");
        CallbackToken = cancellationToken;
        _borrow = frame.LinearPremulRgba;
        if (_borrow.IsEmpty) throw new ProbeFailure("probe-cancel-borrow-empty");
        _pixelSignature = Signature(_borrow.Span); IsHeld = true;
        journal.Write(name, "real-callback-borrow-held", new { frame.Observation, callbackTokenCancelable = CallbackToken.CanBeCanceled });
        Entered.TrySetResult();
        try
        {
            // Deliberately ignore cancel while holding borrowed memory. The owner must kill native first,
            // await this exact consumer, and release its buffers only after this method returns.
            await Release.Task.ConfigureAwait(false);
            RequireUnchanged();
        }
        finally { IsHeld = false; _borrow = default; }
        cancellationToken.ThrowIfCancellationRequested();
    }

    internal void RequireUnchanged()
    {
        if (!IsHeld || _borrow.IsEmpty || Signature(_borrow.Span) != _pixelSignature)
            throw new ProbeFailure("probe-borrow-mutated-before-consumer-return");
    }

    private static ulong Signature(ReadOnlySpan<float> pixels)
    {
        // Allocation-free whole-borrow mutation sentinel. Full canonical pixel validation is separate.
        ulong signature = 14695981039346656037UL;
        foreach (float pixel in pixels)
        {
            uint bits = (uint)BitConverter.SingleToInt32Bits(pixel);
            for (int shift = 0; shift < 32; shift += 8)
                signature = unchecked((signature ^ (byte)(bits >> shift)) * 1099511628211UL);
        }
        return signature;
    }

    public ValueTask OnIntervalAsync(PresentationInterval interval, CancellationToken cancellationToken)
        => throw new ProbeFailure("probe-canceled-borrow-published-interval");
}
