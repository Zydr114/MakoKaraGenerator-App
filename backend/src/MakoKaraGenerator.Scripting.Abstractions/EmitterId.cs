namespace MakoKaraGenerator.Scripting;

/// <summary>包内发射器的独立 ordinal 身份；默认值无效，不与其他 ID 隐式混用。</summary>
public readonly record struct EmitterId
{
    /// <summary>创建非空身份；不裁剪或修改原值。</summary>
    /// <param name="value">稳定身份原值。</param>
    public EmitterId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value;
    }
    /// <summary>原始 ordinal 值。</summary>
    public string Value { get; }
    /// <summary>是否不是无效默认值。</summary>
    public bool IsValid => !string.IsNullOrWhiteSpace(Value);
    /// <summary>返回原值。</summary>
    public override string ToString() => Value ?? string.Empty;
}
