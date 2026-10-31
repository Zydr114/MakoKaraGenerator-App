using System.Text.Json.Nodes;

namespace MakoKaraGenerator.Core.Persistence;

/// <summary>歌词文件 v3 → v4：移除元素覆盖中的当前元素颜色字段。</summary>
internal sealed class LyricsV3ToV4Migration : IProjectSchemaMigration
{
    /// <inheritdoc/>
    public int FromVersion => 3;

    /// <inheritdoc/>
    public int ToVersion => 4;

    /// <inheritdoc/>
    public string Description => "移除当前元素颜色与渐变字段";

    /// <inheritdoc/>
    public void Apply(JsonObject document, MigrationLog log)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(log);

        V6ToV7Migration.RemoveLegacyFields(document);
        log.Add("v3 歌词覆盖中的当前元素颜色与渐变字段已移除：当前元素改用未唱/已唱进度显示。");
    }
}
