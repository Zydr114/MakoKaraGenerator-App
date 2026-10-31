using System.Collections.Immutable;
using MakoKaraGenerator.Core.Internal;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Core.Lyrics;

/// <summary>歌词分支的产品角色。</summary>
public enum LyricBranchRole
{
    /// <summary>主歌词，通常包含逐字时间。</summary>
    Primary = 0,

    /// <summary>翻译歌词，默认静态显示。</summary>
    Translation = 1,

    /// <summary>注音或读音分支。</summary>
    Annotation = 2,

    /// <summary>用户自定义分支。</summary>
    Custom = 3,
}

/// <summary>歌词分支的时间来源策略。</summary>
public enum LyricBranchTimingMode
{
    /// <summary>使用分支自身的行时间。</summary>
    Independent = 0,

    /// <summary>跟随关联主歌词行的可见区间。</summary>
    FollowPrimary = 1,

    /// <summary>没有时间，交由布局策略决定是否显示。</summary>
    None = 2,
}

/// <summary>歌词分支的动画策略。</summary>
public enum LyricBranchAnimationPolicy
{
    /// <summary>使用逐字进度和分支动画配置。</summary>
    Karaoke = 0,

    /// <summary>只显示静态文本，不参与逐字动画。</summary>
    Static = 1,

    /// <summary>跟随关联主歌词的整体可见区间，不继承逐字动画。</summary>
    FollowPrimary = 2,
}

/// <summary>
/// 一个歌词分支，由 <see cref="LyricsDocument" /> 的显式容器持久化承载。
/// </summary>
public sealed record LyricBranch
{
    private ImmutableArray<LyricLine> _lines;

    /// <summary>分支标识。</summary>
    public required string Id { get; init; }

    /// <summary>显示名称。</summary>
    public required string Name { get; init; }

    /// <summary>分支角色。</summary>
    public LyricBranchRole Role { get; init; } = LyricBranchRole.Custom;

    /// <summary>语言标签，例如 <c>ja-JP</c> 或 <c>zh-CN</c>。</summary>
    public string? Language { get; init; }

    /// <summary>时间来源策略。</summary>
    public LyricBranchTimingMode TimingMode { get; init; } = LyricBranchTimingMode.Independent;

    /// <summary>分支样式覆盖；为空表示继承工程样式。</summary>
    public LyricStyleOverride? StyleOverride { get; init; }

    /// <summary>分支中的歌词行。</summary>
    public ImmutableArray<LyricLine> Lines
    {
        get => _lines.IsDefault ? ImmutableArray<LyricLine>.Empty : _lines;
        init => _lines = value;
    }

    /// <summary>分支结构是否有效。</summary>
    public bool IsValid
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Id) || string.IsNullOrWhiteSpace(Name)
                || !Enum.IsDefined(Role) || !Enum.IsDefined(TimingMode))
            {
                return false;
            }

            HashSet<string> ids = new(StringComparer.Ordinal);

            foreach (LyricLine line in Lines)
            {
                if (!ids.Add(line.Id))
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>创建一个已校验的歌词分支。</summary>
    /// <param name="id">分支标识。</param>
    /// <param name="name">显示名称。</param>
    /// <param name="role">分支角色。</param>
    /// <param name="lines">歌词行。</param>
    /// <param name="language">语言标签。</param>
    /// <param name="timingMode">时间来源策略。</param>
    /// <param name="styleOverride">分支样式覆盖。</param>
    /// <returns>新的歌词分支。</returns>
    /// <exception cref="ArgumentException">标识为空或行标识重复时抛出。</exception>
    public static LyricBranch Create(
        string id,
        string name,
        LyricBranchRole role = LyricBranchRole.Custom,
        IEnumerable<LyricLine>? lines = null,
        string? language = null,
        LyricBranchTimingMode timingMode = LyricBranchTimingMode.Independent,
        LyricStyleOverride? styleOverride = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        LyricBranch result = new()
        {
            Id = id,
            Name = name,
            Role = role,
            Language = language,
            TimingMode = timingMode,
            StyleOverride = styleOverride,
            Lines = lines is null ? ImmutableArray<LyricLine>.Empty : [.. lines],
        };

        return result.IsValid
            ? result
            : throw new ArgumentException("歌词分支中的行标识必须唯一。", nameof(lines));
    }

    /// <summary>按行内容比较两个分支，而不是比较数组引用。</summary>
    /// <param name="other">另一个分支。</param>
    /// <returns>分支元数据和行内容均相同时为 <see langword="true"/>。</returns>
    public bool Equals(LyricBranch? other)
    {
        if (other is null)
        {
            return false;
        }

        return string.Equals(Id, other.Id, StringComparison.Ordinal)
            && string.Equals(Name, other.Name, StringComparison.Ordinal)
            && Role == other.Role
            && string.Equals(Language, other.Language, StringComparison.Ordinal)
            && TimingMode == other.TimingMode
            && Equals(StyleOverride, other.StyleOverride)
            && SequenceEquality.ContentEquals(Lines, other.Lines);
    }

    /// <inheritdoc />
    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(Id, StringComparer.Ordinal);
        hash.Add(Name, StringComparer.Ordinal);
        hash.Add(Role);
        hash.Add(Language, StringComparer.Ordinal);
        hash.Add(TimingMode);
        hash.Add(StyleOverride);
        SequenceEquality.AddContentHash(ref hash, Lines);
        return hash.ToHashCode();
    }
}
