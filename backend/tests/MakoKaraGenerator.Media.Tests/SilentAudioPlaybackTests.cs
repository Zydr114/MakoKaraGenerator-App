using MakoKaraGenerator.Media;

namespace MakoKaraGenerator.Media.Tests;

/// <summary>
/// <see cref="SilentAudioPlayback" /> 的测试：位置推进必须可预测。
/// </summary>
/// <remarks>
/// 用可控的时间源而不是 <c>Thread.Sleep</c>：播放位置这类逻辑一旦靠"等一会儿"来测，
/// 就会变成偶发失败的来源。
/// </remarks>
public sealed class SilentAudioPlaybackTests
{
    [Fact]
    public void 未加载时是停止状态()
    {
        using SilentAudioPlayback playback = new(new TestTimeProvider());

        Assert.Equal(AudioPlaybackState.Stopped, playback.State);
        Assert.False(playback.IsAvailable);
        Assert.Equal(0L, playback.PositionUs);
        Assert.Equal(0L, playback.DurationUs);
    }

    [Fact]
    public async Task 播放时位置随时间推进()
    {
        TestTimeProvider clock = new();
        using SilentAudioPlayback playback = new(clock);

        await playback.LoadAsync("song.wav", 10_000_000L);
        playback.Play();

        Assert.Equal(AudioPlaybackState.Playing, playback.State);
        Assert.Equal(0L, playback.PositionUs);

        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(2_000_000L, playback.PositionUs);

        clock.Advance(TimeSpan.FromMilliseconds(250));
        Assert.Equal(2_250_000L, playback.PositionUs);
    }

    [Fact]
    public async Task 暂停保留位置且不再推进()
    {
        TestTimeProvider clock = new();
        using SilentAudioPlayback playback = new(clock);

        await playback.LoadAsync("song.wav", 10_000_000L);
        playback.Play();
        clock.Advance(TimeSpan.FromSeconds(3));
        playback.Pause();

        Assert.Equal(AudioPlaybackState.Paused, playback.State);
        Assert.Equal(3_000_000L, playback.PositionUs);

        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(3_000_000L, playback.PositionUs);
    }

    [Fact]
    public async Task 停止把位置归零()
    {
        TestTimeProvider clock = new();
        using SilentAudioPlayback playback = new(clock);

        await playback.LoadAsync("song.wav", 10_000_000L);
        playback.Play();
        clock.Advance(TimeSpan.FromSeconds(4));
        playback.Stop();

        Assert.Equal(AudioPlaybackState.Stopped, playback.State);
        Assert.Equal(0L, playback.PositionUs);
    }

    [Fact]
    public async Task 跳转被钳制在合法范围()
    {
        using SilentAudioPlayback playback = new(new TestTimeProvider());

        await playback.LoadAsync("song.wav", 5_000_000L);

        playback.Seek(-1_000L);
        Assert.Equal(0L, playback.PositionUs);

        playback.Seek(99_000_000L);
        Assert.Equal(5_000_000L, playback.PositionUs);
    }

    [Fact]
    public async Task 播放中跳转后从新位置继续推进()
    {
        TestTimeProvider clock = new();
        using SilentAudioPlayback playback = new(clock);

        await playback.LoadAsync("song.wav", 20_000_000L);
        playback.Play();
        clock.Advance(TimeSpan.FromSeconds(1));

        playback.Seek(10_000_000L);
        Assert.Equal(10_000_000L, playback.PositionUs);

        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(12_000_000L, playback.PositionUs);
        Assert.Equal(AudioPlaybackState.Playing, playback.State);
    }

    [Fact]
    public async Task 到达末尾时报告播放结束()
    {
        TestTimeProvider clock = new();
        using SilentAudioPlayback playback = new(clock);
        int ended = 0;

        playback.PlaybackEnded += (_, _) => ended++;

        await playback.LoadAsync("song.wav", 2_000_000L);
        playback.Play();
        clock.Advance(TimeSpan.FromSeconds(3));

        playback.Tick();

        Assert.Equal(1, ended);
        Assert.Equal(AudioPlaybackState.Stopped, playback.State);
        Assert.Equal(2_000_000L, playback.PositionUs);

        // 再 Tick 不应重复报告
        playback.Tick();
        Assert.Equal(1, ended);
    }

    [Fact]
    public async Task 播到末尾后再按播放会从头开始()
    {
        TestTimeProvider clock = new();
        using SilentAudioPlayback playback = new(clock);

        await playback.LoadAsync("song.wav", 2_000_000L);
        playback.Play();
        clock.Advance(TimeSpan.FromSeconds(5));
        playback.Tick();

        playback.Play();

        Assert.Equal(AudioPlaybackState.Playing, playback.State);
        Assert.Equal(0L, playback.PositionUs);
    }

    [Fact]
    public async Task 音量被钳制到零到一()
    {
        using SilentAudioPlayback playback = new(new TestTimeProvider());

        await playback.LoadAsync("song.wav", 0L);

        playback.Volume = 2d;
        Assert.Equal(1d, playback.Volume);

        playback.Volume = -1d;
        Assert.Equal(0d, playback.Volume);
    }

    [Fact]
    public async Task 未加载文件时播放是空操作()
    {
        using SilentAudioPlayback playback = new(new TestTimeProvider());

        playback.Play();

        Assert.Equal(AudioPlaybackState.Stopped, playback.State);
    }

    [Fact]
    public async Task 释放后调用会抛异常()
    {
        SilentAudioPlayback playback = new(new TestTimeProvider());

        await playback.LoadAsync("song.wav", 0L);
        playback.Dispose();

        Assert.Throws<ObjectDisposedException>(() => playback.Play());
        Assert.Throws<ObjectDisposedException>(() => playback.Seek(0L));
    }

    /// <summary>可控的时间源。</summary>
    private sealed class TestTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now += delta;
    }
}
