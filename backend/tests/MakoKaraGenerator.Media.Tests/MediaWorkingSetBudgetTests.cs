using System.Collections.Concurrent;
using MakoKaraGenerator.Core.Media;
using Xunit;

namespace MakoKaraGenerator.Media.Tests;

public sealed class MediaWorkingSetBudgetTests
{
    [Fact]
    public void ConstructorEnforcesTheFixedPositiveDecodeCeiling()
    {
        foreach (long limit in new[] { long.MinValue, -1L, 0, MediaAbCapacityV1.MaximumDecodeWorkingBytes + 1, long.MaxValue })
            Assert.Throws<ArgumentOutOfRangeException>(() => new MediaWorkingSetBudget(limit));
        Assert.Equal(1, new MediaWorkingSetBudget(1).LimitBytes);
        Assert.Equal(MediaAbCapacityV1.MaximumDecodeWorkingBytes, new MediaWorkingSetBudget(MediaAbCapacityV1.MaximumDecodeWorkingBytes).LimitBytes);
    }

    [Fact]
    public void ExactCapacitySucceedsPlusOneFailsWithoutChangingAnyCountersAndCanBeReused()
    {
        var budget = new MediaWorkingSetBudget(10);
        using IMediaMemoryReservation first = budget.Reserve(4, MediaWorkingSetPurpose.DecoderNative);
        IMediaMemoryReservation last = budget.Reserve(6, MediaWorkingSetPurpose.CanonicalPixels);
        Assert.Equal(10, budget.LiveBytes);
        MediaWorkingSetSnapshot before = budget.GetSnapshot();
        Failure(MediaAbFailure.CapacityExceeded, () => budget.Reserve(1, MediaWorkingSetPurpose.ReaderCache));
        Failure(MediaAbFailure.CapacityExceeded, () => budget.Reserve(long.MaxValue, MediaWorkingSetPurpose.ReaderCache));
        Same(before, budget.GetSnapshot());
        last.Dispose();
        last.Dispose();
        Assert.Equal(4, budget.LiveBytes);
        using IMediaMemoryReservation replacement = budget.Reserve(6, MediaWorkingSetPurpose.Conversion);
        Assert.Equal(10, budget.LiveBytes);
        Assert.Equal(10, budget.PeakBytes);
        Assert.Equal(6, replacement.Bytes);
    }

    [Fact]
    public void InvalidCountAndUnknownPurposeFailWithoutMutatingTheLedger()
    {
        var budget = new MediaWorkingSetBudget(10);
        MediaWorkingSetSnapshot before = budget.GetSnapshot();
        foreach (long bytes in new[] { long.MinValue, -1L, 0 })
            Assert.Throws<ArgumentOutOfRangeException>(() => budget.Reserve(bytes, MediaWorkingSetPurpose.Metadata));
        foreach (MediaWorkingSetPurpose purpose in new[] { (MediaWorkingSetPurpose)(-1), (MediaWorkingSetPurpose)7, (MediaWorkingSetPurpose)int.MaxValue })
            Assert.Throws<ArgumentOutOfRangeException>(() => budget.Reserve(1, purpose));
        Same(before, budget.GetSnapshot());
        using IMediaMemoryReservation reservation = budget.Reserve(10, MediaWorkingSetPurpose.Metadata);
        Assert.Equal(10, budget.LiveBytes);
    }

    [Fact]
    public void PurposeDiagnosticsAreBoundedConsistentAndSnapshotValuesStayImmutable()
    {
        var budget = new MediaWorkingSetBudget(100);
        var reservations = Enum.GetValues<MediaWorkingSetPurpose>().Select(p => budget.Reserve((long)p + 1, p)).ToArray();
        MediaWorkingSetSnapshot full = budget.GetSnapshot();
        Assert.Equal(7, full.Purposes.Count);
        Assert.Equal(28, full.LiveBytes);
        Assert.Equal(28, full.PeakBytes);
        Assert.Equal(7, full.LiveReservationCount);
        Assert.Equal(7, full.PeakReservationCount);
        for (int i = 0; i < 7; i++)
        {
            MediaWorkingSetPurposeSnapshot p = full.Purposes[i];
            Assert.Equal((MediaWorkingSetPurpose)i, p.Purpose);
            Assert.Equal(i + 1, p.LiveBytes);
            Assert.Equal(i + 1, p.PeakBytes);
            Assert.Equal(1, p.LiveReservationCount);
            Assert.Equal(1, p.PeakReservationCount);
        }
        foreach (IMediaMemoryReservation reservation in reservations) reservation.Dispose();
        MediaWorkingSetSnapshot empty = budget.GetSnapshot();
        Assert.Equal(0, empty.LiveBytes);
        Assert.Equal(0, empty.LiveReservationCount);
        Assert.Equal(28, empty.PeakBytes);
        Assert.All(empty.Purposes, p => { Assert.Equal(0, p.LiveBytes); Assert.Equal(0, p.LiveReservationCount); });
        Assert.Equal(28, full.LiveBytes);
        Assert.All(full.Purposes, p => Assert.Equal(1, p.LiveReservationCount));
    }

    [Fact]
    public async Task ConcurrentCompetitionAdmitsExactlyCapacityAndFailureLeavesReusableSlots()
    {
        const int slots = 8, competitors = 32;
        var budget = new MediaWorkingSetBudget(slots);
        var held = new ConcurrentBag<IMediaMemoryReservation>();
        var refused = new ConcurrentBag<MediaAbFailure>();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task[] attempts = Enumerable.Range(0, competitors).Select(async _ =>
        {
            await start.Task;
            try { held.Add(budget.Reserve(1, MediaWorkingSetPurpose.ReaderCache)); }
            catch (MediaAbException ex) { refused.Add(ex.Failure); }
        }).ToArray();
        start.SetResult();
        await Task.WhenAll(attempts);
        try
        {
            Assert.Equal(slots, held.Count);
            Assert.Equal(competitors - slots, refused.Count);
            Assert.All(refused, f => Assert.Equal(MediaAbFailure.CapacityExceeded, f));
            MediaWorkingSetSnapshot full = budget.GetSnapshot();
            Assert.Equal(slots, full.LiveBytes);
            Assert.Equal(slots, full.LiveReservationCount);
            Assert.Equal(slots, full.PeakBytes);
            Assert.Equal(slots, full.Purposes.Single(p => p.Purpose == MediaWorkingSetPurpose.ReaderCache).LiveBytes);
        }
        finally { foreach (IMediaMemoryReservation reservation in held) reservation.Dispose(); }
        Assert.Equal(0, budget.LiveBytes);
        using IMediaMemoryReservation reused = budget.Reserve(slots, MediaWorkingSetPurpose.Builder);
        Assert.Equal(slots, budget.LiveBytes);
    }

    [Fact]
    public async Task ConcurrentDoubleDisposeReleasesExactlyOnceAndDoesNotEraseAnotherCharge()
    {
        var budget = new MediaWorkingSetBudget(10);
        using IMediaMemoryReservation survivor = budget.Reserve(4, MediaWorkingSetPurpose.Builder);
        IMediaMemoryReservation target = budget.Reserve(6, MediaWorkingSetPurpose.Builder);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task[] disposers = Enumerable.Range(0, 32).Select(async _ => { await start.Task; target.Dispose(); }).ToArray();
        start.SetResult();
        await Task.WhenAll(disposers);
        target.Dispose();
        MediaWorkingSetSnapshot snapshot = budget.GetSnapshot();
        Assert.Equal(4, snapshot.LiveBytes);
        Assert.Equal(1, snapshot.LiveReservationCount);
        Assert.Equal(10, snapshot.PeakBytes);
        Assert.Equal(4, snapshot.Purposes.Single(p => p.Purpose == MediaWorkingSetPurpose.Builder).LiveBytes);
        using IMediaMemoryReservation replacement = budget.Reserve(6, MediaWorkingSetPurpose.CodecValidation);
        Assert.Equal(10, budget.LiveBytes);
        Assert.Equal(6, target.Bytes);
    }

    [Fact]
    public async Task ConcurrentChurnAndSnapshotsMaintainConservationAndAllChargesDrain()
    {
        var budget = new MediaWorkingSetBudget(64);
        Task[] tasks = Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
        {
            for (int i = 0; i < 128; i++)
            {
                using IMediaMemoryReservation charge = budget.Reserve(1 + worker, (MediaWorkingSetPurpose)(worker % 7));
                MediaWorkingSetSnapshot snapshot = budget.GetSnapshot();
                Assert.Equal(snapshot.LiveBytes, snapshot.Purposes.Sum(p => p.LiveBytes));
                Assert.Equal(snapshot.LiveReservationCount, snapshot.Purposes.Sum(p => p.LiveReservationCount));
                Assert.InRange(snapshot.LiveBytes, 1, budget.LimitBytes);
                Assert.InRange(snapshot.LiveReservationCount, 1, 8);
                Assert.InRange(snapshot.LiveBytes, 0, snapshot.PeakBytes);
                Assert.All(snapshot.Purposes, p => Assert.InRange(p.LiveBytes, 0, p.PeakBytes));
            }
        })).ToArray();
        await Task.WhenAll(tasks);
        MediaWorkingSetSnapshot drained = budget.GetSnapshot();
        Assert.Equal(0, drained.LiveBytes);
        Assert.Equal(0, drained.LiveReservationCount);
        Assert.InRange(drained.PeakBytes, 1, 36);
        Assert.All(drained.Purposes, p => { Assert.Equal(0, p.LiveBytes); Assert.Equal(0, p.LiveReservationCount); });
    }

    private static void Failure(MediaAbFailure expected, Action action) => Assert.Equal(expected, Assert.Throws<MediaAbException>(action).Failure);
    private static void Same(MediaWorkingSetSnapshot a, MediaWorkingSetSnapshot b)
    {
        Assert.Equal(a.LiveBytes, b.LiveBytes); Assert.Equal(a.PeakBytes, b.PeakBytes);
        Assert.Equal(a.LiveReservationCount, b.LiveReservationCount); Assert.Equal(a.PeakReservationCount, b.PeakReservationCount);
        Assert.Equal(a.Purposes.ToArray(), b.Purposes.ToArray());
    }
}
