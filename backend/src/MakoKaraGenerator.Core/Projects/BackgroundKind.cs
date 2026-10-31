namespace MakoKaraGenerator.Core.Projects;

/// <summary>
/// 背景源的种类。
/// </summary>
/// <remarks>
/// <para>
/// <b>视频背景是预留项</b>：docs/development.md 把"背景视频完整支持"列在 P1（line 176），
/// 首期只做纯色与图片。接进来时要动的地方已经在 <see cref="BackgroundSource" />
/// 与渲染侧的 <c>IBackgroundFrameSource</c> 上留好了，见 docs/decisions.md ADR-0036。
/// </para>
/// <para>
/// 枚举成员<b>不预占</b>未实现的档位：写一个 <c>Video</c> 成员会让"看起来支持但没实现"
/// 的取值在类型层面合法，反而容易漏掉处理分支。旧程序读到不认识的写法时按"没有背景"处理并报告。
/// </para>
/// </remarks>
public enum BackgroundKind
{
    /// <summary>只用背景色（<see cref="Composition.BackgroundColor" />），不额外铺一层画面。</summary>
    Color = 0,

    /// <summary>单张图片，画在背景色<b>之上</b>。</summary>
    Image = 1,

    /// <summary>
    /// 一段视频，逐帧画在背景色<b>之上</b>。
    /// </summary>
    /// <remarks>
    /// 渲染器每帧只问"这一刻该显示哪一帧"，时间映射在 <see cref="BackgroundPlaybackMode" />；
    /// 比例对不上时的适配方式与图片共用同一套 <see cref="BackgroundFit" />。
    /// </remarks>
    Video = 2,
}
