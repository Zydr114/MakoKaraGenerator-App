using MakoKaraGenerator.Core.Diagnostics;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Core.Validation;

/// <summary>通用容器树及独占显示程序的统一校验；不修正数据。</summary>
public static partial class PresentationValidator
{
    /// <summary>校验节点、父子关系、布局引用、源绑定及独占时间冲突。</summary>
    public static ValidationReport Validate(Project project)
    {
        List<ProjectIssue> issues = [];
        var tree = project.LyricPresentation;
        var nodes = new Dictionary<string, SceneContainerDefinition>(StringComparer.Ordinal);
        void Error(string message, string path, DiagnosticCode code = DiagnosticCode.InvalidDisplayProgram) =>
            DiagnosticList.Add(issues, code, IssueSeverity.Error, message, "project.lyricPresentation." + path);
        foreach (var node in tree.Nodes)
        {
            if (string.IsNullOrWhiteSpace(node.Id) || !nodes.TryAdd(node.Id, node))
                Error("容器 ID 缺失或重复。", "nodes[" + node.Id + "]");
            if (node.Placement is not { IsValid: true } || !node.ReferenceRect.IsValid)
                Error("容器位置或固定参考矩形无效。", "nodes[" + node.Id + "]", DiagnosticCode.InvalidLinePlacement);
        }
        bool Under(string id, string root)
        {
            HashSet<string> visited = new(StringComparer.Ordinal);
            while (nodes.TryGetValue(id, out var node) && visited.Add(id))
            { if (id == root) return true; if (node.ParentId is null) return false; id = node.ParentId; }
            return false;
        }
        foreach (var node in tree.Nodes)
        {
            string path = "nodes[" + node.Id + "]";
            if (node.ParentId is { } parent && (!nodes.ContainsKey(parent) || Under(parent, node.Id)))
                Error("父容器不存在或形成循环。", path + ".parentId");
            if (node.Layout is not { } layout) continue;
            if (!Enum.IsDefined(layout.Kind) || layout.DefaultPlacement is { IsValid: false } || !layout.SafeArea.IsValid
                || !double.IsFinite(layout.MaxWidthRatio) || layout.MaxWidthRatio is <= 0 or > 1)
                Error("歌词内部布局参数无效。", path + ".layout");
            var slots = layout.SlotIds.Where(nodes.ContainsKey).Select(id => nodes[id]).ToArray();
            if (layout.Kind == LyricContainerLayoutKind.SlotSequence)
            {
                if (slots.Length != layout.SlotIds.Length || slots.Length < 1 || layout.SlotIds.Distinct(StringComparer.Ordinal).Count() != slots.Length
                    || slots.Any(slot => slot.ParentId != node.Id || slot.Layout is not null || slot.Content is not null)
                    || layout.SlotAnimation is null || !layout.SlotAnimation.ToProfile(slots).IsValid)
                    Error("槽位必须是有序、唯一的直接子容器，并具有有效调度参数。", path + ".layout.slotIds");
            }
            else if (!layout.SlotIds.IsEmpty || layout.SlotAnimation is not null)
                Error("普通布局不能携带槽位调度。", path + ".layout");
        }
        HashSet<string> programIds = new(StringComparer.Ordinal), sourceIds = new(StringComparer.Ordinal), clipIds = new(StringComparer.Ordinal);
        foreach (var program in tree.Programs)
        {
            string path = "programs[" + program.Id + "]";
            var branch = project.Lyrics.Branches.FirstOrDefault(source => source.Id == program.BranchId);
            if (string.IsNullOrWhiteSpace(program.Id) || !programIds.Add(program.Id) || !sourceIds.Add(program.BranchId) || branch is null
                || program.AnimationPolicy is { } policy && !Enum.IsDefined(policy)
                || !Under(program.DefaultContainerId, program.RootContainerId)
                || !nodes.TryGetValue(program.DefaultContainerId, out var regular) || regular.Layout is null)
            { Error("显示程序的源分支或容器关联无效。", path); continue; }
            var effectivePolicy = program.AnimationPolicy ?? (branch.Role == LyricBranchRole.Primary ? LyricBranchAnimationPolicy.Karaoke : LyricBranchAnimationPolicy.Static);
            if (regular.Layout.Kind == LyricContainerLayoutKind.SlotSequence && effectivePolicy != LyricBranchAnimationPolicy.Karaoke)
                Error("槽位轮换仅用于逐字歌词显示程序。", path);
            HashSet<string> lines = new(StringComparer.Ordinal);
            foreach (var clip in program.Clips)
            {
                string clipPath = path + ".clips[" + clip.Id + "]";
                if (string.IsNullOrWhiteSpace(clip.Id) || !clipIds.Add(clip.Id)
                    || clip.LineIds.Any(id => !lines.Add(id))
                    || !Under(clip.ContainerId, program.RootContainerId)
                    || !nodes.TryGetValue(clip.ContainerId, out var container) || container.Layout is null
                    || branch.TimingMode != LyricBranchTimingMode.Independent || effectivePolicy != LyricBranchAnimationPolicy.Karaoke)
                { Error("片段需要唯一身份、连续有效成员及同展示根下的歌词容器。", clipPath); continue; }
                try
                {
                    var range = LyricDisplaySegmentResolver.Range(branch, clip);
                    _ = LyricDisplaySegmentResolver.ScheduleMembers(branch, new(range.StartUs, range.EndUs, clip, LyricDisplaySegmentResolver.Members(branch, clip)));
                    foreach (var other in branch.Lines)
                        if (!clip.LineIds.Contains(other.Id) && other.Time is { IsValid: true } time
                            && time.StartUs < range.EndUs && range.StartUs < time.EndUs)
                            Error($"片段 '{clip.Id}' 与行 '{other.Id}' 的演唱时间重叠。", clipPath, DiagnosticCode.PresentationTimeConflict);
                    foreach (var other in program.Clips.Where(other => other.Id != clip.Id))
                    {
                        var otherRange = LyricDisplaySegmentResolver.Range(branch, other);
                        if (range.StartUs < otherRange.EndUs && otherRange.StartUs < range.EndUs)
                            Error("展示片段区间重叠。", clipPath, DiagnosticCode.PresentationTimeConflict);
                    }
                }
                catch (InvalidOperationException exception) { Error(exception.Message, clipPath); }
            }
        }
        HashSet<string> overrides = new(StringComparer.Ordinal);
        foreach (var line in tree.LineOverrides)
            if (!overrides.Add(line.LineId) || !project.Lyrics.Branches.Any(branch => branch.Id == line.BranchId && branch.Lines.Any(source => source.Id == line.LineId))
                || line.LocalTransform is { IsValid: false } || line.EnterAnimation is { IsValid: false })
                Error("行覆盖的源关联、位置或动画无效。", "lineOverrides[" + line.LineId + "]", DiagnosticCode.InvalidLinePlacement);
        return ValidationReport.From(issues).Merge(ValidateLeadIns(project));
    }
}
