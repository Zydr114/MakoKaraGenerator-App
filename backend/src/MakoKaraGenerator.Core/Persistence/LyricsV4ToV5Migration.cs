using System.Text.Json.Nodes;

namespace MakoKaraGenerator.Core.Persistence;

/// <summary>
/// 歌词文件 v4 → v5：行新增"进入动画覆盖"。
/// </summary>
/// <remarks>
/// <b>不改写数据</b>：旧文件里每一行都没有这个字段，含义是"跟随工程默认"。
/// 旧工程的工程级默认也不存在（同样按"不做动画"解释），因此升级后画面不变。
/// </remarks>
internal sealed class LyricsV4ToV5Migration : IProjectSchemaMigration
{
    /// <inheritdoc/>
    public int FromVersion => 4;

    /// <inheritdoc/>
    public int ToVersion => 5;

    /// <inheritdoc/>
    public string Description => "歌词行新增进入动画覆盖";

    /// <inheritdoc/>
    public void Apply(JsonObject document, MigrationLog log)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(log);

        log.Add("v4 歌词行没有进入动画字段：按「跟随工程默认」解释，不写入默认值。");
    }
}
