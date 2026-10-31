using System.Diagnostics.CodeAnalysis;

namespace MakoKaraGenerator.Media;

/// <summary>
/// 播放状态。
/// </summary>
public enum AudioPlaybackState
{
    /// <summary>已停止（位置归零，或尚未加载）。</summary>
    Stopped = 0,

    /// <summary>正在播放。</summary>
    Playing = 1,

    /// <summary>已暂停（位置保留）。</summary>
    Paused = 2,
}

/// <summary>
/// 音频播放适配接口。
/// </summary>
/// <remarks>
/// <para>
/// docs/roadmap.md §8 的 <c>MEDIA-04</c> 要求"音频播放适配接口：UI 不直接依赖具体播放器"，
/// docs/development.md §21.2 要求"播放也应通过统一的媒体适配层抽象"。
/// 因此界面只认这个接口，任何播放器名字都不允许出现在 ViewModel 或控件里。
/// </para>
/// <para>
/// 位置由使用者<b>轮询</b> <see cref="PositionUs" /> 获取，而不是依赖回调：
/// 播放器的位置更新发生在音频线程上，让界面直接吃跨线程事件
/// 很容易变成"偶尔崩一下"的隐蔽问题。轮询是显式且线程安全的。
/// </para>
/// </remarks>
public interface IAudioPlayback : IDisposable
{
    /// <summary>当前状态。</summary>
    AudioPlaybackState State { get; }

    /// <summary>后端是否真的能出声；<see langword="false" /> 表示当前是静音回退实现。</summary>
    bool IsAvailable { get; }

    /// <summary>后端名称，用于诊断显示。</summary>
    string BackendName { get; }

    /// <summary>音频总时长（微秒）；未知时为 0。</summary>
    long DurationUs { get; }

    /// <summary>当前播放位置（微秒）。</summary>
    long PositionUs { get; }

    /// <summary>音量（0~1）。</summary>
    double Volume { get; set; }

    /// <summary>播放自然结束时触发。</summary>
    /// <remarks>可能在非 UI 线程上触发，订阅者需要自行切回 UI 线程。</remarks>
    event EventHandler? PlaybackEnded;

    /// <summary>加载音频文件并准备播放。</summary>
    /// <param name="filePath">音频文件路径。</param>
    /// <param name="durationUs">音频总时长（微秒）；时长由调用方从媒体探测获得，
    /// 播放后端不重复探测一遍文件。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>加载完成的任务。</returns>
    Task LoadAsync(string filePath, long durationUs, CancellationToken cancellationToken = default);

    /// <summary>开始或继续播放。</summary>
    void Play();

    /// <summary>暂停并保留位置。</summary>
    void Pause();

    /// <summary>停止并把位置归零。</summary>
    /// <remarks>
    /// 名字保留 <c>Stop</c>：它与 <see cref="Play" />、<see cref="Pause" /> 是同一组播放器标准用语，
    /// 为了避开某个语言的保留字而改成 <c>StopPlayback</c> 只会让这组 API 变得不一致。
    /// </remarks>
    [SuppressMessage(
        "Naming",
        "CA1716:Identifiers should not match keywords",
        Justification = "Stop/Play/Pause 是音频播放器的标准用语，改名会破坏这组 API 的一致性。")]
    void Stop();

    /// <summary>跳转到指定位置。</summary>
    /// <param name="positionUs">目标位置（微秒）。</param>
    void Seek(long positionUs);
}
