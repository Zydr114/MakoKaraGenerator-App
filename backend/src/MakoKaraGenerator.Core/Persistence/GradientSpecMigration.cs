using System.Text.Json.Nodes;

namespace MakoKaraGenerator.Core.Persistence;

/// <summary>为样式增加可选完整渐变描述的工程迁移（schema v10 → v11）。</summary>
internal sealed class GradientSpecMigration : IProjectSchemaMigration
{
    /// <inheritdoc />
    public int FromVersion => 10;

    /// <inheritdoc />
    public int ToVersion => 11;

    /// <inheritdoc />
    public string Description => "增加可选的完整渐变描述";

    /// <inheritdoc />
    public void Apply(JsonObject document, MigrationLog log)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(log);

        // 旧文件只含起止色字段，读取时会继续按兼容路径渲染；没有需要改写的 JSON。
        log.Add("样式可使用完整渐变描述；旧起止色字段保持原有语义。");
    }
}
