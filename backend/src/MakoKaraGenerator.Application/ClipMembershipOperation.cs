using MakoKaraGenerator.Core.Diagnostics;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Application;

/// <summary>片段完整成员事务；失败返回原工程及稳定原因。</summary>
public sealed record ClipMembershipError(string Code, string Detail);

/// <summary>现有片段扩缩、拆分、合并的纯候选；不维护第二套历史。</summary>
public static class ClipMembershipOperation
{
    /// <summary>保留片段身份与上下文；上下文不能包含新成员时明确拒绝。</summary>
    public static bool TrySetMembers(Project source, string branchId, string clipId, IReadOnlyList<string> lineIds,
        out Project result, out ClipMembershipError? error)
    {
        result = source;
        if (!TrySource(source, branchId, out var branch, out var tree, out var program, out error)) return false;
        var clip = program!.Clips.FirstOrDefault(item => item.Id == clipId);
        if (clip is null) return Fail(source, "TargetStale", "展示片段不存在。", out result, out error);
        if (!Range(branch!, lineIds, out string failure)) return Fail(source, failure, "成员必须是来源中按序排列的唯一连续有效行。", out result, out error);
        if (clip.LineIds.SequenceEqual(lineIds)) { error = null; return true; }
        if (clip.ScheduleContext is { } context && lineIds.Any(id => !context.LineIds.Contains(id)))
            return Fail(source, "ScheduleContextConflict", "新增成员不属于片段保留的调度上下文。", out result, out error);
        if (source.PresetApplications.Items.Any(app => Touches(app.Target, [clipId]) && app.Snapshot.EffectRecipes.Length != 0))
            return Fail(source, "ReferenceConflict", "含相对效果配方的片段应用不能隐式改变成员身份。", out result, out error);
        return Validate(source, branchId, tree!, program! with { Clips = program.Clips.SetItem(program.Clips.IndexOf(clip), clip with { LineIds = [.. lineIds] }) }, out result, out error);
    }

    /// <summary>旧身份保留左段，新身份用于右段；共享原调度上下文并重映射固定引用。</summary>
    public static bool TrySplit(Project source, string branchId, string clipId, string splitBeforeLineId, string newClipId,
        out Project result, out ClipMembershipError? error)
    {
        result = source;
        if (!TrySource(source, branchId, out var branch, out var tree, out var program, out error)) return false;
        var clip = program!.Clips.FirstOrDefault(item => item.Id == clipId);
        if (clip is null) return Fail(source, "TargetStale", "展示片段不存在。", out result, out error);
        if (string.IsNullOrWhiteSpace(newClipId) || tree!.Programs.SelectMany(item => item.Clips).Any(item => item.Id == newClipId))
            return Fail(source, "ClipExists", "拆分的新片段身份必须未被占用。", out result, out error);
        int split = clip.LineIds.IndexOf(splitBeforeLineId);
        if (split < 1) return Fail(source, "InvalidRange", "拆分点必须是当前片段中除首行以外的成员。", out result, out error);
        var context = clip.ScheduleContext ?? new LyricClipScheduleContext { LineIds = clip.LineIds };
        LyricDisplayClip[] parts = [clip with { LineIds = [.. clip.LineIds.Take(split)], ScheduleContext = context },
            clip with { Id = newClipId, LineIds = [.. clip.LineIds.Skip(split)], ScheduleContext = context }];
        var next = program with { Clips = [.. program.Clips.SelectMany(item => item.Id == clipId ? parts : [item])] };
        if (!Validate(source, branchId, tree, next, out var candidate, out error)) return false;
        if (!ClipSplitReferences.TryRemap(candidate, new Dictionary<string, LyricDisplayClip[]> { [clipId] = parts }, out candidate, out var remapError))
            return Fail(source, "ReferenceConflict", remapError?.Detail ?? "片段引用或范围配方无法完整拆分。", out result, out error);
        // Both fragments use the original schedule, including original slot indices and clipping clocks.
        try
        {
            var before = LyricDisplayScheduleBuilder.Build(branch!, program, tree).ToDictionary(item => item.Line.Id);
            var after = LyricDisplayScheduleBuilder.Build(branch!, next, candidate.LyricPresentation).ToDictionary(item => item.Line.Id);
            if (before.Count != after.Count || before.Any(pair => !after.TryGetValue(pair.Key, out var item)
                || item.SlotIndex != pair.Value.SlotIndex || item.RevealTimeUs != pair.Value.RevealTimeUs || item.HideTimeUs != pair.Value.HideTimeUs))
                return Fail(source, "ClipScheduleConflict", "拆分不能改变原成员的槽位或显示时钟。", out result, out error);
        }
        catch (InvalidOperationException conflict) { return Fail(source, "ClipScheduleConflict", conflict.Message, out result, out error); }
        result = candidate; error = null; return true;
    }

    /// <summary>同容器同上下文的相邻片段合并；预设绑定冲突拒绝，直接效果片段集合完整重映射。</summary>
    public static bool TryMerge(Project source, string branchId, IReadOnlyList<string> clipIds, string targetClipId,
        out Project result, out ClipMembershipError? error)
    {
        result = source;
        if (!TrySource(source, branchId, out var branch, out var tree, out var program, out error)) return false;
        if (clipIds.Count < 2 || clipIds.Distinct(StringComparer.Ordinal).Count() != clipIds.Count || !clipIds.Contains(targetClipId))
            return Fail(source, "InvalidRange", "合并需要至少两个唯一片段，且保留身份必须是其中之一。", out result, out error);
        var clips = clipIds.Select(id => program!.Clips.FirstOrDefault(item => item.Id == id)).ToArray();
        if (clips.Any(clip => clip is null)) return Fail(source, "TargetStale", "合并片段不存在或不属于当前来源。", out result, out error);
        var target = clips.Single(clip => clip!.Id == targetClipId)!;
        if (clips.Any(clip => clip!.ContainerId != target.ContainerId)) return Fail(source, "ContainerInvalid", "合并片段必须使用同一容器。", out result, out error);
        if (clips.Any(clip => clip!.ScheduleContext != target.ScheduleContext)) return Fail(source, "ScheduleContextConflict", "不同调度上下文不能合并。", out result, out error);
        var ids = clips.SelectMany(clip => clip!.LineIds).ToHashSet(StringComparer.Ordinal);
        string[] lines = [.. branch!.Lines.Where(line => ids.Contains(line.Id)).Select(line => line.Id)];
        if (lines.Length != clips.Sum(clip => clip!.LineIds.Length) || !Range(branch, lines, out _))
            return Fail(source, "DiscontiguousRange", "合并后的成员必须唯一且连续。", out result, out error);
        // A clip preset is one immutable snapshot/identity owner. Combining its scope with another
        // clip cannot silently choose an appearance or drop fixed recipe identities.
        var applications = source.PresetApplications.Items.Where(app => Touches(app.Target, clipIds)).ToArray();
        if (source.PresetApplications.Items.Any(app => app.Identities.Any(identity => Touches(identity.Binding, clipIds))))
            return Fail(source, "ReferenceConflict", "合并范围存在固定效果配方身份。", out result, out error);
        if (applications.Length != 0)
        {
            var retained = applications.FirstOrDefault(app => app.Target.Ids[0] == targetClipId);
            if (applications.Length != clipIds.Count || retained is null || applications.Any(app =>
                app.Snapshot.EffectRecipes.Length != 0 || app.Identities.Length != 0 || app.Snapshot.Packages.Length != 0
                || !SourceMaterialCatalog.Collect(app.Snapshot.Style).IsEmpty || !app.Snapshot.Equals(retained.Snapshot)
                || app.Enabled != retained.Enabled || !app.BranchInputs.SequenceEqual(retained.BranchInputs)))
                return Fail(source, "ReferenceConflict", "合并要求全部片段具有相同纯静态预设应用，不能扩大部分应用或选择冲突外观。", out result, out error);
        }
        var next = program! with { Clips = [.. program!.Clips.Where(clip => !clipIds.Contains(clip.Id) || clip.Id == targetClipId)
            .Select(clip => clip.Id == targetClipId ? target with { LineIds = [.. lines] } : clip)] };
        if (!Validate(source, branchId, tree!, next, out var candidate, out error)) return false;
        var instances = source.Effects.Instances.Select(instance => !Touches(instance.Binding, clipIds) ? instance
            : instance with { Binding = new(ProjectEffectBindingKind.Clips,
                ids: instance.Binding.Ids.Select(id => clipIds.Contains(id) ? targetClipId : id).Distinct(StringComparer.Ordinal)) });
        result = PresetApplicationsEditor.Replace(candidate with { Effects = new(source.Effects.Packages, instances, source.Effects.Budgets) },
            new(source.PresetApplications.Items.Where(app => !Touches(app.Target, clipIds) || app.Target.Ids[0] == targetClipId)));
        error = null; return true;
    }

    private static bool Touches(ProjectEffectBinding binding, IReadOnlyList<string> ids) => binding.Kind == ProjectEffectBindingKind.Clips && binding.Ids.Any(ids.Contains);

    private static bool TrySource(Project source, string branchId, out LyricBranch? branch, out LyricPresentationDefinition? tree,
        out LyricDisplayProgram? program, out ClipMembershipError? error)
    {
        branch = source.Lyrics.Branches.FirstOrDefault(item => item.Id == branchId);
        tree = source.LyricPresentation.WithDefaultPrograms(source.Lyrics); program = tree.FindProgram(branchId);
        error = branch is null || program is null ? new("SourceMissing", "来源分支或展示程序不存在。")
            : branch.TimingMode != LyricBranchTimingMode.Independent || (program.AnimationPolicy ?? (branch.Role == LyricBranchRole.Primary
                ? LyricBranchAnimationPolicy.Karaoke : LyricBranchAnimationPolicy.Static)) != LyricBranchAnimationPolicy.Karaoke
                ? new("UnsupportedSource", "片段仅支持独立计时的逐字歌词来源。") : null;
        return error is null;
    }
    private static bool Range(LyricBranch branch, IReadOnlyList<string> ids, out string failure)
    {
        failure = "InvalidRange";
        if (ids.Count == 0 || ids.Any(string.IsNullOrWhiteSpace) || ids.Distinct(StringComparer.Ordinal).Count() != ids.Count) return false;
        var indices = ids.Select(id => Array.FindIndex(branch.Lines.ToArray(), line => line.Id == id)).ToArray();
        if (indices.Any(index => index < 0) || indices.Any(index => branch.Lines[index].Time is not { IsValid: true })) return false;
        failure = "DiscontiguousRange";
        return indices.SequenceEqual(Enumerable.Range(indices[0], indices.Length));
    }
    private static bool Validate(Project source, string branchId, LyricPresentationDefinition tree, LyricDisplayProgram program,
        out Project result, out ClipMembershipError? error)
    {
        if (PresentationEditor.TrySetTree(source, tree with { Programs = tree.Programs.SetItem(tree.Programs.IndexOf(tree.FindProgram(branchId)!), program) }, out result, out var report))
        { error = null; return true; }
        return Fail(source, report.Contains(DiagnosticCode.PresentationTimeConflict) ? "ClipScheduleConflict" : "TargetConflict", "片段候选未通过完整展示校验。", out result, out error);
    }
    private static bool Fail(Project source, string code, string detail, out Project result, out ClipMembershipError? error)
    { result = source; error = new(code, detail); return false; }
}
