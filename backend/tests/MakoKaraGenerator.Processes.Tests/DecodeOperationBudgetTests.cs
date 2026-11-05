using System;
using System.Threading.Tasks;
using MakoKaraGenerator.Processes.MediaEnvelope;
using Xunit;

namespace MakoKaraGenerator.Processes.Tests;

/// <summary>Pure arithmetic and lease ownership tests; no native backend is opened.</summary>
public sealed class DecodeOperationBudgetTests
{
    [Fact]
    /// <summary>Checks FrozenSumRejectsTwoIndependentFullNativeCeilings.</summary>
    public void FrozenSumRejectsTwoIndependentFullNativeCeilings()
    {
        var source = new Source();
        EnvelopeException error = Assert.Throws<EnvelopeException>(() => new DecodeOperationBudget(
            268435456, 1048576, [268435456, 268435456], 2, source));
        Assert.Equal(EnvelopeFailure.CapacityExceeded, error.Failure);
        Assert.Equal(0, source.Releases);
    }

    [Fact]
    /// <summary>Checks OverflowAndProducerCountAreRejectedBeforeOwnershipTransfer.</summary>
    public void OverflowAndProducerCountAreRejectedBeforeOwnershipTransfer()
    {
        var source = new Source();
        Assert.Throws<EnvelopeException>(() => new DecodeOperationBudget(long.MaxValue, long.MaxValue, [4096], 2, source));
        Assert.Throws<EnvelopeException>(() => new DecodeOperationBudget(268435456, 1048576, [4096, 4096, 4096], 2, source));
        Assert.Equal(0, source.Releases);
    }

    [Fact]
    /// <summary>Checks ActualCapacityAndBorrowBlockSourceRelease.</summary>
    public async Task ActualCapacityAndBorrowBlockSourceRelease()
    {
        var source = new Source();
        var budget = new DecodeOperationBudget(268435456, 1048576, [4096], 2, source);
        using (DecodeOperationBudget.ManagedLease reservation = budget.ReserveManaged(1048576))
        {
            Assert.Equal(1048576, budget.LiveManagedBytes);
            Assert.Throws<EnvelopeException>(() => budget.ReserveManaged(1));
            await Assert.ThrowsAsync<EnvelopeException>(() => budget.ReleaseSourceAsync().AsTask());
        }
        using (budget.BeginBorrow())
        {
            await Assert.ThrowsAsync<EnvelopeException>(() => budget.ReleaseSourceAsync().AsTask());
            Assert.Equal(0, source.Releases);
        }
        await budget.ReleaseSourceAsync();
        await budget.ReleaseSourceAsync();
        Assert.Equal(1, source.Releases);
    }

    [Fact]
    /// <summary>Checks ClaimedNativePartitionCannotBeReusedOrReleasedWithoutDrain.</summary>
    public async Task ClaimedNativePartitionCannotBeReusedOrReleasedWithoutDrain()
    {
        var source = new Source();
        var budget = new DecodeOperationBudget(268435456, 1048576, [4096], 2, source);
        _ = budget.BeginNative(0);
        Assert.Throws<EnvelopeException>(() => budget.BeginNative(0));
        await Assert.ThrowsAsync<EnvelopeException>(() => budget.ReleaseSourceAsync().AsTask());
        Assert.Equal(0, source.Releases);
    }

    /// <summary>Source cleanup errors retain their exact host-private cause without leaking it in public exception text.</summary>
    [Fact]
    public async Task SourceReleaseFailureKeepsPrivateCauseAndSafeDrainCode()
    {
        var expected = new System.IO.IOException("private source /private/path");
        var budget = new DecodeOperationBudget(268435456, 1048576, [4096], 2, new FailedSource(expected));
        EnvelopeException failure = await Assert.ThrowsAsync<EnvelopeException>(() => budget.ReleaseSourceAsync().AsTask());
        Assert.Equal(EnvelopeFailure.DrainFailed, failure.Failure);
        Assert.Equal("source-lease-release-failed", failure.SafeCode);
        Assert.Null(failure.InnerException);
        Assert.DoesNotContain("private", failure.ToString(), StringComparison.Ordinal);
        var field = typeof(EnvelopeException).GetField("_retainedCause",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(field);
        Assert.Same(expected, field.GetValue(failure));
    }

    private sealed class FailedSource(Exception failure) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.FromException(failure);
    }

    private sealed class Source : IAsyncDisposable
    {
        internal int Releases;
        public ValueTask DisposeAsync() { Releases++; return ValueTask.CompletedTask; }
    }
}
