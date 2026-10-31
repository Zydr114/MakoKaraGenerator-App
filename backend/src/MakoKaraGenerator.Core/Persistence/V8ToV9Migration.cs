using System.Text.Json.Nodes;

namespace MakoKaraGenerator.Core.Persistence;

/// <summary>
/// v8 → v9：工程新增"行进入动画"的默认值。
/// </summary>
/// <remarks>
/// <para>
/// <b>不改写数据</b>：旧文件里没有这个字段，含义正是"不做动画"——这与加字段之前的行为一致
/// （那时根本不可能有动画）。升级后画面一个像素都不会变。
/// </para>
/// <para>
/// 步骤必须存在：注册表要把 v8 认下来，并给用户一条"你的文件被按 v9 解释了"的日志。
/// </para>
/// </remarks>
internal sealed class V8ToV9Migration : IProjectSchemaMigration
{
    /// <inheritdoc/>
    public int FromVersion => 8;

    /// <inheritdoc/>
    public int ToVersion => 9;

    /// <inheritdoc/>
    public string Description => "工程新增行进入动画的默认值";

    /// <inheritdoc/>
    public void Apply(JsonObject document, MigrationLog log)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(log);

        log.Add("v8 文件没有进入动画字段：按「不做动画」解释，不写入默认值。");
    }
}
