namespace MakoKaraGenerator.Core.Lyrics.Import;

/// <summary>
/// 扩展名 → 候选导入器的登记表（识别的第一层，ADR-0046）。
/// </summary>
/// <remarks>
/// <para>
/// 只有扩展名是不够的：普通 LRC 与增强 LRC 的扩展名完全相同，
/// 只能靠"行内有没有逐字标签"分开。因此本表只负责给出<b>候选</b>，
/// 真正命中哪个由 <see cref="ILyricsImporter.CanImport" /> 与注册表的优先级决定。
/// </para>
/// <para>
/// 未知扩展名返回空列表：这时注册表退回"所有导入器都试一遍内容嗅探"，
/// 与加入本表之前的行为一致，不因为多了这个目录而变得更脆。
/// </para>
/// </remarks>
public sealed class LyricsFormatCatalog
{
    private static readonly Dictionary<string, string[]> DefaultTable = new(StringComparer.OrdinalIgnoreCase)
    {
        [".lrc"] = ["lrc.enhanced", "lrc.plain"],
        [".elrc"] = ["lrc.enhanced", "lrc.plain"],
        [".txt"] = ["ustar", "lrc.enhanced", "text.plain", "lrc.plain"],
        [".srt"] = ["srt"],
        [".vtt"] = ["vtt"],
        [".ass"] = ["ass"],
        [".ssa"] = ["ass"],
        [".ttml"] = ["ttml"],
        [".xml"] = ["ttml"],
    };

    private readonly Dictionary<string, IReadOnlyList<string>> _table;

    /// <summary>默认登记表。</summary>
    public static LyricsFormatCatalog Default { get; } = new(DefaultTable);

    /// <summary>文件选择器应列出的扩展名（按展示顺序，含已知的全部导入格式）。</summary>
    public static IReadOnlyList<string> PickerExtensions { get; } =
        [".lrc", ".elrc", ".txt", ".srt", ".vtt", ".ass", ".ssa", ".ttml", ".xml"];

    /// <summary>创建一个登记表。</summary>
    /// <param name="entries">扩展名（含点）→ 候选导入器标识，按优先级排列。</param>
    /// <exception cref="ArgumentNullException"><paramref name="entries" /> 为 <see langword="null" /> 时抛出。</exception>
    public LyricsFormatCatalog(IEnumerable<KeyValuePair<string, string[]>> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        _table = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

        foreach ((string extension, string[] importerIds) in entries)
        {
            _table[extension] = importerIds;
        }
    }

    /// <summary>已登记的扩展名（含点）。</summary>
    public IReadOnlyCollection<string> KnownExtensions => _table.Keys;

    /// <summary>取某文件名的候选导入器标识。</summary>
    /// <param name="fileName">文件名（含扩展名）。</param>
    /// <returns>候选标识（按优先级）；扩展名未登记时为空。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="fileName" /> 为 <see langword="null" /> 时抛出。</exception>
    public IReadOnlyList<string> CandidatesFor(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);

        string extension = Path.GetExtension(fileName);

        return extension.Length > 0 && _table.TryGetValue(extension, out IReadOnlyList<string>? ids)
            ? ids
            : [];
    }
}
