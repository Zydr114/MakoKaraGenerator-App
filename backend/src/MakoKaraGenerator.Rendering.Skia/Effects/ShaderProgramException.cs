namespace MakoKaraGenerator.Rendering.Skia;

/// <summary>固定 shader 编译／输入失败；不伪装为透明绕过或空效果。</summary>
public sealed class ShaderProgramException : InvalidOperationException
{
    /// <summary>创建含代码、包内路径和字段的诊断。</summary>
    public ShaderProgramException(string code, string sourcePath, string field, string message, Exception? innerException = null)
        : base($"{code}: {sourcePath} {field}: {message}", innerException)
    { Code = code; SourcePath = sourcePath; Field = field; }
    /// <summary>稳定错误代码。</summary>
    public string Code { get; }
    /// <summary>包内源码逻辑路径。</summary>
    public string SourcePath { get; }
    /// <summary>失败的 uniform／child 或编译字段。</summary>
    public string Field { get; }
}
