namespace MakoKaraGenerator.Runtime.Hosting.Authoring;

/// <summary>作者试样的明确选择门禁，由界面映射显示文本。</summary>
public sealed class TemplateSampleException(string code) : ArgumentException(code)
{
    /// <summary>稳定选择失败标识，界面负责翻译。</summary>
    public string Code { get; } = code;
}
