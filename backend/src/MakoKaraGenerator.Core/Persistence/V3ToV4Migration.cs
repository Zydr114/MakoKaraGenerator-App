using System.Text.Json.Nodes;

namespace MakoKaraGenerator.Core.Persistence;

/// <summary>
/// v3 → v4：歌词样式新增渐变、描边、阴影、不透明度与基础缩放。
/// </summary>
/// <remarks>
/// <para>
/// 与前面两步同样<b>不改写数据</b>：旧文件里没有这些字段，正是“这些外观还没设置过”，
/// 语义上等价于默认值（无渐变、无描边、无阴影、不透明、缩放为 1）。
/// 往旧文件里补十几个默认值只会让它在 diff 里凭空多出一片，并且掩盖“这一版新增了什么”。
/// </para>
/// <para>
/// 唯一的例外是开发期那一对实验性渐变字段：它们已被渐变样式取代（当时是三态，ADR-0040 后收口为未唱/已唱两态），
/// 留着不会被读取，所以按“已唱渐变”翻译过来（见 <see cref="LegacyGradientFields"/>）。
/// </para>
/// <para>
/// 步骤必须存在：注册表要把 v3 认下来，并给用户一条“你的文件被按 v4 解释了”的日志。
/// </para>
/// </remarks>
internal sealed class V3ToV4Migration : IProjectSchemaMigration
{
    /// <inheritdoc/>
    public int FromVersion => 3;

    /// <inheritdoc/>
    public int ToVersion => 4;

    /// <inheritdoc/>
    public string Description => "歌词样式新增渐变、描边、阴影、不透明度与基础缩放";

    /// <inheritdoc/>
    public void Apply(JsonObject document, MigrationLog log)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(log);

        if (document["lyricStyle"] is JsonObject style)
        {
            LegacyGradientFields.Translate(style, log);
        }

        log.Add("v3 文件没有描边、阴影、不透明度与缩放字段：按「未设置」解释，不写入默认值。");
    }
}
