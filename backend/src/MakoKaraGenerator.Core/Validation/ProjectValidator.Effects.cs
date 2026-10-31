using MakoKaraGenerator.Core.Diagnostics;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Projects;
using static MakoKaraGenerator.Core.Diagnostics.DiagnosticList;

namespace MakoKaraGenerator.Core.Validation;

public static partial class ProjectValidator
{
    private static void ValidateEffects(Project project, List<ProjectIssue> issues)
    {
        Dictionary<(string, string), EffectPackageDefinition> definitions = [];
        foreach (var package in project.Effects.Packages)
        {
            try { definitions.Add((package.PackageId, package.Revision), EffectPackageDefinition.Read(package)); }
            catch (EffectPackageException error)
            { Add(issues, DiagnosticCode.InvalidEffectInstance, IssueSeverity.Warning, $"效果包语义无效：{error.Message}", "effects.packages." + package.Revision + "." + error.Path); }
        }
        foreach (var reference in SourceMaterialCatalog.Collect(project))
        {
            try
            {
                if (!definitions.TryGetValue((reference.Material.PackageId, reference.Material.Revision), out var definition))
                    throw new EffectPackageException("SourceMaterialPackageInvalid", reference.Path, "源材质没有有效固定包修订。");
                SourceMaterialCatalog.Validate(reference, definition);
            }
            catch (EffectPackageException error)
            { Add(issues, DiagnosticCode.InvalidEffectInstance, IssueSeverity.Error, $"源材质不可执行：{error.Message}", reference.Path + ":" + error.Path); }
        }
        var branches = project.Lyrics.Branches.Select(b => b.Id).ToHashSet(StringComparer.Ordinal);
        var lines = project.Lyrics.AllLines.Select(l => l.Id).ToHashSet(StringComparer.Ordinal);
        var tokens = project.Lyrics.AllLines.SelectMany(l => l.Tokens).Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        var clips = project.LyricPresentation.Programs.SelectMany(p => p.Clips).Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        var nodes = project.LyricPresentation.Nodes.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        for (int index = 0; index < project.Effects.Instances.Length; index++)
        {
            var instance = project.Effects.Instances[index]; var binding = instance.Binding;
            string path = $"effects.instances[{index}]"; IssueSeverity severity = instance.Enabled ? IssueSeverity.Error : IssueSeverity.Warning;
            try
            {
                if (!definitions.TryGetValue((instance.PackageId, instance.Revision), out var definition))
                    throw new EffectPackageException("EffectPackageInvalid", path, "实例没有完整、语义有效的固定包修订。");
                var export = definition.GetExport(instance.ExportId);
                var allowed = export switch { EffectControllerDefinition controller => controller.BindingKinds, EffectGraphDefinition graph => graph.BindingKinds, _ => [] };
                if (!allowed.Contains(ProjectEffectsJson.KindName(binding.Kind), StringComparer.Ordinal))
                    throw new EffectPackageException("EffectBindingUnsupported", path + ".binding", "导出不允许该绑定类型。");
                _ = definition.ResolveParameters(instance.ExportId, instance.Parameters.Encode());
                if (instance.OverlayLayer != EffectOverlayLayer.Package && !export.Outputs.Any(output => output.Kind == "Overlay"))
                    throw new EffectPackageException("EffectOverlayLayerUnsupported", path, "图元层覆盖要求实际Overlay输出。");
                if (instance.TemplateOrigin is { } origin && !(origin.OwnerKind == StyleTemplateKind.Line ? lines.Contains(origin.OwnerId) : tokens.Contains(origin.OwnerId)))
                    throw new EffectPackageException("TemplateOwnerMissing", path, "模板套用所有者已不存在；实例已保留供修复。");
                if (export.Outputs.Any(output => output.Requires.Contains("AudioFeatures", StringComparer.Ordinal)) && project.AudioAnalysis?.Data is null)
                    throw new EffectPackageException("RequiredAudioUnavailable", path, "实例要求完整音频分析；当前无源、未分析或分析失败。");
                bool exists = binding.Kind switch
                {
                    ProjectEffectBindingKind.Project => true,
                    ProjectEffectBindingKind.Branch => branches.Contains(binding.Id!), ProjectEffectBindingKind.Node => nodes.Contains(binding.Id!),
                    ProjectEffectBindingKind.Lines => binding.Ids.All(lines.Contains), ProjectEffectBindingKind.Tokens => binding.Ids.All(tokens.Contains),
                    ProjectEffectBindingKind.Clips => binding.Ids.All(clips.Contains), _ => false,
                };
                if (!exists || instance.Subscription.Kind == "branch" && !branches.Contains(instance.Subscription.BranchId!))
                    throw new EffectPackageException("EffectBindingMissing", path + ".binding", "绑定或订阅引用的源／场景对象不存在；实例已保留。");
            }
            catch (EffectPackageException error)
            { Add(issues, DiagnosticCode.InvalidEffectInstance, severity, $"效果「{instance.Name}」不可执行：{error.Message}", path + ":" + error.Path); }
        }
    }
}
