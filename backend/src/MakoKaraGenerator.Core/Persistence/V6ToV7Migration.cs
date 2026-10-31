using System.Text.Json.Nodes;

namespace MakoKaraGenerator.Core.Persistence;

/// <summary>项目文件 v6 → v7：移除独立的当前元素颜色字段。</summary>
internal sealed class V6ToV7Migration : IProjectSchemaMigration
{
    /// <inheritdoc/>
    public int FromVersion => 6;

    /// <inheritdoc/>
    public int ToVersion => 7;

    /// <inheritdoc/>
    public string Description => "移除当前元素颜色与渐变字段";

    /// <inheritdoc/>
    public void Apply(JsonObject document, MigrationLog log)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(log);

        RemoveLegacyFields(document);
        log.Add("v6 样式中的当前元素颜色与渐变字段已移除：当前元素改用未唱/已唱进度显示。");
    }

    internal static void RemoveLegacyFields(JsonObject document)
    {
        document["lyricStyle"]?.AsObject().RemoveLegacyColorFields();

        if (document["lyrics"] is JsonObject lyrics)
        {
            RemoveFromLines(lyrics["lines"] as JsonArray);
        }

        RemoveFromLines(document["lines"] as JsonArray);
    }

    private static void RemoveFromLines(JsonArray? lines)
    {
        if (lines is null)
        {
            return;
        }

        foreach (JsonNode? lineNode in lines)
        {
            if (lineNode is not JsonObject line)
            {
                continue;
            }

            line["styleOverride"]?.AsObject().RemoveLegacyColorFields();

            if (line["tokens"] is JsonArray tokens)
            {
                foreach (JsonNode? tokenNode in tokens)
                {
                    if (tokenNode is JsonObject token)
                    {
                        token["styleOverride"]?.AsObject().RemoveLegacyColorFields();
                    }
                }
            }
        }
    }
}

internal static class LegacyCurrentColorJsonExtensions
{
    public static void RemoveLegacyColorFields(this JsonObject style)
    {
        style.Remove("currentColor");
        style.Remove("currentGradientStartColor");
        style.Remove("currentGradientEndColor");
    }
}
