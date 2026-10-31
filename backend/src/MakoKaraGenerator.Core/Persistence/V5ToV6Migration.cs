using System.Text.Json.Nodes;

namespace MakoKaraGenerator.Core.Persistence;

/// <summary>
/// v5 → v6：项目新增背景源（背景图片与适配方式）。
/// </summary>
/// <remarks>
/// <para>
/// 与前面几步同样<b>不改写数据</b>：旧文件里没有背景字段，正是"没有额外背景画面"，
/// 语义上等价于加入该字段之前的行为（只有背景色）。升级后画面一个像素都不会变。
/// </para>
/// <para>
/// 步骤必须存在：注册表要把 v5 认下来，并给用户一条"你的文件被按 v6 解释了"的日志。
/// </para>
/// </remarks>
internal sealed class V5ToV6Migration : IProjectSchemaMigration
{
    /// <inheritdoc/>
    public int FromVersion => 5;

    /// <inheritdoc/>
    public int ToVersion => 6;

    /// <inheritdoc/>
    public string Description => "项目新增背景源（背景图片与适配方式）";

    /// <inheritdoc/>
    public void Apply(JsonObject document, MigrationLog log)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(log);

        log.Add("v5 文件没有背景字段：按「只有背景色」解释，不写入默认值。");
    }
}
