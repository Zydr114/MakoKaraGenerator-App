using MakoKaraGenerator.Core.Media;
using MakoKaraGenerator.Rendering.Abstractions;

namespace MakoKaraGenerator.Media;

internal sealed class MediaFeatureTrackWriteSource : IMediaFeatureTrackWriteSource
{
    internal static readonly long WorkingBytes = Mft1Wire.CopyBufferBytes + 3L * IntPtr.Size + 3L * sizeof(long);
    private Stream[] _sections = [];
    private long[] _lengths = [];
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IMediaMemoryReservation _reservation;
    private byte[] _copyBuffer;
    private bool _disposed;

    internal MediaFeatureTrackWriteSource(MediaFeatureFileSource source, PresentationIndexSummary presentation,
        Stream intervals, Stream grids, Stream prefixes, IMediaWorkingSetBudget budget)
    {
        Source = source; Presentation = presentation;
        Mft1Layout layout = Mft1Layout.Create(source.Kind, presentation.FrameCount);
        _reservation = budget.Reserve(WorkingBytes, MediaWorkingSetPurpose.Builder);
        try
        {
            _sections = [intervals, grids, prefixes];
            _lengths = [layout.IntervalBytes, layout.GridBytes, layout.PrefixBytes];
            _copyBuffer = new byte[Mft1Wire.CopyBufferBytes];
        }
        catch { _reservation.Dispose(); throw; }
    }

    public MediaFeatureFileSource Source { get; }
    public PresentationIndexSummary Presentation { get; }
    public ValueTask CopyIntervalsToAsync(Stream target, CancellationToken cancellationToken) => CopyAsync(0, target, cancellationToken);
    public ValueTask CopyGridsToAsync(Stream target, CancellationToken cancellationToken) => CopyAsync(1, target, cancellationToken);
    public ValueTask CopyPrefixesToAsync(Stream target, CancellationToken cancellationToken) => CopyAsync(2, target, cancellationToken);

    private async ValueTask CopyAsync(int section, Stream target, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(target);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this); token.ThrowIfCancellationRequested();
            Mft1Wire.Require(target.CanWrite && !_sections.Contains(target), "Section copy target is not an independent writable stream.");
            Stream input = _sections[section]; long length = _lengths[section];
            Mft1Wire.CheckLength(input, length); input.Position = 0;
            long remaining = length;
            while (remaining > 0)
            {
                int count = (int)Math.Min(remaining, _copyBuffer.Length);
                int read = await input.ReadAsync(_copyBuffer.AsMemory(0, count), token).ConfigureAwait(false);
                if (read == 0) throw Mft1Wire.Invalid("Scratch section ended before its declared length.");
                await target.WriteAsync(_copyBuffer.AsMemory(0, read), token).ConfigureAwait(false);
                remaining -= read;
            }
            Mft1Wire.CheckLength(input, length);
        }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return; _disposed = true;
            try { await Mft1Wire.DisposeAllAsync(_sections).ConfigureAwait(false); }
            finally { _sections = []; _lengths = []; _copyBuffer = []; _reservation.Dispose(); }
        }
        finally { _gate.Release(); }
    }
}
