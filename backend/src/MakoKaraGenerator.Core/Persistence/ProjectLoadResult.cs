using MakoKaraGenerator.Core.Diagnostics;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Core.Persistence;

/// <summary>
/// 读取项目的结果：要么给出项目与诊断，要么只给出诊断。
/// </summary>
/// <remarks>
/// 区分"读到了项目"与"可以导出"是有意的：带错误的项目仍然应该能打开，
/// 让用户看到并修正问题；错误的真正含义是"不能导出"（docs/development.md §6.3）。
/// </remarks>
public sealed record ProjectLoadResult
{
    /// <summary>读出的项目；读取在结构层面失败时为 <see langword="null"/>。</summary>
    public Project? Project { get; init; }

    /// <summary>读取与校验产生的诊断。</summary>
    public required ValidationReport Report { get; init; }

    /// <summary>是否成功得到了项目对象。</summary>
    public bool Loaded => Project is not null;

    /// <summary>是否可以在当前状态下继续导出。</summary>
    public bool CanExport => Project is not null && !Report.HasErrors;
}
