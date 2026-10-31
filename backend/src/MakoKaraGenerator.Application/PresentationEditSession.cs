using MakoKaraGenerator.Core.Diagnostics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Validation;

namespace MakoKaraGenerator.Application;

/// <summary>展示编辑的不可变预演会话；基准工程变化后明确拒绝提交。</summary>
public sealed record PresentationEditSession
{
    /// <summary>创建只读基准和初始草稿。</summary>
    public PresentationEditSession(Project source) { ArgumentNullException.ThrowIfNull(source); Source = source; Draft = source; }
    /// <summary>开始编辑时的工程引用。</summary>
    public Project Source { get; }
    /// <summary>已经统一校验的预演工程。</summary>
    public Project Draft { get; private init; }
    /// <summary>是否包含实际展示修改。</summary>
    public bool HasChanges => Draft.LyricPresentation != Source.LyricPresentation;
    /// <summary>采纳有效预演，不修改源歌词、样式或合成数据。</summary>
    public bool TryPreview(Project candidate, out PresentationEditSession result, out ValidationReport report)
    {
        result = this;
        if (!ReferenceEquals(candidate.Lyrics, Source.Lyrics) || candidate.Composition != Source.Composition
            || candidate.LyricStyle != Source.LyricStyle || candidate.Background != Source.Background)
        {
            report = ValidationReport.From([new ProjectIssue { Code = DiagnosticCode.InvalidDisplayProgram,
                Severity = IssueSeverity.Error, Message = "展示草稿不得修改源歌词、样式或合成数据。" }]);
            return false;
        }
        report = PresentationValidator.Validate(candidate);
        if (report.HasErrors) return false;
        result = candidate.LyricPresentation == Draft.LyricPresentation ? this : this with { Draft = candidate };
        return true;
    }
    /// <summary>验证提交时基准仍有效；无变化返回原工程。</summary>
    public bool TryCommit(Project current, out Project result, out ValidationReport report)
    {
        result = current;
        if (!ReferenceEquals(current, Source))
        {
            report = ValidationReport.From([new ProjectIssue { Code = DiagnosticCode.InvalidDisplayProgram,
                Severity = IssueSeverity.Error, Message = "工程已经变化，展示预演已过期，请取消后重新编辑。" }]);
            return false;
        }
        report = PresentationValidator.Validate(Draft);
        if (report.HasErrors) return false;
        result = HasChanges ? Source with { LyricPresentation = Draft.LyricPresentation } : Source;
        return true;
    }
}
