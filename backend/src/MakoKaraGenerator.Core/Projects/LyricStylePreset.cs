namespace MakoKaraGenerator.Core.Projects;

/// <summary>带有用户可见名称的歌词样式预设。</summary>
/// <remarks>
/// <para>
/// <see cref="Id" /> 是<b>机器</b>用的稳定标识（存进工程文件、按 id 查找），
/// <see cref="Name" /> 是给人看的显示名。两者分开：显示名要允许随时改中文措辞，
/// 而一旦名字参与持久化，改名字就会让旧工程找不到预设。
/// </para>
/// <para>预设提供的是<b>一整份</b>样式基线，行/元素的覆盖在其上逐项生效（见 <see cref="LyricStyleResolver" />）。</para>
/// </remarks>
public sealed record LyricStylePreset
{
    /// <summary>稳定标识，在同一预设集合内唯一。</summary>
    public required string Id { get; init; }

    /// <summary>用户可见名称。</summary>
    public required string Name { get; init; }

    /// <summary>预设样式。</summary>
    public required LyricStyle Style { get; init; }

    /// <summary>创建一个预设。</summary>
    /// <param name="id">稳定标识，只允许字母、数字、<c>-</c>、<c>_</c> 与 <c>.</c>。</param>
    /// <param name="name">用户可见名称，不可为空白。</param>
    /// <param name="style">预设样式。</param>
    /// <returns>新的预设。</returns>
    /// <exception cref="ArgumentException">标识或名称为空白，或标识含不允许的字符时抛出。</exception>
    /// <exception cref="ArgumentNullException"><paramref name="style"/> 为 <see langword="null"/> 时抛出。</exception>
    public static LyricStylePreset Create(string id, string name, LyricStyle style)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(style);

        foreach (char c in id)
        {
            bool allowed = char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.';

            if (!allowed)
            {
                throw new ArgumentException(
                    $"预设标识只允许字母、数字、'-'、'_' 与 '.'，而 '{id}' 里出现了 '{c}'。",
                    nameof(id));
            }
        }

        return new LyricStylePreset { Id = id, Name = name, Style = style };
    }
}
