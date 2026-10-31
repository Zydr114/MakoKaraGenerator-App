using MakoKaraGenerator.Core.Effects;

namespace MakoKaraGenerator.Core.Projects;

/// <summary>模板套用的只读预演；结果工程仅在用户提交后才进入工作区。</summary>
/// <param name="Result">预演后的工程。</param>
/// <param name="TargetCount">符合本次范围的目标数量。</param>
/// <param name="ChangedCount">覆盖数据实际发生变化的目标数量。</param>
public readonly record struct StyleTemplateApplyPreview(Project Result, int TargetCount, int ChangedCount);

/// <summary>只由歌词行编辑流程调用的模板套用命令。</summary>
public static partial class StyleTemplateApplicator
{
    /// <summary>按稳定行与元素 ID 套用到当前目标；失败时返回原工程。</summary>
    public static bool TryApply(
        Project project, StyleTemplate template, string lineId, string? tokenId,
        out Project result, out string reason)
    {
        bool valid = TryPreview(project, template, lineId, tokenId, all: false,
            out StyleTemplateApplyPreview preview, out reason);
        result = valid ? preview.Result : project;
        return valid;
    }

    /// <summary>预演当前目标或全部同类型目标，失败时不返回部分结果。</summary>
    /// <param name="project">当前工程。</param>
    /// <param name="template">待套用模板。</param>
    /// <param name="lineId">当前行 ID；全部范围可为空。</param>
    /// <param name="tokenId">当前元素 ID；全部范围可为空。</param>
    /// <param name="all">是否套用到工程中的所有同类型目标。</param>
    /// <param name="preview">完整的预演结果和目标统计。</param>
    /// <param name="reason">失败原因。</param>
    public static bool TryPreview(Project project, StyleTemplate template,
        string? lineId, string? tokenId, bool all,
        out StyleTemplateApplyPreview preview, out string reason)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(template);
        preview = new(project, 0, 0);
        reason = string.Empty;
        try { _ = StyleTemplate.Create(template.Id, template.Name, template.Kind, template.Style, template.Properties, template.EffectRecipes, template.Packages); }
        catch (Exception error) when (error is ArgumentException or EffectPackageException) { reason = error.Message; return false; }
        var branches = all ? project.Lyrics.Branches.Where(branch => !branch.Lines.IsEmpty).ToArray()
            : project.Lyrics.Branches.Where(branch => branch.Lines.Any(line => line.Id == lineId)).ToArray();
        if (branches.Length == 0) { reason = "请先选择歌词行。"; return false; }
        Project result = project;
        int count = 0, changed = 0;
        foreach (var branch in branches)
        {
            Project view = result with { Lyrics = result.Lyrics with { PrimaryBranchId = branch.Id } };
            if (all && template.Kind == StyleTemplateKind.Token && !branch.Lines.Any(line => !line.Tokens.IsEmpty)) continue;
            if (!TryPreviewBranch(view, template, lineId, tokenId, all, out var part, out reason)) return false;
            count += part.TargetCount; changed += part.ChangedCount;
            result = MakoKaraGenerator.Core.Lyrics.LyricBranchEditor.Edit(result, branch.Id, _ => part.Result);
        }
        if (count == 0) { reason = "工程中没有歌词元素。"; return false; }
        try
        {
            var owners = Owners(project, template.Kind, lineId, tokenId, all);
            result = ApplyRecipes(result, template, owners);
            changed = owners.Count(owner => OwnerChanged(project, result, template.Id, owner));
        }
        catch (Exception error) when (error is ArgumentException or EffectPackageException) { reason = error.Message; return false; }
        preview = new(result, count, changed);
        return true;
    }

    private static bool TryPreviewBranch(Project project, StyleTemplate template,
        string? lineId, string? tokenId, bool all,
        out StyleTemplateApplyPreview preview, out string reason)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(template);
        preview = new StyleTemplateApplyPreview(project, 0, 0);
        reason = string.Empty;

        if (template.Kind is not (StyleTemplateKind.Line or StyleTemplateKind.Token))
        {
            reason = "模板类型不受支持。";
            return false;
        }
        StyleTargetLevel level = template.Kind == StyleTemplateKind.Line
            ? StyleTargetLevel.Line : StyleTargetLevel.Token;
        List<StyleTarget> targets = [];
        if (all)
        {
            for (int lineIndex = 0; lineIndex < project.Lyrics.Lines.Length; lineIndex++)
            {
                if (level == StyleTargetLevel.Line)
                    targets.Add(new StyleTarget(lineIndex));
                else
                    for (int tokenIndex = 0;
                        tokenIndex < project.Lyrics.Lines[lineIndex].Tokens.Length; tokenIndex++)
                        targets.Add(new StyleTarget(lineIndex, tokenIndex));
            }
        }
        else
        {
            int lineIndex = -1;
            for (int index = 0; index < project.Lyrics.Lines.Length; index++)
                if (project.Lyrics.Lines[index].Id == lineId) { lineIndex = index; break; }
            if (lineIndex < 0)
            {
                reason = lineId is null ? "请先选择歌词行。" : $"找不到歌词行 {lineId}。";
                return false;
            }

            int tokenIndex = -1;
            if (level == StyleTargetLevel.Token)
            {
                if (tokenId is null)
                {
                    reason = "请先选择一个元素。";
                    return false;
                }
                for (int index = 0; index < project.Lyrics.Lines[lineIndex].Tokens.Length; index++)
                    if (project.Lyrics.Lines[lineIndex].Tokens[index].Id == tokenId)
                    { tokenIndex = index; break; }
                if (tokenIndex < 0)
                {
                    reason = $"找不到元素 {tokenId}。";
                    return false;
                }
            }
            targets.Add(new StyleTarget(lineIndex, tokenIndex));
        }

        if (targets.Count == 0)
        {
            reason = level == StyleTargetLevel.Line ? "工程中没有歌词行。" : "工程中没有歌词元素。";
            return false;
        }

        Project result;
        try
        {
            result = template.Properties.IsEmpty ? project : StyleBatchEditor.Apply(project, level, targets,
                template.Style, template.Properties);
        }
        catch (ArgumentException exception)
        {
            reason = exception.Message;
            return false;
        }

        int changedCount = 0;
        foreach (StyleTarget target in targets)
        {
            LyricStyleOverride? before = level == StyleTargetLevel.Line
                ? project.Lyrics.Lines[target.LineIndex].StyleOverride
                : project.Lyrics.Lines[target.LineIndex].Tokens[target.TokenIndex].StyleOverride;
            LyricStyleOverride? after = level == StyleTargetLevel.Line
                ? result.Lyrics.Lines[target.LineIndex].StyleOverride
                : result.Lyrics.Lines[target.LineIndex].Tokens[target.TokenIndex].StyleOverride;
            if (!Equals(before, after)) changedCount++;
        }
        preview = new StyleTemplateApplyPreview(result, targets.Count, changedCount);
        return true;
    }
}
