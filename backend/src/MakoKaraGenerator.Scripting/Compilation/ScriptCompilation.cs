using System.Collections.Immutable;

namespace MakoKaraGenerator.Scripting.Compilation;

/// <summary>一份固定路径的 UTF-8 C# 源码；路径用于编译诊断及 portable PDB。</summary>
/// <param name="Path">包内相对路径。</param>
/// <param name="Text">固定源码内容。</param>
public sealed record ScriptSource(string Path, string Text);

/// <summary>带源码位置的编译诊断。</summary>
/// <param name="Code">Roslyn 诊断编号。</param>
/// <param name="Message">诊断内容。</param>
/// <param name="IsError">是否阻止使用此次编译结果。</param>
/// <param name="Path">源码路径，无位置时为空。</param>
/// <param name="Line">从 1 开始的行号，无位置时为 0。</param>
/// <param name="Column">从 1 开始的列号，无位置时为 0。</param>
public sealed record ScriptCompileDiagnostic(string Code, string Message, bool IsError, string? Path, int Line, int Column);

/// <summary>与项目数据分离的编译结果；失败时不发布 DLL。</summary>
/// <param name="ArtifactHash">源码、SDK、引用和编译选项的指纹。</param>
/// <param name="PeImage">DLL 的不可变字节。</param>
/// <param name="PdbImage">portable PDB 的不可变字节。</param>
/// <param name="Diagnostics">此次全部警告／错误。</param>
public sealed record ScriptCompilationResult(
    string ArtifactHash, ImmutableArray<byte> PeImage, ImmutableArray<byte> PdbImage,
    ImmutableArray<ScriptCompileDiagnostic> Diagnostics)
{
    /// <summary>成功产物的完整身份及字节摘要；失败结果没有可运行描述。</summary>
    public ScriptArtifactDescriptor? Descriptor { get; init; }
    /// <summary>编译是否成功且存在有效产物。</summary>
    public bool Succeeded => !PeImage.IsDefaultOrEmpty && !Diagnostics.Any(static d => d.IsError);
}
