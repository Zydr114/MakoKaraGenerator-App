using System.Text.Json.Nodes;

namespace MakoKaraGenerator.Core.Persistence;

/// <summary>加入字重、斜体、字距与行／元素方向阴影的可选字段。</summary>
internal sealed class StyleEditorParametersMigration(int fromVersion) : IProjectSchemaMigration
{
    /// <inheritdoc />
    public int FromVersion => fromVersion;

    /// <inheritdoc />
    public int ToVersion => fromVersion + 1;

    /// <inheritdoc />
    public string Description => "增加字体参数和方向阴影覆盖";

    /// <inheritdoc />
    public void Apply(JsonObject document, MigrationLog log)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(log);
        // 所有新字段可选，旧工程保持 400 字重、正体和零字距。
        log.Add("新增字体参数与方向阴影覆盖；原有样式保持不变。");
    }
}
