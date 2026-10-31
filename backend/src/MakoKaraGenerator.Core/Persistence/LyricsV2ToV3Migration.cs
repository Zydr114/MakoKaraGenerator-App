using System.Text.Json.Nodes;

namespace MakoKaraGenerator.Core.Persistence;

/// <summary>
/// 歌词文件 v2 → v3：行与元素的样式覆盖新增渐变、描边、阴影、不透明度与基础缩放。
/// </summary>
/// <remarks>
/// <para>
/// 歌词文件是<b>独立的一条版本线</b>（ADR-0029）：它的形状变了就自己升一步，
/// 不去借用工程文件的 2 → 3（那一步讲的是音频路径，只属于 <c>project.json</c>）。
/// 因此迁移步骤按文件分表登记：工程文件用 <see cref="ProjectSchemaMigrator.Default"/>，
/// 歌词文件用 <see cref="ProjectSchemaMigrator.LyricsDefault"/>。
/// </para>
/// <para>
/// 与工程文件那步一样<b>不改写数据</b>：缺字段就是“这一层没有覆盖这一项”。
/// 开发期那对实验性渐变字段仍然要翻译（覆盖里也可能出现）。
/// </para>
/// </remarks>
internal sealed class LyricsV2ToV3Migration : IProjectSchemaMigration
{
    /// <inheritdoc/>
    public int FromVersion => 2;

    /// <inheritdoc/>
    public int ToVersion => 3;

    /// <inheritdoc/>
    public string Description => "样式覆盖新增渐变、描边、阴影、不透明度与基础缩放";

    /// <inheritdoc/>
    public void Apply(JsonObject document, MigrationLog log)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(log);

        if (document["lines"] is not JsonArray lines)
        {
            return;
        }

        foreach (JsonNode? node in lines)
        {
            if (node is not JsonObject line)
            {
                continue;
            }

            if (line["styleOverride"] is JsonObject lineOverride)
            {
                LegacyGradientFields.Translate(lineOverride, log);
            }

            if (line["tokens"] is not JsonArray tokens)
            {
                continue;
            }

            foreach (JsonNode? tokenNode in tokens)
            {
                if (tokenNode is JsonObject token && token["styleOverride"] is JsonObject tokenOverride)
                {
                    LegacyGradientFields.Translate(tokenOverride, log);
                }
            }
        }

        log.Add("v2 歌词文件没有描边、阴影、不透明度与缩放覆盖：按「未覆盖」解释，不写入默认值。");
    }
}
