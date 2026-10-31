using System.Collections.Concurrent;
using System.Reflection;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using SoundFlow.Components;

namespace MakoKaraGenerator.Media.Tests;

// Executes the production event bridge without allocating an audio engine/device.
// These objects are identity tokens, not evidence of audible native playback.
public sealed class PlaybackCompletionBridgeTests
{
    [Fact]
    public async Task 原生后台结束只入队旧解码段不能停止当前解码段()
    {
        var backend = Token<SoundFlowAudioPlayback>();
        var old = Token<SoundPlayer>(); var current = Token<SoundPlayer>();
        Set(backend, "_endedPlayers", new ConcurrentQueue<SoundPlayer>());
        Set(backend, "_durationUs", 2_000_000L); Set(backend, "_sessionStartUs", 100_000L);
        Set(backend, "_player", current); Set(backend, "_state", AudioPlaybackState.Playing);
        int notifications = 0; backend.PlaybackEnded += (_, _) => notifications++;
        await Task.Run(() => End(backend, old));
        Assert.Equal(AudioPlaybackState.Playing, backend.State);
        Assert.Equal(0, notifications);
        await Task.Run(() => End(backend, current));
        Assert.Equal(AudioPlaybackState.Playing, Get(backend, "_state"));
        // A later stale callback must not replace/drop the accepted current completion.
        await Task.Run(() => End(backend, old));
        Assert.Equal(AudioPlaybackState.Stopped, backend.State);
        Assert.Equal(2_000_000, backend.PositionUs); Assert.Equal(1, notifications);
    }

    [Fact]
    public void 解码段替换后排队通知不能修改新起点或状态()
    {
        var backend = Token<SoundFlowAudioPlayback>();
        var old = Token<SoundPlayer>(); var current = Token<SoundPlayer>();
        Set(backend, "_endedPlayers", new ConcurrentQueue<SoundPlayer>());
        Set(backend, "_durationUs", 2_000_000L); Set(backend, "_player", old);
        Set(backend, "_state", AudioPlaybackState.Playing);
        End(backend, old);
        Set(backend, "_player", current); Set(backend, "_sessionStartUs", 500_000L);
        Assert.Equal(AudioPlaybackState.Playing, backend.State);
        Assert.Equal(500_000L, Get(backend, "_sessionStartUs"));
    }

    [SuppressMessage("Usage", "CA1816:Dispose methods should call SuppressFinalize",
        Justification = "Uninitialized identity tokens own no native resources; suppress their finalizers without invoking device cleanup.")]
    private static T Token<T>() where T : class
    { var value = (T)RuntimeHelpers.GetUninitializedObject(typeof(T)); GC.SuppressFinalize(value); return value; }
    private static void End(SoundFlowAudioPlayback backend, SoundPlayer sender) => typeof(SoundFlowAudioPlayback)
        .GetMethod("OnPlaybackEnded", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(backend, [sender, EventArgs.Empty]);
    private static void Set(object target, string field, object value) => target.GetType()
        .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
    private static object? Get(object target, string field) => target.GetType()
        .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target);
}
