using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;

namespace MakoKaraGenerator.Core.Persistence;

/// <summary>
/// 项目包中的文件名与 JSON 读写设置。
/// </summary>
/// <remarks>
/// 项目包结构见 docs/development.md §6.1。首期使用可读的目录形式而不是私有二进制格式，
/// 并且 <c>cache/</c> 与 <c>backups/</c> 不属于源数据，不出现在这里。
/// </remarks>
public static class ProjectJson
{
    /// <summary>项目入口文件名。</summary>
    public const string ProjectFileName = "project.json";

    /// <summary>不可变歌词修订的工程相对目录（路径分隔符固定为斜杠）。</summary>
    public const string LyricsDirectoryName = "data/lyrics";

    /// <summary>脚本目录名；完整脚本工程持久化按 R4 合同接续。</summary>
    public const string ScriptsDirectoryName = "scripts";

    /// <summary>原子写入时使用的临时文件后缀。</summary>
    public const string TemporaryFileSuffix = ".tmp";

    /// <summary>读写项目文件时使用的 JSON 设置。</summary>
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        JsonSerializerOptions options = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            // 源数据格式偏重"人可读、可 diff"，因此缩进写、不转义中文。
            WriteIndented = true,
            // 手工编辑项目文件时容忍注释与尾随逗号，读取更友好。
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            // 只保留必要的转义：默认编码器会把中文转成 \uXXXX，破坏可读性。
            Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            // null 字段直接省略，保持文件简洁；读回时同样得到 null。
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
