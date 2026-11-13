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
            bool targetExists = PresetApplicationTargets.Exists(project, item.Target);
            if (!targetExists)
                Add(issues, DiagnosticCode.InvalidEffectInstance, IssueSeverity.Warning,
                    $"预设「{item.Snapshot.Name}」的应用对象已不存在，已跳过执行；固定分配仍保留供撤销恢复，可在外观的工程默认中移除。", "presetApplications." + item.Id);
            IssueSeverity severity = item.Enabled ? IssueSeverity.Error : IssueSeverity.Warning;
            try
            {
                foreach (var package in item.Snapshot.Packages) _ = project.Effects.GetPackage(package.PackageId, package.Revision);
                if (!targetExists) continue;
                if (item.BranchInputs.Values.Any(id => !project.Lyrics.Branches.Any(branch => branch.Id == id))) throw new ArgumentException("预设的订阅输入分支已失效。");
                foreach (var identity in item.Identities)
                    if (!PresetApplicationTargets.Exists(project, identity.Binding)) throw new ArgumentException("预设的相对成员绑定已失效。");
            }
            catch (Exception error) when (error is ArgumentException or EffectPackageException)
            { Add(issues, DiagnosticCode.InvalidEffectInstance, severity, $"预设「{item.Snapshot.Name}」不可应用：{error.Message}", "presetApplications." + item.Id); }
        }
    }
}
