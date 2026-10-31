namespace MakoKaraGenerator.Core.Diagnostics;

/// <summary>
/// 一条诊断信息。
/// </summary>
/// <remarks>
/// 诊断必须对人类可读：<see cref="Message"/> 面向用户，
/// <see cref="Location"/> 给出可定位的路径（例如 <c>lyrics.lines[0].tokens[2].endUs</c>），
/// <see cref="Code"/> 供测试与界面做稳定判断。
/// </remarks>
public sealed record ProjectIssue
{
    /// <summary>稳定标识，用于测试断言与界面分类。</summary>
    public required DiagnosticCode Code { get; init; }

    /// <summary>严重程度。</summary>
    public required IssueSeverity Severity { get; init; }

    /// <summary>面向用户的说明文本。</summary>
    public required string Message { get; init; }

    /// <summary>可定位的数据路径；无法定位时为 <see langword="null"/>。</summary>
    public string? Location { get; init; }

    /// <summary>以 <c>"[错误] 歌词行文本为空 (lyrics.lines[0].text)"</c> 形式返回。</summary>
    public override string ToString() =>
        Location is null
            ? $"[{Severity}] {Message}"
            : $"[{Severity}] {Message} ({Location})";
}
