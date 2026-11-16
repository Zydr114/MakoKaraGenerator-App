using System.Buffers.Binary;
using MakoKaraGenerator.Core.Media;
using MakoKaraGenerator.Processes.MediaEnvelope;
using MakoKaraGenerator.Rendering.Abstractions;
using Xunit;
using static MakoKaraGenerator.Media.Tests.MediaPresentationFixture;
using static MakoKaraGenerator.Media.Tests.MediaPresentationDecodePumpTests;

namespace MakoKaraGenerator.Media.Tests;

/// <summary>Complete managed pump and real container proofs over synthetic bytes; no native decoding is claimed.</summary>
public sealed class MediaPresentationMatroskaTimingTests
{
    private static readonly string[] ExpectedEvents = ["F0", "I0", "F1", "I1", "F2", "I2"];
    [Fact]
    public async Task DefaultDurationRoundingCannotOverrideNextActualMillisecondPts()
    {
        using var fixture = new Fixture([0, 33, 67], defaultDuration: true, finalDurationMs: null); var sink = new Sink();
        PresentationIndexSummary summary = await fixture.RunAsync(sink);
        Assert.Equal(new long[] { 0, 33_000, 67_000 }, sink.Intervals.Select(value => value.StartUs));
        Assert.Equal(new long[] { 33_000, 67_000, 100_334 }, sink.Intervals.Select(value => value.EndUs));
        Assert.Equal(100_334, summary.VideoDurationUs); Assert.Equal(3, summary.FrameCount);
        Assert.Equal(PresentationEndAuthority.NextPts, sink.Intervals[0].EndAuthority);
        Assert.Equal(PresentationEndAuthority.NextPts, sink.Intervals[1].EndAuthority);
        Assert.Equal(PresentationEndAuthority.FrameDuration, sink.Intervals[2].EndAuthority);
        Assert.Equal(ExpectedEvents, sink.Events);
        Assert.Equal(0, fixture.Operation.LiveManagedBytes); await fixture.Operation.ReleaseSourceAsync();
    }

    [Fact]
    public async Task VfrNeedsExplicitDurationOnlyOnItsFinalPresentationFrame()
    {
        using var fixture = new Fixture([0, 40, 100], defaultDuration: false, finalDurationMs: 40); var sink = new Sink();
        PresentationIndexSummary summary = await fixture.RunAsync(sink);
        Assert.Equal(new long[] { 0, 40_000, 100_000 }, sink.Intervals.Select(value => value.StartUs));
        Assert.Equal(new long[] { 40_000, 100_000, 140_000 }, sink.Intervals.Select(value => value.EndUs));
        Assert.Equal(140_000, summary.VideoDurationUs);
        Assert.Equal(0, fixture.Operation.LiveManagedBytes); await fixture.Operation.ReleaseSourceAsync();
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0UL)]
    public async Task MissingOrNonpositiveFinalDurationNeverReturnsCompletedIndex(ulong? finalDurationMs)
    {
        using var fixture = new Fixture([0, 40, 100], defaultDuration: false, finalDurationMs); var sink = new Sink();
        MediaAbException error = await Assert.ThrowsAsync<MediaAbException>(() => fixture.RunAsync(sink));
        Assert.Equal(MediaAbFailure.PresentationInvalid, error.Failure);
        Assert.Equal(0, fixture.Operation.LiveManagedBytes); Assert.Equal(0, fixture.Operation.ActiveBorrows);
        await fixture.Operation.ReleaseSourceAsync();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task DuplicateOrDecreasingActualPtsStillFail(long nextPts)
    {
        using var fixture = new Fixture([0, nextPts, 100], defaultDuration: true, finalDurationMs: null);
        await Assert.ThrowsAsync<MediaAbException>(() => fixture.RunAsync(new Sink()));
        Assert.Equal(0, fixture.Operation.LiveManagedBytes); await fixture.Operation.ReleaseSourceAsync();
    }

    private sealed class Sink : IMediaPresentationSink
    {
        internal List<string> Events { get; } = [];
        internal List<PresentationInterval.Timed> Intervals { get; } = [];
        public ValueTask OnFrameAsync(PresentationFrameBorrow frame, CancellationToken token)
        { Events.Add("F" + frame.Observation.Id.Index); token.ThrowIfCancellationRequested(); return ValueTask.CompletedTask; }
        public ValueTask OnIntervalAsync(PresentationInterval interval, CancellationToken token)
        { Events.Add("I" + interval.Id.Index); Intervals.Add(Assert.IsType<PresentationInterval.Timed>(interval)); return ValueTask.CompletedTask; }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly MediaFeatureFileSource _source;
        private readonly MemoryStream _file;
        private readonly Session _session;
        private readonly MediaPresentationDeployment _deployment;
        internal DecodeOperationBudget Operation { get; } = new(MediaAbCapacityV1.MaximumDecodeWorkingBytes,
            64L * 1024 * 1024, [64L * 1024 * 1024], 2, new NoopLease());
        internal Fixture(long[] ptsMs, bool defaultDuration, ulong? finalDurationMs)
        {
            byte[] file = MatroskaFrames(out long[] positions, out long[] payloads, ptsMs, defaultDuration, finalDurationMs);
            _file = new(file); MediaDigest toolHash = Hash([1]);
            string gatePath = OperatingSystem.IsWindows() ? @"C:\controlled\gate" : "/controlled/gate";
            string helperPath = OperatingSystem.IsWindows() ? @"C:\controlled\helper" : "/controlled/helper";
            MediaLoadedToolFile gate = new(gatePath, new("gate", 1, toolHash)), helper = new(helperPath, new("helper", 1, toolHash));
            _deployment = new(gate.TrustedPath, helper.TrustedPath, [gate, helper], []);
            MediaNativePresentationMetadata Frame(int ordinal) => new(ordinal, ordinal, positions[ordinal], 3,
                new(ptsMs[ordinal], new(1, 1000)), 0, 1, 1, MediaNativePixelFormat.Rgb24,
                1, 13, 0, 2, 0, 0, 1, 1, default, 1, 3, 3, 0, 0, 0, 0, 5, 0, 2,
                Hash(file.AsSpan((int)payloads[ordinal], 3)));
            _source = MediaPresentationInterpretation.Create(new(Hash(file), file.Length), new(MediaSourceKind.Video, 0, null), Frame(0), null, _deployment.Tool);
            using var metadata = new MemoryStream(); using var pixels = new MemoryStream();
            for (int ordinal = 0; ordinal < ptsMs.Length; ordinal++)
            {
                Loaded(metadata, helper); Record(metadata, 5, []); Record(metadata, 2, FrameBytes(Frame(ordinal)));
                pixels.Write(file.AsSpan((int)payloads[ordinal], 3));
            }
            Loaded(metadata, helper); Record(metadata, 5, []); byte[] complete = new byte[24];
            BinaryPrimitives.WriteInt64LittleEndian(complete, ptsMs.Length);
            BinaryPrimitives.WriteInt64LittleEndian(complete.AsSpan(8), ptsMs.Length); BinaryPrimitives.WriteUInt32LittleEndian(complete.AsSpan(16), 1);
            Record(metadata, 3, complete); _session = new(new MemoryStream(metadata.ToArray()), new MemoryStream(pixels.ToArray()));
        }
        internal Task<PresentationIndexSummary> RunAsync(IMediaPresentationSink sink) =>
            MediaPresentationDecodePump.RunAsync(_source, _file, null, _session, _deployment, sink, Operation, CancellationToken.None);
        public void Dispose() { _file.Dispose(); _session.Input.Dispose(); _session.Output.Dispose(); _session.Metadata.Dispose(); }
    }

    private sealed class Session(Stream metadata, Stream output) : IEnvelopeSession
    {
        public Stream Input { get; } = new MemoryStream(); public Stream Output => output;
        public Stream Metadata => metadata; public Stream Diagnostics => Stream.Null;
        public long PipeBackingBytes => 16384; public long ProgressVersion => 0; public bool StartAttempted => false;
        public ValueTask StartHeldAsync(EnvelopeStart start, CancellationToken token) => throw new NotSupportedException();
        public ValueTask<EnvelopeSnapshot> ObserveAsync(CancellationToken token) => throw new NotSupportedException();
        public ValueTask<bool> ConfirmStartAbsentAsync(CancellationToken token) => throw new NotSupportedException();
        public ValueTask ReleaseExecGateAsync(CancellationToken token) => throw new NotSupportedException();
        public ValueTask KillVerifiedCgroupAsync(EnvelopeIdentity identity, CancellationToken token) => throw new NotSupportedException();
        public ValueTask UnrefVerifiedAsync(EnvelopeIdentity identity, CancellationToken token) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
