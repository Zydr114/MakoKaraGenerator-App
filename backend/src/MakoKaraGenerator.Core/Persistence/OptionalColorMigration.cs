using System.Text.Json.Nodes;

namespace MakoKaraGenerator.Core.Persistence;

// 旧覆盖中的 null 表示继承；新文件用属性缺失表达继承，显式 null 表达关闭。
internal sealed class OptionalColorMigration(int fromVersion) : IProjectSchemaMigration
{
    public int FromVersion => fromVersion;
    public int ToVersion => fromVersion + 1;
    public string Description => "区分可选颜色的继承与明确关闭";

    public void Apply(JsonObject document, MigrationLog log)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(log);
        Visit(document);
        log.Add("旧覆盖中的空颜色继续继承上层；新覆盖支持明确关闭效果。");
    }

    private static void Visit(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            if (obj["styleOverride"] is JsonObject overlay)
            {
                foreach (string name in new[] { "unsungGradientStartColor", "unsungGradientEndColor",
                    "sungGradientStartColor", "sungGradientEndColor", "strokeColor", "shadowColor" })
                {
                    if (overlay.ContainsKey(name) && overlay[name] is null) overlay.Remove(name);
                }
            }
            foreach (var child in obj) Visit(child.Value);
        }
        else if (node is JsonArray array)
        {
            foreach (JsonNode? child in array) Visit(child);
        }
    }
}
