using System.Text.Json.Nodes;

namespace MakoKaraGenerator.Core.Persistence;

/// <summary>
/// 一次针对 JSON 文档的 schema 迁移。
/// </summary>
/// <remarks>
/// <para>
/// 迁移作用在<b>JSON 层</b>（docs/development.md §6.4：旧版本 JSON → 迁移器 → 当前内存模型）。
/// 这样做的理由：迁移要在领域模型之外完成，UI 与渲染器就不必到处兼容旧字段；
/// 同时迁移逻辑可以脱离具体 DTO 单独测试。
/// </para>
/// <para>
/// 实现必须是纯函数式的改写：只修改传入的文档，不读文件、不写文件。
/// 迁移失败时调用方保留原文件，并把日志交给用户（§6.4）。
/// </para>
/// </remarks>
public interface IProjectSchemaMigration
{
    /// <summary>起始版本。</summary>
    int FromVersion { get; }

    /// <summary>目标版本。</summary>
    int ToVersion { get; }

    /// <summary>给人看的说明，会出现在迁移日志里。</summary>
    string Description { get; }

    /// <summary>改写文档。</summary>
    /// <param name="document">待迁移的 JSON 文档，就地修改。</param>
    /// <param name="log">迁移日志。</param>
    /// <exception cref="ArgumentNullException">任一参数为 <see langword="null"/> 时抛出。</exception>
    void Apply(JsonObject document, MigrationLog log);
}

/// <summary>迁移过程中产生的可读日志。</summary>
/// <remarks>
/// 日志不是调试信息，而是给用户的交代：升级过的项目必须能说清"改了什么"，
/// 否则用户无法判断自己的文件是否被动过（docs/development.md §6.4、§19）。
/// </remarks>
public sealed class MigrationLog
{
    private readonly List<string> _entries = [];

    /// <summary>日志条目，按发生顺序。</summary>
    public IReadOnlyList<string> Entries => _entries;

    /// <summary>是否发生过迁移。</summary>
    public bool IsEmpty => _entries.Count == 0;

    /// <summary>追加一条日志。</summary>
    /// <param name="message">日志内容。</param>
    /// <exception cref="ArgumentException"><paramref name="message"/> 为空白时抛出。</exception>
    public void Add(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        _entries.Add(message);
    }
}
