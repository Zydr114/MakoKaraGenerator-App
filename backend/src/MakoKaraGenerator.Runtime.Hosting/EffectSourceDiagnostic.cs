using System.Globalization;

namespace MakoKaraGenerator.Runtime.Hosting;

/// <summary>真实源码位置或包字段诊断；无源码行列时使用零。</summary>
public sealed record EffectSourceDiagnostic(string Code, string Message, bool IsError, string? Path, int Line, int Column)
{
    /// <summary>用于诊断列表显示的完整逻辑位置、代码与内容。</summary>
    public string DisplayLabel => string.IsNullOrEmpty(Path) ? Code + " " + Message
        : Line > 0 ? string.Create(CultureInfo.InvariantCulture, $"{Path}:{Line}:{Column} {Code} {Message}")
        : Path + " " + Code + " " + Message;
}
