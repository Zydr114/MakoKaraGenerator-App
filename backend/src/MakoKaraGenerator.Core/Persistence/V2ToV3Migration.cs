using System.Text.Json.Nodes;

namespace MakoKaraGenerator.Core.Persistence;

/// <summary>
/// v2 → v3：新增音频路径字段。
/// </summary>
/// <remarks>
/// <para>
/// 与 v1 → v2 同样<b>不改写数据</b>：没有 <c>audioPath</c> 就是"这个工程还没导入音频"，
/// 往旧文件里插一个空值只会让它在 diff 里凭空多出一行。
/// </para>
/// <para>
/// 步骤本身必须存在：注册表要把 v2 认下来，并给用户一条"你的文件被按 v3 解释了"的日志。
/// </para>
/// </remarks>
internal sealed class V2ToV3Migration : IProjectSchemaMigration
{
    /// <inheritdoc/>
    public int FromVersion => 2;

    /// <inheritdoc/>
    public int ToVersion => 3;

    /// <inheritdoc/>
    public string Description => "新增音频路径";

    /// <inheritdoc/>
    public void Apply(JsonObject document, MigrationLog log)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(log);

        log.Add("v2 文件没有音频路径：按「尚未导入音频」解释，不写入空字段。");
    }
}
