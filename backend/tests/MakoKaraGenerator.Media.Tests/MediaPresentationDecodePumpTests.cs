using System.Buffers.Binary;
using System.Text;
using MakoKaraGenerator.Core.Media;
using MakoKaraGenerator.Processes.MediaEnvelope;
using MakoKaraGenerator.Rendering.Abstractions;
using Xunit;
using static MakoKaraGenerator.Media.Tests.MediaPresentationFixture;

namespace MakoKaraGenerator.Media.Tests;

public sealed class MediaPresentationDecodePumpTests
{
    private static readonly string[] ExpectedEvents = ["F0", "I0", "F1", "I1", "F2", "I2"];
    [Fact]
    public async Task ReorderedPacketsPublishExactFrameIntervalOrderAndFullIndexHash()
    {
        var fixture = new PumpFixture(); var sink = new Sink();
        PresentationIndexSummary summary = await MediaPresentationDecodePump.RunAsync(fixture.Source, fixture.File, null, fixture.Session,
            fixture.Deployment, sink, fixture.Operation, default);
        Assert.Equal(ExpectedEvents, sink.Events);
        Assert.Equal(3, summary.FrameCount); Assert.Equal(3000, summary.VideoDurationUs);
        Assert.Equal(PresentationEndAuthority.NextPts, ((PresentationInterval.Timed)sink.Intervals[0]).EndAuthority);
        Assert.Equal(PresentationEndAuthority.FrameDuration, ((PresentationInterval.Timed)sink.Intervals[2]).EndAuthority);
        Assert.Equal(new byte[] { (byte)'N', (byte)'N', (byte)'N' }, ((MemoryStream)fixture.Session.Input).ToArray());
        using var canonical = new MemoryStream(); foreach (PresentationInterval interval in sink.Intervals)
        { byte[] bytes = new byte[64]; Mft1Wire.EncodeInterval(bytes, interval); canonical.Write(bytes); }
        Assert.Equal(Hash(canonical.ToArray()), summary.IndexDigest); Assert.Equal(0, fixture.Operation.LiveManagedBytes); Assert.Equal(0, fixture.Operation.ActiveBorrows);
        await fixture.Operation.ReleaseSourceAsync();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task MissingFullFlushOrRepeatedPacketProofNeverReturnsSummary(bool fullyFlushed, bool duplicate)
    {
        var fixture = new PumpFixture(fullyFlushed, duplicate); var sink = new Sink();
        await Assert.ThrowsAsync<MediaAbException>(() => MediaPresentationDecodePump.RunAsync(fixture.Source, fixture.File, null, fixture.Session,
            fixture.Deployment, sink, fixture.Operation, default));
        Assert.Equal(0, fixture.Operation.LiveManagedBytes); Assert.Equal(0, fixture.Operation.ActiveBorrows); await fixture.Operation.ReleaseSourceAsync();
    }

    [Fact]
    public async Task CancellationDuringBorrowPropagatesOriginalTokenAndReleasesManagedCapacity()
    {
        var fixture = new PumpFixture(); using var cancellation = new CancellationTokenSource(); var sink = new Sink(cancellation);
        OperationCanceledException error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => MediaPresentationDecodePump.RunAsync(fixture.Source, fixture.File, null, fixture.Session,
            fixture.Deployment, sink, fixture.Operation, cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken); Assert.Equal(0, fixture.Operation.LiveManagedBytes); Assert.Equal(0, fixture.Operation.ActiveBorrows); await fixture.Operation.ReleaseSourceAsync();
    }

    private sealed class Sink(CancellationTokenSource? cancellation = null) : IMediaPresentationSink
    {
        internal List<string> Events { get; } = []; internal List<PresentationInterval> Intervals { get; } = [];
        public async ValueTask OnFrameAsync(PresentationFrameBorrow frame, CancellationToken token)
        {
            Events.Add("F" + frame.Observation.Id.Index); float first = frame.LinearPremulRgba.Span[0];
            await Task.Yield(); Assert.Equal(first, frame.LinearPremulRgba.Span[0]); Assert.Equal(4, frame.LinearPremulRgba.Length);
            cancellation?.Cancel(); token.ThrowIfCancellationRequested();
        }
        public ValueTask OnIntervalAsync(PresentationInterval interval, CancellationToken token)
        { Events.Add("I" + interval.Id.Index); Intervals.Add(interval); return ValueTask.CompletedTask; }
    }

    private sealed class PumpFixture
    {
        internal MediaFeatureFileSource Source { get; }
        internal MemoryStream File { get; }
        internal Session Session { get; }
        internal MediaPresentationDeployment Deployment { get; }
        internal DecodeOperationBudget Operation { get; } = new(MediaAbCapacityV1.MaximumDecodeWorkingBytes, 64L * 1024 * 1024, [64L * 1024 * 1024], 2, new NoopLease());
        internal PumpFixture(bool fullyFlushed = true, bool duplicate = false)
        {
            byte[] movie = Movie(out long offset, composition: true); File = new(movie);
            MediaDigest toolHash = Hash([1]);
            string gatePath = OperatingSystem.IsWindows() ? @"C:\controlled\gate" : "/controlled/gate";
            string helperPath = OperatingSystem.IsWindows() ? @"C:\controlled\helper" : "/controlled/helper";
            MediaLoadedToolFile gate = new(gatePath, new("gate", 1, toolHash)), helper = new(helperPath, new("helper", 1, toolHash));
            Deployment = new(gate.TrustedPath, helper.TrustedPath, [gate, helper], []);
            MediaNativePresentationMetadata first = Metadata(0, 0, offset, 0, Hash(movie.AsSpan((int)offset, 3)));
            Source = MediaPresentationInterpretation.Create(new(Hash(movie), movie.Length), new(MediaSourceKind.Video, 0, null), first, null, Deployment.Tool);
            using var metadata = new MemoryStream();
            for (int ordinal = 0; ordinal < 3; ordinal++)
            {
                int packet = ordinal switch { 0 => 0, 1 => 2, _ => 1 }; if (duplicate && ordinal == 1) packet = 0;
                Loaded(metadata, helper); Record(metadata, 5, []);
                Record(metadata, 2, FrameBytes(Metadata(ordinal, packet, offset + 3 * packet, ordinal, Hash(movie.AsSpan((int)offset + 3 * packet, 3)))));
            }
            Loaded(metadata, helper); Record(metadata, 5, []); byte[] complete = new byte[24];
            BinaryPrimitives.WriteInt64LittleEndian(complete, 3); BinaryPrimitives.WriteInt64LittleEndian(complete.AsSpan(8), 3); BinaryPrimitives.WriteUInt32LittleEndian(complete.AsSpan(16), fullyFlushed ? 1u : 0u); Record(metadata, 3, complete);
            Session = new(new MemoryStream(metadata.ToArray()), new MemoryStream([10, 20, 30, 70, 80, 90, 40, 50, 60]));
        }
    }

    internal static MediaNativePresentationMetadata Metadata(long ordinal, long packet, long offset, long pts, MediaDigest hash) =>
        new(ordinal, packet, offset, 3, new(pts, new(1, 1000)), 1, 1, 1, MediaNativePixelFormat.Rgb24,
            1, 13, 0, 2, 0, 0, 1, 1, default, 1, 3, 3, 0, 0, 0, 0, 7, 0, 1, hash);
    internal static byte[] FrameBytes(MediaNativePresentationMetadata frame)
    {
        byte[] bytes = new byte[224];
        void I64(int at, long value) => BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(at), value);
        void I32(int at, int value) => BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(at), value);
        I64(0, frame.Ordinal); I64(8, frame.PacketId); I64(16, frame.PacketPosition); I32(24, frame.PacketBytes); I64(28, frame.Pts.Ticks); I32(36, frame.Pts.TimeBase.Numerator); I32(40, frame.Pts.TimeBase.Denominator); I64(44, frame.DecoderDurationTicks);
        I32(52, frame.Width); I32(56, frame.Height); I32(60, (int)frame.PixelFormat); I32(64, frame.Primaries); I32(68, frame.Transfer); I32(72, frame.Matrix); I32(76, frame.Range); I32(80, frame.Chroma); I32(84, frame.Alpha); I32(88, frame.SarNumerator); I32(92, frame.SarDenominator);
        I32(136, frame.PlaneCount); I32(140, frame.FirstStride); I32(144, frame.FirstBytes); I32(148, frame.CbStride); I32(152, frame.CbBytes); I32(156, frame.CrStride); I32(160, frame.CrBytes); I32(164, frame.StreamId); I32(168, frame.StreamIndex); I32(172, frame.Family); Convert.FromHexString(frame.PacketHash.LowerHex).CopyTo(bytes, 176); return bytes;
    }
    internal static void Loaded(Stream output, MediaLoadedToolFile file)
    {
        byte[] path = Encoding.UTF8.GetBytes(file.TrustedPath), value = new byte[44 + path.Length]; BinaryPrimitives.WriteInt64LittleEndian(value, file.ManifestEntry.ByteLength);
        Convert.FromHexString(file.ManifestEntry.ContentSha256.LowerHex).CopyTo(value, 8); BinaryPrimitives.WriteUInt32LittleEndian(value.AsSpan(40), (uint)path.Length); path.CopyTo(value, 44); Record(output, 4, value);
    }
    internal static void Record(Stream output, uint type, byte[] value)
    {
        byte[] head = new byte[16]; "MKGPRE1\0"u8.CopyTo(head); BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(8), type); BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(12), (uint)value.Length); output.Write(head); output.Write(value);
    }

    private sealed class Session(Stream metadata, Stream output) : IEnvelopeSession
    {
        public Stream Input { get; } = new MemoryStream(); public Stream Output => output; public Stream Metadata => metadata; public Stream Diagnostics { get; } = Stream.Null;
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
