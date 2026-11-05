using MakoKaraGenerator.Core.Media;
using MakoKaraGenerator.Media;
using MakoKaraGenerator.Processes.MediaEnvelope;
using MakoKaraGenerator.Rendering.Abstractions;

namespace M10.Presentation.Probe;

internal sealed class RejectionProbe(ClosureManifest manifest, FixtureSet fixtures, RejectionSet rejections,
    string fixtureRoot, EvidenceJournal journal, long nativeBytes)
{
    internal async Task<bool> RunAsync(CancellationToken token)
    {
        foreach (RejectionCase item in rejections.Cases)
            await InspectRejectedAsync(item, manifest, token).ConfigureAwait(false);
        FixtureCase positive = fixtures.Cases.FirstOrDefault(item => item.Family == 4)
            ?? throw new ProbeFailure("probe-rejections-need-real-jpeg-positive");
        await RequirePositiveInspectAsync(positive, token).ConfigureAwait(false);

        LoadedFile pinned = manifest.Files.Single(item => item.Path == manifest.Helper);
        string changedHash = (pinned.Sha256[0] == 'a' ? "b" : "a") + pinned.Sha256[1..];
        ClosureManifest stale = manifest with { Files = manifest.Files.Select(item => item == pinned
            ? item with { Sha256 = changedHash } : item).ToArray() };
        await InspectRejectedAsync(AsCase(positive, "closure-pinned-helper-hash-stale", native: false,
            "Expected manifest hash changed while all real tool files remain untouched"), stale, token).ConfigureAwait(false);

        string missingStatus;
        LoadedFile? dependency = manifest.Files.FirstOrDefault(item => !item.Executable
            && Path.GetFileName(item.Path).StartsWith("libavcodec", StringComparison.Ordinal));
        if (dependency is null)
        {
            missingStatus = "unsupported-no-separate-libavcodec-loaded-entry";
            journal.Write("closure-undeclared-loaded-library", "scenario-unsupported", new { code = missingStatus,
                scope = "finite-negative-needs-one-actual-libavcodec-entry" });
        }
        else
        {
            ClosureManifest missing = manifest with { Files = manifest.Files.Where(item => item != dependency).ToArray() };
            await InspectRejectedAsync(AsCase(positive, "closure-undeclared-loaded-library", native: true,
                "One observed libavcodec entry omitted while all real tool bytes remain unchanged"), missing, token).ConfigureAwait(false);
            missingStatus = "passed";
        }
        await RejectLateEpochAsync(positive, token).ConfigureAwait(false);
        journal.Write("run", "finite-rejection-matrix-result", new { authoredSourceRejections = rejections.Cases.Length, stalePinnedHash = "passed",
            undeclaredLoadedLibrary = missingStatus, lateEpochProtocolInjection = "passed",
            complete = missingStatus == "passed", physicalLateDlopenOrFileReplacement = "not-exercised" });
        return missingStatus == "passed";
    }

    private static RejectionCase AsCase(FixtureCase source, string name, bool native, string reason)
        => new(name, source.File, source.ByteLength, source.Sha256, source.Kind, source.StreamIndex,
            MediaAbFailure.ProfileUnsupported, native, reason);

    private async Task InspectRejectedAsync(RejectionCase item, ClosureManifest deployment,
        CancellationToken caller)
    {
        var raw = new PrivateFixtureLease(ProbeFiles.Child(fixtureRoot, item.File), new MediaFileContent(new(item.Sha256), item.ByteLength));
        await using var file = new MediaEnvelopeFileLease(raw);
        var budget = new MediaWorkingSetBudget(MediaAbCapacityV1.MaximumDecodeWorkingBytes);
        var quarantine = new JournalQuarantine(journal, item.Name);
        var backend = new AuditedEnvelopeBackend(new LinuxSystemdEnvelopeBackend(), journal, item.Name, (ulong)nativeBytes);
        var real = new MediaEnvelopeRunner(backend, quarantine, nativeBytes);
        var decoder = new MediaPresentationDecoder(deployment.Deployment(), real);
        using var duration = CancellationTokenSource.CreateLinkedTokenSource(caller);
        duration.CancelAfter(TimeSpan.FromSeconds(35));
        Exception? outcome = null;
        try
        {
            _ = await decoder.InspectAsync(file, new(item.Kind, item.StreamIndex, null), budget, duration.Token).ConfigureAwait(false);
        }
        catch (Exception error) { outcome = error; }
        if (quarantine.Count != 0 || real.RetainedOperationCount != 0) Program.RetainFailure(quarantine, real, file);
        if (outcome is MediaAbException { Failure: MediaAbFailure.CapacityUnsupported })
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(outcome).Throw();
        if (outcome is not MediaAbException failure || failure.Failure != item.ExpectedFailure)
            throw new ProbeFailure("probe-rejection-category-or-acceptance-mismatch");
        if (item.NativeExpected)
        {
            AuditDrain.Require(backend, quarantine, journal, item.Name, budget.LiveBytes, real.RetainedOperationCount);
            if (backend.Opened!.KillRequests < 1) throw new ProbeFailure("probe-native-rejection-did-not-own-kill");
        }
        else if (backend.Opened is not null || budget.LiveBytes != 0 || quarantine.Count != 0 || real.RetainedOperationCount != 0)
            throw new ProbeFailure("probe-preexec-rejection-started-or-retained-native");
        await file.DisposeAsync().ConfigureAwait(false);
        if (!raw.Released) throw new ProbeFailure("probe-rejected-source-not-released");
        journal.Write(item.Name, "expected-rejection-drained", new { item.ExpectedFailure, item.NativeExpected,
            item.Reason, failure.SafeDetail, sourceReleased = raw.Released, budget = budget.GetSnapshot() });
    }

    private async Task RequirePositiveInspectAsync(FixtureCase item, CancellationToken token)
    {
        string name = "closure-unmodified-positive-control";
        var raw = new PrivateFixtureLease(ProbeFiles.Child(fixtureRoot, item.File), item);
        await using var file = new MediaEnvelopeFileLease(raw);
        var budget = new MediaWorkingSetBudget(MediaAbCapacityV1.MaximumDecodeWorkingBytes);
        var quarantine = new JournalQuarantine(journal, name);
        var backend = new AuditedEnvelopeBackend(new LinuxSystemdEnvelopeBackend(), journal, name, (ulong)nativeBytes);
        var real = new MediaEnvelopeRunner(backend, quarantine, nativeBytes);
        var decoder = new MediaPresentationDecoder(manifest.Deployment(), real);
        using var duration = CancellationTokenSource.CreateLinkedTokenSource(token);
        duration.CancelAfter(TimeSpan.FromSeconds(35));
        try { _ = await decoder.InspectAsync(file, new(item.Kind, item.StreamIndex, item.Override), budget, duration.Token).ConfigureAwait(false); }
        catch { if (quarantine.Count != 0 || real.RetainedOperationCount != 0) Program.RetainFailure(quarantine, real, file); throw; }
        AuditDrain.Require(backend, quarantine, journal, name, budget.LiveBytes, real.RetainedOperationCount);
        await file.DisposeAsync().ConfigureAwait(false);
        if (!raw.Released) throw new ProbeFailure("probe-positive-control-source-not-released");
        journal.Write(name, "unmodified-real-source-control-passed");
    }

    private async Task RejectLateEpochAsync(FixtureCase item, CancellationToken caller)
    {
        const string name = "closure-late-epoch-protocol-hash-injection";
        var raw = new PrivateFixtureLease(ProbeFiles.Child(fixtureRoot, item.File), item);
        await using var file = new MediaEnvelopeFileLease(raw);
        var budget = new MediaWorkingSetBudget(MediaAbCapacityV1.MaximumDecodeWorkingBytes);
        var quarantine = new JournalQuarantine(journal, name);
        var backend = new AuditedEnvelopeBackend(new LinuxSystemdEnvelopeBackend(), journal, name, (ulong)nativeBytes);
        var real = new MediaEnvelopeRunner(backend, quarantine, nativeBytes);
        MediaPresentationDeployment deployment = manifest.Deployment();
        using var duration = CancellationTokenSource.CreateLinkedTokenSource(caller);
        duration.CancelAfter(TimeSpan.FromSeconds(35));
        var normal = new MediaPresentationDecoder(deployment, real);
        MediaFeatureFileSource accepted;
        try { accepted = await normal.InspectAsync(file, new(item.Kind, item.StreamIndex, item.Override), budget, duration.Token).ConfigureAwait(false); }
        catch { if (quarantine.Count != 0 || real.RetainedOperationCount != 0) Program.RetainFailure(quarantine, real, file); throw; }
        AuditDrain.Require(backend, quarantine, journal, name, budget.LiveBytes, real.RetainedOperationCount);
        await using var source = new MediaPresentationSourceLease(file, accepted);
        var injection = new LoadedClosureFaultRunner(real);
        var decoder = new MediaPresentationDecoder(deployment, injection);
        var sink = new CountingSink();
        Exception? outcome = null;
        try { _ = await decoder.DecodeAsync(source, sink, budget, duration.Token).ConfigureAwait(false); }
        catch (Exception error) { outcome = error; }
        if (quarantine.Count != 0 || real.RetainedOperationCount != 0) Program.RetainFailure(quarantine, real, source, injection);
        if (outcome is MediaAbException { Failure: MediaAbFailure.CapacityUnsupported })
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(outcome).Throw();
        if (!injection.Injected || sink.Frames != 1 || outcome is not MediaAbException { Failure: MediaAbFailure.ProfileUnsupported })
            throw new ProbeFailure("probe-late-closure-fault-not-rejected");
        AuditDrain.Require(backend, quarantine, journal, name, budget.LiveBytes, real.RetainedOperationCount);
        await source.DisposeAsync().ConfigureAwait(false);
        await file.DisposeAsync().ConfigureAwait(false);
        if (!raw.Released) throw new ProbeFailure("probe-late-fault-source-not-released");
        journal.Write(name, "late-epoch-protocol-fault-rejected-and-drained", new { injection.Injected, sink.Frames,
            sink.Intervals, backend.Opened!.KillRequests, physicalToolFilesModified = false,
            realOwnedNative = true, budget = budget.GetSnapshot() });
    }

    private sealed class CountingSink : IMediaPresentationSink
    {
        internal int Frames { get; private set; }
        internal int Intervals { get; private set; }
        private CancellationToken? _callbackToken;
        private void RequireToken(CancellationToken token)
        {
            if (!token.CanBeCanceled || (_callbackToken is { } actual && token != actual))
                throw new ProbeFailure("probe-late-fault-callback-token-invalid");
            _callbackToken ??= token; token.ThrowIfCancellationRequested();
        }
        public ValueTask OnFrameAsync(PresentationFrameBorrow frame, CancellationToken cancellationToken)
        {
            RequireToken(cancellationToken);
            if (frame.LinearPremulRgba.IsEmpty || Frames != 0)
                throw new ProbeFailure("probe-late-fault-frame-or-token-invalid");
            foreach (float value in frame.LinearPremulRgba.Span)
                if (!float.IsFinite(value)) throw new ProbeFailure("probe-late-fault-frame-not-finite");
            Frames++;
            return ValueTask.CompletedTask;
        }
        public ValueTask OnIntervalAsync(PresentationInterval interval, CancellationToken cancellationToken)
        {
            RequireToken(cancellationToken);
            Intervals++;
            return ValueTask.CompletedTask;
        }
    }
}
