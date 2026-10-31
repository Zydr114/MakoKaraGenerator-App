namespace MakoKaraGenerator.Backend.Worker.Tests;

public sealed class GpuPlaybackControlTests
{
    private static GpuPlaybackState State(long seq, long time = 100, string reset = "initial") =>
        new(seq, "playing", time, 1, 30, reset, time, "1234567890123456789");

    [Fact]
    public void PendingStateIsBoundedAndClockCorrectionDoesNotResetTimeline()
    {
        var control = new GpuPlaybackControl();
        Assert.True(control.Offer(State(1)));
        Assert.True(control.TryBegin(out var first));
        Assert.True(control.Offer(State(2)));
        Assert.True(control.Offer(State(3, 90)));
        Assert.False(control.TryBegin(out _));
        Assert.Equal(1, control.Complete(first!));
        Assert.True(control.TryBegin(out var latest));
        Assert.Equal(3, latest!.ControlSeq);
        Assert.Equal(1, control.Complete(latest));
        Assert.True(control.Offer(State(4, 90, "seek-1")));
        Assert.True(control.TryBegin(out var seek));
        Assert.Equal(2, control.Complete(seek!));
    }

    [Fact]
    public void ReplayMismatchAndLateCompletionCannotAdvanceState()
    {
        var control = new GpuPlaybackControl();
        var state = State(1);
        Assert.True(control.Offer(state));
        Assert.False(control.Offer(state));
        Assert.Throws<InvalidOperationException>(() => control.Offer(State(1, 99)));
        Assert.True(control.TryBegin(out _));
        Assert.Throws<InvalidOperationException>(() => control.Complete(State(2)));
        Assert.Equal(1, control.Complete(state));
        control.Close();
        Assert.False(control.TryBegin(out _));
        Assert.Throws<InvalidOperationException>(() => control.Offer(State(2)));
    }

    [Fact]
    public void InvalidSequenceAndNoncanonicalClockAreRejected()
    {
        var control = new GpuPlaybackControl();
        Assert.Throws<ArgumentException>(() => control.Offer(State(9_007_199_254_740_992)));
        Assert.Throws<ArgumentException>(() => control.Offer(State(1) with { SenderMonotonicNs = "01" }));
        Assert.Throws<ArgumentException>(() => control.Offer(State(1) with { Rate = double.NaN }));
    }
}
