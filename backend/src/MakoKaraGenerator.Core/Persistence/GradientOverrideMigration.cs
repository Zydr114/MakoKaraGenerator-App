using System.Text.Json.Nodes;

namespace MakoKaraGenerator.Core.Persistence;

/// <summary>为行和元素覆盖加入可选的完整渐变描述。</summary>
internal sealed class GradientOverrideMigration(int fromVersion) : IProjectSchemaMigration
{
    /// <inheritdoc />
    public int FromVersion => fromVersion;

    /// <inheritdoc />
    public int ToVersion => fromVersion + 1;

    /// <inheritdoc />
    public string Description => "增加行与元素的完整渐变覆盖";

    /// <inheritdoc />
    public void Apply(JsonObject document, MigrationLog log)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(log);
        // 新字段可选。缺失表示继承，旧文件无需改写已有颜色和渐变端点。
        log.Add("样式覆盖可使用完整渐变描述；旧颜色字段保持原有语义。");
    }
}
