using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Media;
using MakoKaraGenerator.Rendering.Abstractions;

namespace MakoKaraGenerator.Media;

/// <summary>Bounded synchronous CPU sampling of one fixed input through the shared raw-moment algorithms.</summary>
/// <remarks>Owns only its grids and charge. The injected fully validated reader remains borrowed from its outer read-lease owner.</remarks>
public sealed partial class MediaFeatureSampler : IProjectMediaFeatureSampler, IDisposable
{
    private readonly IMediaFeatureTrackReader? _reader;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly MediaMomentKernel _kernel = new();
    private readonly MediaCanvasMomentQuery _canvas;
    private readonly MediaDisplayPlacement? _placement;
    private IMediaMemoryReservation? _reservation;
    private MediaMomentValues[] _grids = [];
    private Slot[] _slots = [];
    private int _closing;
    private bool _disposed;

    /// <summary>Validates a fixed input and plan, then admits exact numerical backing through the shared runtime ledger.</summary>
    /// <param name="input">The immutable complete fixed input.</param>
    /// <param name="plan">The exact Core-resolved dependency plan.</param>
    /// <param name="validatedReader">Only AvailableFile may carry a reader, already opened by the complete codec gate.</param>
    /// <param name="budget">The same owner ledger as the reader, with a runtime CPU ceiling.</param>
    /// <param name="cancellationToken">Cancellation propagated unchanged before publishing an initialized provider.</param>
    public MediaFeatureSampler(MediaFeatureInput input, ProjectMediaDependencyPlan plan,
        IMediaFeatureTrackReader? validatedReader, IMediaWorkingSetBudget budget, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(budget); cancellationToken.ThrowIfCancellationRequested();
        ProjectMediaFeatureBinding.ValidateInputPlan(input, plan);
        ValidateReader(input, validatedReader);
        if (budget.LimitBytes <= 0 || budget.LimitBytes > MediaAbCapacityV1.MaximumRuntimeCpuCacheBytes)
            throw new MediaAbException(MediaAbFailure.CapacityUnsupported, "Sampler requires a shared ledger within the fixed runtime CPU cache ceiling.");
        Input = input; Plan = plan; _reader = validatedReader; _canvas = new(_kernel);
        _placement = Placement(input);
        int count = input.State != MediaInputState.Available || plan.Dependencies.IsEmpty ? 0
            : input is MediaFeatureInput.AvailableSolid ? 1 : plan.DistinctWindowsUs.Length;
        if (count == 0) { cancellationToken.ThrowIfCancellationRequested(); return; }
        long bytes = WorkingBytes(count, input is MediaFeatureInput.AvailableSolid);
        _reservation = budget.Reserve(bytes, MediaWorkingSetPurpose.ReaderCache);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            _slots = new Slot[count];
            _grids = new MediaMomentValues[checked(count * MediaTrackLayoutV1.Cells)];
            for (int i = 0; i < count; i++) _slots[i].WindowUs = plan.DistinctWindowsUs[i];
            if (input is MediaFeatureInput.AvailableSolid solid) InitializeSolid(solid, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch
        {
            _grids = []; _slots = []; _reservation?.Dispose(); _reservation = null;
            throw;
        }
    }

    /// <inheritdoc/>
    public MediaFeatureInput Input { get; }
    /// <inheritdoc/>
    public ProjectMediaDependencyPlan Plan { get; }

    /// <inheritdoc/>
    public ProjectMediaFeatureSample Sample(ProjectMediaDependency dependency, MediaFeatureQueryRect rect,
        long projectTimeUs, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); EnsureActive();
        _gate.Wait(cancellationToken);
        try
        {
            EnsureActive(); cancellationToken.ThrowIfCancellationRequested();
            ProjectMediaFeatureBinding.RequireDependency(Plan, dependency);
            ValidateRequest(dependency, rect, projectTimeUs);
            if (Input.State != MediaInputState.Available)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return new(Input, dependency, rect, projectTimeUs);
            }
            int slot = Input is MediaFeatureInput.AvailableSolid ? 0 : FindSlot(dependency.WindowUs);
            try
            {
                Memory<MediaMomentValues> grid = _grids.AsMemory(checked(slot * MediaTrackLayoutV1.Cells), MediaTrackLayoutV1.Cells);
                if (!_slots[slot].Valid || (Input is not MediaFeatureInput.AvailableSolid && _slots[slot].ProjectTimeUs != projectTimeUs))
                {
                    // Invalidate before touching existing backing. A failed replacement cannot retain its old key.
                    _slots[slot].Valid = false;
                    _reader!.ReadTemporalGrid(new(projectTimeUs, dependency.WindowUs, Input.Context.Mapping.Playback), grid, cancellationToken);
                }
                ProjectMediaFeatureSample result = Integrate(dependency, rect, projectTimeUs, grid.Span, cancellationToken);
                // No key becomes visible until both the complete read and numerical snapshot succeeded.
                cancellationToken.ThrowIfCancellationRequested();
                _slots[slot].ProjectTimeUs = projectTimeUs; _slots[slot].Valid = true;
                return result;
            }
            catch
            {
                // File slots may contain a failed replacement. A solid grid is immutable after construction.
                if (Input is MediaFeatureInput.AvailableFile) _slots[slot].Valid = false;
                throw;
            }
        }
        finally { _gate.Release(); }
    }

    /// <summary>Stops query admission, waits for the current synchronous query, and releases only owned backing and charge.</summary>
    /// <remarks>Does not dispose the borrowed reader. The outer owner disposes its read lease only after this drain returns.</remarks>
    public void Dispose()
    {
        Interlocked.Exchange(ref _closing, 1);
        _gate.Wait();
        try
        {
            if (_disposed) return;
            _grids = []; _slots = []; _reservation?.Dispose(); _reservation = null; _disposed = true;
        }
        finally { _gate.Release(); }
        // Keep the fixed gate usable for racing already-waiting callers, which observe closing and fail.
    }

    private void EnsureActive() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _closing) != 0, this);

    private int FindSlot(long windowUs)
    {
        for (int i = 0; i < _slots.Length; i++) if (_slots[i].WindowUs == windowUs) return i;
        throw new MediaAbException(MediaAbFailure.ProfileUnsupported, "Declared media window is not present in the fixed input plan.");
    }

    private static long WorkingBytes(int count, bool solid) => checked(
        count * (long)MediaTrackLayoutV1.Cells * MediaTrackLayoutV1.Moments * sizeof(double)
        + 512L + count * 64L + MediaMomentKernel.WorkingBufferBytes
        + (solid ? (long)MediaTrackLayoutV1.Cells * MediaTrackLayoutV1.Moments * sizeof(ushort) : 0));

    private struct Slot
    {
        internal long WindowUs;
        internal long ProjectTimeUs;
        internal bool Valid;
    }
}
