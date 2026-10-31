using System.Text.Json.Nodes;

namespace MakoKaraGenerator.Core.Persistence;

/// <summary>
/// 歌词文件 v1 → v2：行与元素的样式覆盖进入文件。
/// </summary>
/// <remarks>
/// <para>
/// 歌词文件的 1 → 2 与工程文件的同名一步是<b>两件事</b>：工程文件那步讲基线样式与预设标识，
/// 这一步讲行/元素覆盖。因此它必须单独登记在歌词步骤表里——歌词文件曾经借用工程文件的步骤，
/// 拆分步骤表后就会因为"缺少 1 → 2"而整体读不出来（实测八个诊断测试直接红）。
/// </para>
/// <para>
/// 同样<b>不改写数据</b>：没有 <c>styleOverride</c> 就是"这一层没有覆盖"。
/// </para>
/// </remarks>
internal sealed class LyricsV1ToV2Migration : IProjectSchemaMigration
{
    /// <inheritdoc/>
    public int FromVersion => 1;

    /// <inheritdoc/>
    public int ToVersion => 2;

    /// <inheritdoc/>
    public string Description => "新增行与元素的样式覆盖字段";

    /// <inheritdoc/>
    public void Apply(JsonObject document, MigrationLog log)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(log);

        log.Add("v1 歌词文件没有样式覆盖：按「无覆盖」解释，不写入新的默认值。");
    }
}
