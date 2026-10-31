using System.Collections.Concurrent;
using MakoKaraGenerator.Rendering.Skia;
using SkiaSharp;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed class ImageSurfaceBudgetBorrowTests
{
    private const long ImageBytes = 8 * 8 * 4;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BorrowReportsExactAdditionalBytesWhenSurfaceAndByteLimitsAreFull(bool alreadyKnown)
    {
        using var bitmap = new SKBitmap(8, 8);
        using var image = SKImage.FromBitmap(bitmap);
        int storedImages = alreadyKnown ? 2 : 1;
        using var budget = new ImageSurfaceBudgetSession(new() { MaximumSurfaces = 1, MaximumBytes = ImageBytes * storedImages });
        using var owner = budget.BeginFrame();
        using var frame = budget.BeginFrame();
        using var existing = alreadyKnown ? budget.Borrow(owner, image.Handle, ImageBytes) : null;
        using var occupied = frame.Reserve(1, ImageBytes, "Occupied");

        var error = Assert.Throws<ImageSurfaceBudgetException>(() => budget.Borrow(frame, image.Handle, ImageBytes));

        Assert.Equal("BorrowGraphInput", error.Operation);
        Assert.Equal("ImageSurfaceBudgetExceeded", error.Code);
        Assert.Equal(1, error.CurrentSurfaces);
        Assert.Equal(1, error.AdditionalSurfaces);
        Assert.Equal(1, error.MaximumSurfaces);
        Assert.Equal(ImageBytes * storedImages, error.CurrentBytes);
        Assert.Equal(alreadyKnown ? 0 : ImageBytes, error.AdditionalBytes);
        Assert.Equal(ImageBytes * storedImages, error.MaximumBytes);
        Assert.Equal(storedImages, budget.Usage.ActiveSurfaces);
        Assert.Equal(ImageBytes * storedImages, budget.Usage.EstimatedBytes);
        occupied.Dispose(); existing?.Dispose();
        Assert.Equal(0, budget.Usage.ActiveSurfaces);
        Assert.Equal(0, budget.Usage.EstimatedBytes);
        Assert.NotEqual(nint.Zero, image.Handle);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentFirstBorrowsShareOneAllocationWithinAndAcrossFrames(bool separateFrames)
    {
        const int callers = 8, rounds = 32;
        using var bitmap = new SKBitmap(8, 8);
        using var image = SKImage.FromBitmap(bitmap);
        nint handle = image.Handle;
        using var budget = new ImageSurfaceBudgetSession(new() { MaximumSurfaces = 1, MaximumBytes = ImageBytes });
        var frames = Enumerable.Range(0, separateFrames ? callers : 1).Select(_ => budget.BeginFrame()).ToArray();
        using var phases = new Barrier(callers);
        ConcurrentQueue<Exception> failures = [];
        var reservations = new ImageSurfaceReservation?[callers];
        var heldUsage = new ImageSurfaceUsage[rounds];
        var releasedUsage = new ImageSurfaceUsage[rounds];
        var sharedAllocations = new bool[rounds];
        var frameClaims = new bool[rounds];
        try
        {
            var tasks = Enumerable.Range(0, callers).Select(caller => Task.Factory.StartNew(() =>
            {
                for (int round = 0; round < rounds; round++)
                {
                    phases.SignalAndWait();
                    try { reservations[caller] = budget.Borrow(frames[separateFrames ? caller : 0], handle, ImageBytes); }
                    catch (Exception error) { failures.Enqueue(error); }
                    phases.SignalAndWait();
                    if (caller == 0)
                    {
                        heldUsage[round] = budget.Usage;
                        sharedAllocations[round] = reservations[0] is { } first
                            && reservations.All(item => item is not null && ReferenceEquals(first.Allocation, item.Allocation));
                        frameClaims[round] = frames.All(frame => frame.Usage.ActiveSurfaces == 1 && frame.Usage.EstimatedBytes == ImageBytes);
                    }
                    phases.SignalAndWait();
                    reservations[caller]?.Dispose(); reservations[caller] = null;
                    phases.SignalAndWait();
                    if (caller == 0) releasedUsage[round] = budget.Usage;
                }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
            await Task.WhenAll(tasks);

            Assert.Empty(failures);
            Assert.All(sharedAllocations, value => Assert.True(value));
            Assert.All(frameClaims, value => Assert.True(value));
            Assert.All(heldUsage, usage => { Assert.Equal(1, usage.ActiveSurfaces); Assert.Equal(ImageBytes, usage.EstimatedBytes); });
            Assert.All(releasedUsage, usage => { Assert.Equal(0, usage.ActiveSurfaces); Assert.Equal(0, usage.EstimatedBytes); });
            Assert.Equal(1, budget.Usage.PeakSurfaces);
            Assert.Equal(ImageBytes, budget.Usage.PeakBytes);
            Assert.NotEqual(nint.Zero, image.Handle);
        }
        finally
        {
            foreach (var reservation in reservations) reservation?.Dispose();
            foreach (var frame in frames) frame.Dispose();
        }
    }
}
