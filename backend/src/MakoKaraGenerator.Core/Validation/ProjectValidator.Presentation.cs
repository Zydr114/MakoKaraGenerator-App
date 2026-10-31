using MakoKaraGenerator.Core.Diagnostics;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Core.Validation;

public static partial class ProjectValidator
{
    private static void ValidatePresentation(Project project, List<ProjectIssue> issues)
    {
        issues.AddRange(PresentationValidator.Validate(project).Issues);
        foreach (var line in project.LyricPresentation.LineOverrides)
            ValidateLineAnimation(line.EnterAnimation, "project.lyricPresentation.lineOverrides[" + line.LineId + "].enterAnimation", issues);
    }
}
