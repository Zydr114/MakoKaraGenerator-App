using System.Text.Json.Nodes;

namespace MakoKaraGenerator.Core.Persistence;

/// <summary>为样式增加描边与方向阴影值对象的工程迁移（schema v11 → v12）。</summary>
internal sealed class StrokeShadowSpecMigration : IProjectSchemaMigration
{
    /// <inheritdoc />
    public int FromVersion => 11;

    /// <inheritdoc />
    public int ToVersion => 12;

    /// <inheritdoc />
    public string Description => "增加描边与方向阴影描述";

    /// <inheritdoc />
    public void Apply(JsonObject document, MigrationLog log)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(log);

        // 旧的颜色/宽度与 XY 偏移字段继续由读取映射解释；新值对象是可选字段，
        // 因而迁移不猜测用户是否希望改变已有画面。
        log.Add("样式可使用 StrokeSpec 与 ShadowSpec；旧描边和阴影字段保持原有语义。");
    }
}
