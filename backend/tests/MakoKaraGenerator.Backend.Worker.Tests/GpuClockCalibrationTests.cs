using MakoKaraGenerator.Backend.Contracts;

namespace MakoKaraGenerator.Backend.Worker.Tests;

public sealed class GpuClockCalibrationTests
{
    [Fact]
    public void FourTimestampsExcludeSenderProcessingAndMapClockDomains()
    {
        var clock = new GpuClockCalibration();
        clock.Begin(1_000_000_000);
        clock.Complete(new("clock-pong", "1000000000", "600000000", "610000000"), 1_030_000_000);
        Assert.True(clock.Calibrated);
        Assert.Equal(410_000_000, clock.WorkerMinusSenderNs);
        Assert.Equal(10_000_000, clock.UncertaintyNs);
        var playing = new GpuPlaybackState(1, "playing", 100, 2, 30, "initial", 100, "620000000");
        Assert.Equal(40_100, clock.MediaTime(playing, 1_050_000_000));
        Assert.Equal(100, clock.MediaTime(playing with { Mode = "paused" }, 1_050_000_000));
        Assert.Throws<WorkerProtocolException>(() => clock.Complete(new("clock-pong", "1000000000", "600000000", "610000000"), 1_030_000_000));
    }
    [Theory]
    [InlineData("01", "1", "1", 2)]
    [InlineData("1", "3", "2", 4)]
    [InlineData("1", "2", "10", 4)]
    [InlineData("1", "1", "1", 1_000_000_002)]
    public void MalformedOrImpossibleClockCannotCalibrate(string echo, string receive, string send, long now)
    {
        var clock = new GpuClockCalibration(); clock.Begin(1);
        Assert.Throws<WorkerProtocolException>(() => clock.Complete(new("clock-pong", echo, receive, send), now));
        Assert.False(clock.Calibrated);
    }
    [Fact]
    public void ExcessiveUncertaintyDisablesPlayingWithoutForgingCalibration()
    {
        var clock = new GpuClockCalibration(); clock.Begin(1);
        clock.Complete(new("clock-pong", "1", "1", "1"), 100_000_001);
        Assert.False(clock.Calibrated);
        Assert.Throws<WorkerProtocolException>(() => clock.MediaTime(new(1,"playing",0,1,30,"initial",0,"1"), 100_000_002));
    }
}
