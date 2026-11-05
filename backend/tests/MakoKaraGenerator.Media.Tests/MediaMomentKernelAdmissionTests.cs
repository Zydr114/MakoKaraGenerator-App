using System.Buffers;
using MakoKaraGenerator.Core.Media;
using Xunit;

namespace MakoKaraGenerator.Media.Tests;

/// <summary>Bounded scratch, cancellation, input memory and invalid canonical-pixel regressions.</summary>
public sealed class MediaMomentKernelAdmissionTests
{
    /// <summary>The scratch cap admits fixed backing before use and cannot enlarge the Core builder ceiling.</summary>
    [Fact]
    public void FixedScratchBudgetIsAdmittedWithoutOwningPixels()
    {
        Assert.Equal(224, MediaMomentKernel.WorkingBufferBytes);
        Assert.Equal(MediaMomentKernel.WorkingBufferBytes, new MediaMomentKernel().WorkingByteLimit);
        Assert.Equal(MediaAbFailure.CapacityExceeded, Assert.Throws<MediaAbException>(() => new MediaMomentKernel(223)).Failure);
        Assert.Equal(MediaAbFailure.CapacityUnsupported, Assert.Throws<MediaAbException>(() => new MediaMomentKernel(-1)).Failure);
        Assert.Equal(MediaAbFailure.CapacityUnsupported, Assert.Throws<MediaAbException>(() =>
            new MediaMomentKernel(MediaAbCapacityV1.MaximumBuilderWorkingBytes + 1)).Failure);
        Assert.Equal(576, MediaMomentKernel.GetMaximumPixelContributions(1, 1));
        Assert.Equal(16_974_351, MediaMomentKernel.GetMaximumPixelContributions(4096, 4096));
        Assert.Throws<MediaAbException>(() => MediaMomentKernel.GetMaximumPixelContributions(8192, 8192));
    }

    /// <summary>All operations require exactly the fixed 576-cell grid shape.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(575)]
    [InlineData(577)]
    public void GridLengthIsExact(int length)
    {
        var kernel = new MediaMomentKernel();
        var destination = new QuantizedMediaMoments[length];
        Assert.Throws<MediaAbException>(() => kernel.BuildGrid(MediaMomentKernelTests.Frame(1, 1, new float[] { 0, 0, 0, 1 }), destination, CancellationToken.None));
        Assert.Throws<MediaAbException>(() => kernel.BuildSolidGrid(new(0, 0, 0, 1), destination));
        Assert.Throws<MediaAbException>(() => kernel.IntegrateSourceRect(new MediaMomentValues[length], new(0, 0, 1, 1)));
    }

    /// <summary>Raster capacity and exact backing length are validated before scanning pixels.</summary>
    [Fact]
    public void InvalidRasterOrMemoryLengthIsRefused()
    {
        var kernel = new MediaMomentKernel();
        QuantizedMediaMoments[] grid = MediaMomentKernelTests.Grid();
        Assert.Throws<MediaAbException>(() => kernel.BuildGrid(MediaMomentKernelTests.Frame(0, 1, []), grid, CancellationToken.None));
        Assert.Throws<MediaAbException>(() => kernel.BuildGrid(MediaMomentKernelTests.Frame(1, 1, [0, 0, 0]), grid, CancellationToken.None));
        Assert.Throws<MediaAbException>(() => kernel.BuildGrid(MediaMomentKernelTests.Frame(1, 1, [0, 0, 0, 1, 0]), grid, CancellationToken.None));
        Assert.Equal(MediaAbFailure.CapacityExceeded, Assert.Throws<MediaAbException>(() =>
            kernel.BuildGrid(MediaMomentKernelTests.Frame(8192, 8192, []), grid, CancellationToken.None)).Failure);
    }

    /// <summary>NaN, infinities, hidden transparent RGB and unpremultiplied channels cannot be repaired.</summary>
    [Fact]
    public void InvalidCanonicalPixelsAndSolidsAreRejectedWithoutClamping()
    {
        var kernel = new MediaMomentKernel();
        float[][] invalid = [[float.NaN, 0, 0, 1], [0, float.PositiveInfinity, 0, 1], [0, 0, -0.01f, 1],
            [0.6f, 0, 0, 0.5f], [0.1f, 0, 0, 0], [0, 0, 0, 1.01f], [-0.0f, 0, 0, 1]];
        foreach (float[] pixel in invalid)
        {
            Assert.Equal(MediaAbFailure.StatisticsInvalid, Assert.Throws<MediaAbException>(() =>
                kernel.BuildGrid(MediaMomentKernelTests.Frame(1, 1, pixel), MediaMomentKernelTests.Grid(), CancellationToken.None)).Failure);
            Assert.Throws<MediaAbException>(() => kernel.BuildSolidGrid(new(pixel[0], pixel[1], pixel[2], pixel[3]), MediaMomentKernelTests.Grid()));
        }
    }

    /// <summary>Pre-cancellation escapes unchanged before input memory is touched.</summary>
    [Fact]
    public void PreCancelledOperationsDoNotTouchTheBorrow()
    {
        var kernel = new MediaMomentKernel();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var memory = new CallbackMemoryManager(static () => throw new InvalidOperationException("Borrow was touched."), [0, 0, 0, 1]);
        var frame = MediaMomentKernelTests.FrameMemory(1, 1, memory.ExposeMemory());
        Assert.Throws<OperationCanceledException>(() => kernel.BuildGrid(frame, MediaMomentKernelTests.Grid(), cancellation.Token));
        Assert.Throws<OperationCanceledException>(() => kernel.BuildSolidGrid(new(0, 0, 0, 1), MediaMomentKernelTests.Grid(), cancellation.Token));
        Assert.Throws<OperationCanceledException>(() => kernel.IntegrateSourceRect(new MediaMomentValues[MediaTrackLayoutV1.Cells], new(0, 0, 1, 1), cancellation.Token));
    }

    /// <summary>Cancellation arriving when the borrow is acquired is checked before pixel scanning.</summary>
    [Fact]
    public void CancellationAfterMemoryAcquisitionRemainsOperationCancelled()
    {
        var kernel = new MediaMomentKernel();
        using var cancellation = new CancellationTokenSource();
        using var memory = new CallbackMemoryManager(cancellation.Cancel, [0, 0, 0, 1]);
        var frame = MediaMomentKernelTests.FrameMemory(1, 1, memory.ExposeMemory());
        Assert.Throws<OperationCanceledException>(() => kernel.BuildGrid(frame, MediaMomentKernelTests.Grid(), cancellation.Token));
    }

    private sealed class CallbackMemoryManager : MemoryManager<float>
    {
        private readonly Action _onAcquire;
        private readonly float[] _pixels;
        internal CallbackMemoryManager(Action onAcquire, float[] pixels) { _onAcquire = onAcquire; _pixels = pixels; }
        internal Memory<float> ExposeMemory() => CreateMemory(_pixels.Length);
        /// <inheritdoc/>
        public override Span<float> GetSpan() { _onAcquire(); return _pixels; }
        /// <inheritdoc/>
        public override MemoryHandle Pin(int elementIndex = 0) => throw new NotSupportedException();
        /// <inheritdoc/>
        public override void Unpin() { }
        protected override void Dispose(bool disposing) { }
    }
}
