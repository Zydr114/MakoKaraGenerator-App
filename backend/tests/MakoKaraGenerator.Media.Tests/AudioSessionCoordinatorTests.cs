using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Media.Tests;

public sealed class AudioSessionCoordinatorTests
{
    [Fact]
    public async Task 相同路径的新媒体修订重载显示名和普通编辑不重载()
    {
        var players = new List<Player>();
        using var session = Create(players);
        Project project = Audio("A.wav") with { AudioMedia = new(new string('a', 64), 10, "first.wav") };
        await session.SelectProject(project, newSession: true);
        long generation = session.Generation;
        int changes = 0;
        session.Changed += (_, _) => changes++;
        await session.SelectProject(project with { AudioMedia = new(new string('a', 64), 10, "renamed.wav") });
        Assert.Single(players);
        Assert.Equal(generation, session.Generation);
        Assert.Equal("renamed.wav", session.DisplayName);
        Assert.Equal(1, changes);
        await session.SelectProject(project with { AudioMedia = new(new string('b', 64), 11, "second.wav") });
        Assert.Equal(2, players.Count);
        Assert.Equal(1, players[0].DisposeCount);
        Assert.Equal(new string('b', 64), session.Target!.RevisionHash);
        Assert.True(session.Generation > generation);
    }

    [Fact]
    public async Task 导入领域提交后的取消仍接入正确候选并如实成功()
    {
        var players = new List<Player>();
        using var session = Create(players);
        await session.SelectProject(Audio("A.wav"), newSession: true);
        using var cancellation = new CancellationTokenSource();
        bool result = await session.ImportAsync("B.wav", () =>
        {
            cancellation.Cancel();
            return Audio("B.wav");
        }, cancellation.Token);
        Assert.True(result);
        Assert.Equal(AudioSessionState.Ready, session.State);
        Assert.Same(players[1], session.Playback);
        Assert.Equal("B.wav", session.Playback!.BackendName);
        Assert.Equal(1, players[0].DisposeCount);
        Assert.Equal(0, players[1].DisposeCount);
    }

    [Fact]
    public async Task 导入被领域拒绝时保留原播放并释放候选()
    {
        var players = new List<Player>();
        using var session = Create(players);
        await session.SelectProject(Audio("A.wav"), newSession: true);
        session.Playback!.Play();
        var waveform = session.Waveform;
        Assert.False(await session.ImportAsync("B.wav", () => null));
        Assert.Same(players[0], session.Playback);
        Assert.Same(waveform, session.Waveform);
        Assert.Equal(AudioPlaybackState.Playing, session.Playback!.State);
        Assert.Equal(0, players[0].DisposeCount);
        Assert.Equal(1, players[1].DisposeCount);
        Assert.Equal(AudioSessionState.Ready, session.State);
    }

    [Fact]
    public async Task 取消恢复等待后忽略取消的准备仍释放且可以重试()
    {
        var players = new List<Player>();
        var gate = new TaskCompletionSource();
        using var session = new AudioSessionCoordinator((_, _) => Task.FromResult<LoadedWaveform?>(Sample()), _ =>
        {
            var player = new Player(players.Count == 0 ? gate.Task : null);
            players.Add(player);
            return new(player, null);
        });
        Project project = Audio("A.wav");
        await session.SelectProject(project, newSession: true, restore: false);
        using var cancellation = new CancellationTokenSource();
        var pending = session.RestoreAsync(project, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        var cleanup = session.RestoreAsync(project);
        gate.SetResult();
        await cleanup;
        Assert.Equal(1, players[0].DisposeCount);
        Assert.Null(session.Playback);
        Assert.Equal(AudioSessionState.Unavailable, session.State);
        await session.RetryAsync();
        Assert.Same(players[1], session.Playback);
        Assert.Equal(AudioSessionState.Ready, session.State);
    }

    [Fact]
    public async Task 旧解码失败不能改写新目标会话状态()
    {
        var players = new List<Player>();
        var old = new TaskCompletionSource<LoadedWaveform?>();
        using var session = Create(players, (path, _) => path == "A.wav" ? old.Task : Task.FromResult<LoadedWaveform?>(Sample()));
        var pending = session.SelectProject(Audio("A.wav"), newSession: true);
        await session.SelectProject(Audio("B.wav"), newSession: true);
        old.SetException(new IOException("late failure"));
        await pending;
        Assert.Null(session.Failure);
        Assert.Equal(AudioSessionState.Ready, session.State);
        Assert.Equal("B.wav", session.Playback!.BackendName);
        Assert.Single(players);
    }

    [Fact]
    public async Task 旧播放器停止释放异常不反向改回旧目标且不会重复释放()
    {
        var players = new List<Player>();
        using var session = Create(players);
        await session.SelectProject(Audio("A.wav"), newSession: true);
        players[0].ThrowOnCleanup = true;
        await session.SelectProject(Audio("B.wav"));
        Assert.Equal(AudioSessionState.Ready, session.State);
        Assert.Equal("B.wav", session.Playback!.BackendName);
        Assert.IsType<AggregateException>(session.CleanupFailure);
        Assert.Equal(1, players[0].DisposeCount);
        session.Dispose();
        session.Dispose();
        Assert.All(players, player => Assert.Equal(1, player.DisposeCount));
    }

    [Fact]
    public async Task 导入提交期间换工程不能接入旧候选()
    {
        var players = new List<Player>();
        using var session = Create(players);
        await session.SelectProject(Audio("A.wav"), newSession: true);
        bool result = await session.ImportAsync("B.wav", () =>
        {
            _ = session.SelectProject(Audio("C.wav"), newSession: true);
            return Audio("B.wav");
        });
        Assert.False(result);
        Assert.Equal("C.wav", session.Playback!.BackendName);
        Assert.Equal(1, players[1].DisposeCount);
        Assert.Equal(0, players[2].DisposeCount);
    }

    [Fact]
    public async Task 拒绝候选的释放异常不遗留已释放请求且可继续导入()
    {
        var players = new List<Player>();
        using var session = Create(players);
        await session.SelectProject(Audio("A.wav"), newSession: true);
        Assert.False(await session.ImportAsync("B.wav", () =>
        {
            players.Last().ThrowOnCleanup = true;
            return null;
        }));
        Assert.IsType<IOException>(session.CleanupFailure);
        Assert.Same(players[0], session.Playback);
        Assert.Equal(AudioSessionState.Ready, session.State);
        Assert.Equal(1, players[1].DisposeCount);
        Assert.True(await session.ImportAsync("C.wav", () => Audio("C.wav")));
        Assert.Equal("C.wav", session.Playback!.BackendName);
        Assert.Equal(1, players[0].DisposeCount);
        Assert.Equal(1, players[1].DisposeCount);
    }

    private static Project Audio(string path) => ProjectMediaEditor.SetAudioPath(Project.Create("音频会话"), path);
    private static LoadedWaveform Sample()
    {
        var builder = new WaveformPeakBuilder(); builder.Append(new float[8000]);
        return new(new AudioStreamInfo(1_000_000, 8000, 1, "pcm", "wav"), builder.Build(8000));
    }
    private static AudioSessionCoordinator Create(List<Player> players,
        Func<string, CancellationToken, Task<LoadedWaveform?>>? decode = null) => new(
        decode ?? ((_, _) => Task.FromResult<LoadedWaveform?>(Sample())), _ =>
        {
            var player = new Player(); players.Add(player); return new(player, null);
        });

    private sealed class Player(Task? preparation = null) : IAudioPlayback
    {
        private string _source = "";
        public int DisposeCount { get; private set; }
        public bool ThrowOnCleanup { get; set; }
        public AudioPlaybackState State { get; private set; }
        public bool IsAvailable => true;
        public string BackendName => _source;
        public long DurationUs { get; private set; }
        public long PositionUs { get; private set; }
        public double Volume { get; set; }
        public event EventHandler? PlaybackEnded { add { } remove { } }
        public async Task LoadAsync(string path, long durationUs, CancellationToken cancellationToken = default)
        { _source = path; DurationUs = durationUs; if (preparation is not null) await preparation; }
        public void Play() => State = AudioPlaybackState.Playing;
        public void Pause() => State = AudioPlaybackState.Paused;
        public void Stop() { State = AudioPlaybackState.Stopped; PositionUs = 0; if (ThrowOnCleanup) throw new InvalidOperationException("stop"); }
        public void Seek(long positionUs) => PositionUs = positionUs;
        public void Dispose() { DisposeCount++; if (ThrowOnCleanup) throw new IOException("dispose"); }
    }
}
