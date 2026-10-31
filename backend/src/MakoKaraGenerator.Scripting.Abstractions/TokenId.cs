namespace MakoKaraGenerator.Scripting;

/// <summary>源歌词元素的只读身份；不与场景节点或装载身份混用。</summary>
public readonly record struct TokenId
{
    /// <summary>创建非空身份，不修正或裁剪源 ID。</summary>
    /// <param name="value">源元素的稳定身份。</param>
    public TokenId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value;
    }

    /// <summary>原始 ordinal 身份值。</summary>
    public string Value { get; }

    /// <summary>是否为有效身份；默认结构值无效。</summary>
    public bool IsValid => !string.IsNullOrWhiteSpace(Value);

    /// <summary>返回原始身份值。</summary>
    public override string ToString() => Value ?? string.Empty;
}
