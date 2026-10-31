using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Lyrics;

namespace MakoKaraGenerator.Core.Projects;

/// <summary>将相对预设能力映射到真实对象，不猜测对象身份或扩大词目标。</summary>
public static class PresetApplicationTargets
{
    /// <summary>结构正确的具体对象当前是否存在。</summary>
    public static bool Exists(Project project, ProjectEffectBinding target)
    {
        ArgumentNullException.ThrowIfNull(project); _ = PresetApplications.ObjectKey(target);
        return target.Kind switch
        {
            ProjectEffectBindingKind.Project => true,
            ProjectEffectBindingKind.Branch => project.Lyrics.Branches.Any(branch => branch.Id == target.Id),
            ProjectEffectBindingKind.Lines => project.Lyrics.AllLines.Any(line => line.Id == target.Ids[0]),
            ProjectEffectBindingKind.Tokens => project.Lyrics.AllLines.Any(line => line.Tokens.Any(token => token.Id == target.Ids[0])),
            ProjectEffectBindingKind.Clips => project.LyricPresentation.Programs.Any(program => program.Clips.Any(clip => clip.Id == target.Ids[0])),
            ProjectEffectBindingKind.Node => project.LyricPresentation.FindNode(target.Id!) is not null,
            _ => false,
        };
    }

    /// <summary>验证外观与相对配方可完整应用；失败不给出部分绑定。</summary>
    public static bool CanApply(Project project, StyleTemplate preset, ProjectEffectBinding target, out string reason)
    {
        ArgumentNullException.ThrowIfNull(preset); reason = string.Empty;
        try
        {
            if (!Exists(project, target)) throw new ArgumentException("应用对象已失效。");
            if (preset.Kind == StyleTemplateKind.Token && target.Kind != ProjectEffectBindingKind.Tokens
                || target.Kind == ProjectEffectBindingKind.Tokens && (preset.Properties.Any(property => !TokenStyleSupport.IsSupported(property))
                    || preset.EffectRecipes.Any(recipe => recipe.Target == TemplateEffectTarget.Line)))
                throw new ArgumentException("所选预设包含不适用于文字目标的属性或相对配方。");
            foreach (var recipe in preset.EffectRecipes)
            {
                var configuration = recipe.ToConfiguration(preset.Packages.Single(p => p.PackageId == recipe.PackageId && p.Revision == recipe.Revision));
                var bindings = Resolve(project, target, recipe.Target);
                if (bindings.Length == 0 || bindings.Any(binding => !configuration.BindingKinds.Contains(ProjectEffectsJson.KindName(binding.Kind), StringComparer.Ordinal)))
                    throw new ArgumentException("所选预设的相对目标没有支持的实际绑定成员。");
            }
            return true;
        }
        catch (Exception error) when (error is ArgumentException or EffectPackageException) { reason = error.Message; return false; }
    }

    /// <summary>工程／分支 Application 配方返回单个范围，不拆分成文字。</summary>
    public static ProjectEffectBinding[] Resolve(Project project, ProjectEffectBinding target, TemplateEffectTarget relative)
    {
        if (!Exists(project, target)) throw new ArgumentException("应用对象已失效。", nameof(target));
        // 相对作用对象不改变工程／分支的实例作用域；节点继续使用真实动态父关系。
        if (relative == TemplateEffectTarget.Application || target.Kind is ProjectEffectBindingKind.Project or ProjectEffectBindingKind.Branch or ProjectEffectBindingKind.Node) return [target];
        if (target.Kind == ProjectEffectBindingKind.Tokens)
            return relative == TemplateEffectTarget.TokenFamily ? [target] : throw new ArgumentException("文字应用不能扩大为整行。", nameof(relative));
        IEnumerable<LyricLine> lines = target.Kind switch
        {
            ProjectEffectBindingKind.Project => project.Lyrics.AllLines,
            ProjectEffectBindingKind.Branch => project.Lyrics.Branches.Single(branch => branch.Id == target.Id).Lines,
            ProjectEffectBindingKind.Lines => project.Lyrics.AllLines.Where(line => line.Id == target.Ids[0]),
            ProjectEffectBindingKind.Clips => ClipLines(project, target.Ids[0]),
            _ => throw new ArgumentException("不支持的相对范围。", nameof(target)),
        };
        return relative switch
        {
            TemplateEffectTarget.Line => [.. lines.Select(line => new ProjectEffectBinding(ProjectEffectBindingKind.Lines, ids: [line.Id]))],
            TemplateEffectTarget.TokenFamily => [.. lines.SelectMany(line => line.Tokens).Select(token => new ProjectEffectBinding(ProjectEffectBindingKind.Tokens, ids: [token.Id]))],
            _ => throw new ArgumentOutOfRangeException(nameof(relative)),
        };
    }

    private static IEnumerable<LyricLine> ClipLines(Project project, string clipId)
    {
        var program = project.LyricPresentation.Programs.Single(item => item.Clips.Any(clip => clip.Id == clipId));
        var members = program.Clips.Single(item => item.Id == clipId).LineIds.ToHashSet(StringComparer.Ordinal);
        return project.Lyrics.Branches.Single(branch => branch.Id == program.BranchId).Lines.Where(line => members.Contains(line.Id));
    }
}
