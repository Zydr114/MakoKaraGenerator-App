using MakoKaraGenerator.Core.Media;
using MakoKaraGenerator.Rendering.Abstractions;

namespace MakoKaraGenerator.Media.Tests;

internal sealed class Mft1TestBudget(long limit) : IMediaWorkingSetBudget
{
    private long _live;
    private readonly object _sync = new();
    internal long Live => Interlocked.Read(ref _live);
    internal long Peak { get; private set; }
    public long LimitBytes => limit;
    public IMediaMemoryReservation Reserve(long bytes, MediaWorkingSetPurpose purpose)
    {
        lock (_sync)
        {
            if (bytes <= 0 || bytes > limit - _live)
                throw new MediaAbException(MediaAbFailure.CapacityExceeded, "Test owner ledger refused the reservation.");
            _live += bytes; Peak = Math.Max(Peak, _live); return new Charge(this, bytes);
        }
    }
    private sealed class Charge(Mft1TestBudget owner, long bytes) : IMediaMemoryReservation
    {
        private int _released;
        public long Bytes => bytes;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) lock (owner._sync) owner._live -= bytes;
        }
    }
}

internal sealed class Mft1TestScratch(long bytes) : IMediaTrackScratchReservation
{
    private readonly Dictionary<MediaTrackSection, BoundedSection> _sections = [];
    internal int OpenCount => _sections.Count;
    internal int ClosedCount => _sections.Values.Count(s => s.Closed);
    internal bool Disposed { get; private set; }
    internal Action<MediaTrackSection>? BeforeOpen { get; set; }
    public long ReservedByteBudget => bytes;
    public ValueTask<Stream> OpenSectionAsync(MediaTrackSection section, long maximumBytes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); ObjectDisposedException.ThrowIf(Disposed, this);
        if (_sections.ContainsKey(section)) throw new InvalidOperationException("Section was opened twice.");
        BeforeOpen?.Invoke(section);
        var stream = new BoundedSection(maximumBytes); _sections.Add(section, stream);
        return ValueTask.FromResult<Stream>(stream);
    }
    public async ValueTask DisposeAsync()
    {
        Disposed = true;
        foreach (BoundedSection section in _sections.Values) await section.DisposeAsync();
    }
    private sealed class BoundedSection(long maximum) : MemoryStream
    {
        internal bool Closed { get; private set; }
        private void Check(long count)
        {
            if (count > maximum - Position) throw new MediaAbException(MediaAbFailure.CapacityExceeded, "Bounded section exceeds its admission.");
        }
        public override void Write(ReadOnlySpan<byte> buffer) { Check(buffer.Length); base.Write(buffer); }
        public override void Write(byte[] buffer, int offset, int count) { Check(count); base.Write(buffer, offset, count); }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Check(buffer.Length); return base.WriteAsync(buffer, cancellationToken);
        }
        public override void SetLength(long value)
        {
            if (value > maximum) throw new MediaAbException(MediaAbFailure.CapacityExceeded, "Bounded section length exceeds its admission.");
            base.SetLength(value);
        }
        protected override void Dispose(bool disposing) { Closed = true; base.Dispose(disposing); }
    }
}

internal sealed class Mft1ObservedInput(byte[] bytes) : MemoryStream(bytes, false)
{
    internal long BytesRead { get; private set; }
    internal int Reads { get; private set; }
    internal bool Closed { get; private set; }
    internal Action? BeforeRead { get; set; }
    internal void ResetObservations() { BytesRead = 0; Reads = 0; }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        BeforeRead?.Invoke(); cancellationToken.ThrowIfCancellationRequested();
        int read = await base.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        BytesRead += read; Reads++; return read;
    }
    protected override void Dispose(bool disposing) { Closed = true; base.Dispose(disposing); }
}

internal sealed class Mft1FixtureWriteSource : IMediaFeatureTrackWriteSource
{
    private readonly Mft1IndependentFixture _fixture;
    internal int IntervalLengthDelta { get; set; }
    internal bool Disposed { get; private set; }
    internal Mft1FixtureWriteSource(Mft1IndependentFixture fixture)
    {
        _fixture = fixture;
        MediaArtifactIdentity identity = fixture.Identity;
        Presentation = new(identity.SourceKey, identity.ProfileDigest, identity.SourceDescriptorDigest,
            identity.PresentationIndexDigest, identity.FrameCount, identity.VideoDurationUs);
    }
    public MediaFeatureFileSource Source => _fixture.Source;
    public PresentationIndexSummary Presentation { get; }
    public ValueTask CopyIntervalsToAsync(Stream target, CancellationToken cancellationToken) =>
        CopyAsync(target, 256, checked((int)_fixture.Count * 64 + IntervalLengthDelta), cancellationToken);
    public ValueTask CopyGridsToAsync(Stream target, CancellationToken cancellationToken) =>
        CopyAsync(target, _fixture.GridOffset, checked((int)_fixture.Count * 9216), cancellationToken);
    public ValueTask CopyPrefixesToAsync(Stream target, CancellationToken cancellationToken) =>
        CopyAsync(target, _fixture.PrefixOffset, _fixture.Bytes.Length - _fixture.PrefixOffset, cancellationToken);
    private ValueTask CopyAsync(Stream target, int offset, int count, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        return target.WriteAsync(_fixture.Bytes.AsMemory(offset, count), token);
    }
    public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
}
