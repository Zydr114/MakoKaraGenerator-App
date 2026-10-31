using System.Collections.Immutable;
using MakoKaraGenerator.Core.Internal;

namespace MakoKaraGenerator.Core.Lyrics;

/// <summary>
/// 一份歌词文档：对应项目入口指向的不可变歌词修订（docs/development.md §6.1）。
/// </summary>
/// <remarks>
/// 本类型同时是歌词导入器与导出器的处理单元，接口签名见 docs/development.md §7.1。
/// </remarks>
public sealed record LyricsDocument
{
    private ImmutableArray<LyricBranch> _branches =
        [LyricBranch.Create("primary", "主歌词", LyricBranchRole.Primary)];

    /// <summary>主歌词分支的稳定标识。</summary>
    public string PrimaryBranchId { get; init; } = "primary";

    /// <summary>显式的歌词分支容器；它是行与语言的唯一数据来源。</summary>
    public ImmutableArray<LyricBranch> Branches
    {
        get => _branches.IsDefault ? [] : _branches;
        init => _branches = value;
    }

    /// <summary>当前主歌词分支；损坏文档没有对应分支时为空。</summary>
    public LyricBranch? PrimaryBranch => Branches.FirstOrDefault(entry => entry.Id == PrimaryBranchId);

    /// <summary>按分支顺序枚举全部歌词行。</summary>
    public IEnumerable<LyricLine> AllLines => Branches.SelectMany(entry => entry.Lines);

    /// <summary>替换指定分支；未找到标识或内容相同时返回原实例。</summary>
    /// <param name="branch">目标分支。</param>
    /// <returns>更新后的文档。</returns>
    public LyricsDocument ReplaceBranch(LyricBranch branch)
    {
        ArgumentNullException.ThrowIfNull(branch);
        for (int i = 0; i < Branches.Length; i++)
            if (Branches[i].Id == branch.Id)
                return Branches[i] == branch ? this : this with { Branches = Branches.SetItem(i, branch) };
        return this;
    }

    /// <summary>当前实现支持的歌词修订 schema 版本。</summary>
    /// <remarks>v11 显式保存主分支与分支容器、稳定关联、注音和动态布局配置。
    /// 工程与歌词的版本线独立；当前格式不自动转换旧工程（ADR-0050）。</remarks>
    public const int CurrentSchemaVersion = 17;

    /// <summary>文档的 schema 版本。当前程序只接受当前格式（ADR-0050）；历史迁移器不参与新格式转换。</summary>
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>歌词语言标签（BCP-47，例如 <c>"zh-CN"</c>）；未知时为 <see langword="null"/>。</summary>
    public string? Language
    {
        get => PrimaryBranch?.Language;
        init
        {
            for (int i = 0; i < Branches.Length; i++)
                if (Branches[i].Id == PrimaryBranchId)
                    _branches = Branches.SetItem(i, Branches[i] with { Language = value });
        }
    }

    /// <summary>
    /// 主歌词行序列的便捷访问；写入只替换主分支的行，不改变其他分支。
    /// </summary>
    /// <remarks>
    /// 取值时会把 <c>default</c> 规范化为空序列；原因见 <see cref="LyricLine.Tokens"/>。
    /// </remarks>
    public ImmutableArray<LyricLine> Lines
    {
        get => PrimaryBranch?.Lines ?? [];
        init
        {
            for (int i = 0; i < Branches.Length; i++)
                if (Branches[i].Id == PrimaryBranchId)
                    _branches = Branches.SetItem(i, Branches[i] with { Lines = value });
        }
    }

    /// <summary>文档中所有文本元素的个数。</summary>
    public int TokenCount
    {
        get
        {
            int count = 0;

            foreach (LyricLine line in AllLines)
            {
                count += line.Tokens.Length;
            }

            return count;
        }
    }

    /// <summary>创建一个歌词文档。</summary>
    /// <param name="lines">歌词行序列。</param>
    /// <param name="language">语言标签，可为 <see langword="null"/>。</param>
    /// <param name="schemaVersion">schema 版本，默认使用当前版本。</param>
    /// <returns>新的歌词文档实例。</returns>
    /// <exception cref="ArgumentException">行标识重复时抛出。</exception>
    public static LyricsDocument Create(
        IEnumerable<LyricLine>? lines = null,
        string? language = null,
        int schemaVersion = CurrentSchemaVersion)
    {
        ImmutableArray<LyricLine> lineArray = lines is null
            ? ImmutableArray<LyricLine>.Empty
            : [.. lines];

        EnsureUniqueLineIds(lineArray);

        return new LyricsDocument
        {
            SchemaVersion = schemaVersion,
            Language = language,
            Lines = lineArray,
        };
    }

    /// <summary>依次枚举所有行的所有文本元素，同时给出所属行。</summary>
    /// <returns>元素与所属行的序列。</returns>
    public IEnumerable<(LyricLine Line, LyricToken Token)> EnumerateTokens()
    {
        foreach (LyricLine line in AllLines)
        {
            foreach (LyricToken token in line.Tokens)
            {
                yield return (line, token);
            }
        }
    }

    /// <summary>
    /// 手写的结构相等性；原因见 <see cref="LyricLine.Equals(LyricLine?)"/>。
    /// </summary>
    /// <param name="other">另一份文档。</param>
    /// <returns>所有字段与行内容均相等时为 <see langword="true"/>。</returns>
    public bool Equals(LyricsDocument? other)
    {
        if (other is null)
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        return SchemaVersion == other.SchemaVersion
            && string.Equals(PrimaryBranchId, other.PrimaryBranchId, StringComparison.Ordinal)
            && SequenceEquality.ContentEquals(Branches, other.Branches);
    }

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(SchemaVersion);
        hash.Add(PrimaryBranchId, StringComparer.Ordinal);
        SequenceEquality.AddContentHash(ref hash, Branches);
        return hash.ToHashCode();
    }

    private static void EnsureUniqueLineIds(ImmutableArray<LyricLine> lines)
    {
        if (lines.IsEmpty)
        {
            return;
        }

        HashSet<string> seen = new(StringComparer.Ordinal);

        foreach (LyricLine line in lines)
        {
            if (!seen.Add(line.Id))
            {
                throw new ArgumentException($"歌词行标识重复：'{line.Id}'。", nameof(lines));
            }
        }
    }
}
