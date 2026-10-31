using System.Text.Json.Nodes;

namespace MakoKaraGenerator.Core.Persistence;

/// <summary>
/// v1 → v2：新增歌词样式字段（基线样式、预设标识、行/元素样式覆盖）。
/// </summary>
/// <remarks>
/// <para>
/// 这一步<b>不改写数据</b>，只是把它解释成新版本：v2 新增的字段全部遵循
/// "缺失即默认"——没有 <c>lyricStyle</c> 就是默认样式，没有 <c>styleOverride</c>
/// 就是"不覆盖"。因此旧文件不需要插入任何字段，插入反而是在用户数据里凭空造出内容。
/// </para>
/// <para>
/// 步骤本身仍然必须存在：注册表要把 v1 认下来，并给用户一条"你的文件被按 v2 解释了"的日志。
/// </para>
/// </remarks>
internal sealed class V1ToV2Migration : IProjectSchemaMigration
{
    /// <inheritdoc/>
    public int FromVersion => 1;

    /// <inheritdoc/>
    public int ToVersion => 2;

    /// <inheritdoc/>
    public string Description => "新增歌词样式字段（基线样式、预设标识、行/元素样式覆盖）";

    /// <inheritdoc/>
    public void Apply(JsonObject document, MigrationLog log)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(log);

        log.Add("v1 文件没有样式字段：按默认样式与「无覆盖」解释，不写入新的默认值。");
    }
}
