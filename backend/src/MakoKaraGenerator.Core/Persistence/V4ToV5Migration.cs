using System.Text.Json.Nodes;

namespace MakoKaraGenerator.Core.Persistence;

/// <summary>
/// v4 → v5：歌词样式新增水平对齐方式。
/// </summary>
/// <remarks>
/// <para>
/// 与前面几步同样<b>不改写数据</b>：旧文件里没有这个字段，正是"还没设过对齐"，
/// 语义上等价于默认的居中——也就是加入该字段之前的实际行为。因此升级后画面上
/// 一个像素都不会变，往文件里补一个 "center" 只会制造无意义的 diff。
/// </para>
/// <para>
/// 步骤必须存在：注册表要把 v4 认下来，并给用户一条"你的文件被按 v5 解释了"的日志。
/// </para>
/// </remarks>
internal sealed class V4ToV5Migration : IProjectSchemaMigration
{
    /// <inheritdoc/>
    public int FromVersion => 4;

    /// <inheritdoc/>
    public int ToVersion => 5;

    /// <inheritdoc/>
    public string Description => "歌词样式新增水平对齐方式";

    /// <inheritdoc/>
    public void Apply(JsonObject document, MigrationLog log)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(log);

        log.Add("v4 文件没有水平对齐字段：按默认的居中解释，不写入默认值。");
    }
}
