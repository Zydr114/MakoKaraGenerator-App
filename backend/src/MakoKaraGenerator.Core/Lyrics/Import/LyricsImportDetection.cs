using System.Collections.Immutable;

namespace MakoKaraGenerator.Core.Lyrics.Import;

/// <summary>一个候选导入器与它给出的匹配理由。</summary>
/// <param name="Importer">候选导入器。</param>
/// <param name="Reason">它认为能处理的理由。</param>
public sealed record LyricsImportCandidate(ILyricsImporter Importer, string Reason);

/// <summary>
/// 识别结果：选中的导入器、理由，以及还有哪些候选（ADR-0046）。
/// </summary>
/// <remarks>
/// <see cref="IsAmbiguous" /> 为 <see langword="true" /> 表示不止一个候选能处理这个文件
/// （典型例子：同一个 <c>.lrc</c> 既像普通 LRC 又像增强 LRC）。
/// 这时仍然按注册表优先级选一个，但必须在报告里说清"还有谁能处理、为什么没选它"。
/// </remarks>
/// <param name="Importer">选中的导入器。</param>
/// <param name="Reason">选中理由。</param>
/// <param name="IsAmbiguous">是否存在多个匹配。</param>
/// <param name="Matches">全部匹配（按优先级）。</param>
public sealed record LyricsImportDetection(
    ILyricsImporter Importer,
    string Reason,
    bool IsAmbiguous,
    ImmutableArray<LyricsImportCandidate> Matches)
{
    /// <summary>匹配到的导入器显示名（按优先级），用于写进报告。</summary>
    public ImmutableArray<string> CandidateNames =>
        [.. Matches.Select(match => match.Importer.DisplayName)];
}
