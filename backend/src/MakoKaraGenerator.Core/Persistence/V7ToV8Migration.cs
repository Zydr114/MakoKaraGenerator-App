using System.Text.Json.Nodes;

namespace MakoKaraGenerator.Core.Persistence;

/// <summary>
/// v7 → v8：背景源支持视频，并新增"视频短于音频时的时间策略"。
/// </summary>
/// <remarks>
/// <para>
/// 与前面几步同样<b>不改写数据</b>：旧文件里没有 <c>playback</c> 字段，缺省就是"循环"，
/// 而那正是文档要求的默认策略；旧文件也不可能写着视频。升级后画面一个像素都不会变。
/// </para>
/// <para>
/// 步骤必须存在：注册表要把 v7 认下来，并给用户一条"你的文件被按 v8 解释了"的日志。
/// 旧值没有需要翻译的字段，所以只写默认值到内存模型，不动 JSON。
/// </para>
/// </remarks>
internal sealed class V7ToV8Migration : IProjectSchemaMigration
{
    /// <inheritdoc/>
    public int FromVersion => 7;

    /// <inheritdoc/>
    public int ToVersion => 8;

    /// <inheritdoc/>
    public string Description => "背景源支持视频与播放方式";

    /// <inheritdoc/>
    public void Apply(JsonObject document, MigrationLog log)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(log);

        log.Add("v7 文件没有背景播放方式字段：按「循环」解释，不写入默认值。");
    }
}
