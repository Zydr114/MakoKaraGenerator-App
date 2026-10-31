namespace MakoKaraGenerator.Application.Editing;

/// <summary>时间编辑的稳定目标；元素标识为空时编辑行时间。</summary>
/// <param name="BranchId">来源分支标识。</param>
/// <param name="LineId">所属歌词行标识。</param>
/// <param name="TokenId">元素标识；为空时目标为整行。</param>
public sealed record LyricTimingEditTarget(string BranchId, string LineId, string? TokenId = null);
