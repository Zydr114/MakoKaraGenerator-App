using MakoKaraGenerator.Core.Diagnostics;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Projects;
using static MakoKaraGenerator.Core.Diagnostics.DiagnosticList;

namespace MakoKaraGenerator.Core.Validation;

public static partial class ProjectValidator
{
    private static void ValidatePresetApplications(Project project, List<ProjectIssue> issues)
    {
        foreach (var item in project.PresetApplications.Items)
        {
            IssueSeverity severity = item.Enabled ? IssueSeverity.Error : IssueSeverity.Warning;
            try
            {
                if (!PresetApplicationTargets.Exists(project, item.Target)) throw new ArgumentException("应用对象已失效；固定快照仍保留供修复或移除。");
                if (item.BranchInputs.Values.Any(id => !project.Lyrics.Branches.Any(branch => branch.Id == id))) throw new ArgumentException("预设的订阅输入分支已失效。");
                foreach (var identity in item.Identities)
                    if (!PresetApplicationTargets.Exists(project, identity.Binding)) throw new ArgumentException("预设的相对成员绑定已失效。");
                foreach (var package in item.Snapshot.Packages) _ = project.Effects.GetPackage(package.PackageId, package.Revision);
            }
            catch (Exception error) when (error is ArgumentException or EffectPackageException)
            { Add(issues, DiagnosticCode.InvalidEffectInstance, severity, $"预设「{item.Snapshot.Name}」不可应用：{error.Message}", "presetApplications." + item.Id); }
        }
    }
}
