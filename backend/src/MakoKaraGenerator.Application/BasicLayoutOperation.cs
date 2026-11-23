using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Diagnostics;
using MakoKaraGenerator.Core.Lyrics;

namespace MakoKaraGenerator.Application;

/// <summary>完整来源和结果及影响计数；失败和无变化保持原工程引用。</summary>
public sealed record BasicLayoutPreview(Project Source, Project Result, int AffectedLines, int ExistingClips,
    int PreservedNodes, int SplitClips, string[] NewClipIds);

/// <summary>布局与精确片段拆分在同一工程事务中预演；失败不返回部分修改。</summary>
public static class BasicLayoutOperation
{
    /// <summary>仅完整匹配固定几何、调度和节点条件时识别布局。</summary>
    public static BasicLayoutPreset? Recognize(LyricPresentationDefinition tree, string containerId)
    {
        var node = tree.FindNode(containerId);
        if (node?.Layout is not { Kind: LyricContainerLayoutKind.SlotSequence } layout || layout.SlotIds.Length is < 1 or > 3
            || node.Placement != ScenePlacement.Default || node.ReferenceRect != SceneReferenceRect.Canvas || node.Content is not null || node.Attachment is not null
            || tree.Nodes.Count(child => child.ParentId == node.Id) != layout.SlotIds.Length
            || layout != new LyricContainerLayout { Kind = LyricContainerLayoutKind.SlotSequence, SlotIds = layout.SlotIds, SlotAnimation = new() { MaxVisibleLines = layout.SlotIds.Length } }) return null;
        foreach (var preset in Enum.GetValues<BasicLayoutPreset>())
        {
            var placements = BasicLayoutPresets.Placements(preset);
            if (placements.Length != layout.SlotIds.Length) continue;
            if (layout.SlotIds.Select((id, index) => (Slot: tree.FindNode(id), Index: index)).All(item =>
                item.Slot?.ParentId == node.Id && item.Slot.Layout is null && item.Slot.Content is null && item.Slot.Attachment is null
                && item.Slot.ReferenceRect == SceneReferenceRect.Canvas && item.Slot.Placement == placements[item.Index])) return preset;
        }
        return null;
    }

    /// <summary>精确成员布局变换与引用映射在一个预演中完成，不返回部分修改。</summary>
    public static bool TryPreview(Project source, string branchId, BasicLayoutPreset preset, IReadOnlyList<string>? selectedLineIds,
        BasicLayoutNames names, out BasicLayoutPreview preview, out BasicLayoutError? error)
    {
        preview = new(source, source, 0, 0, source.LyricPresentation.Nodes.Length, 0, []);
        error = null;
        if (!Enum.IsDefined(preset)) { error = new(BasicLayoutErrorCode.InvalidPreset); return false; }
        var branch = source.Lyrics.Branches.FirstOrDefault(item => item.Id == branchId);
        var tree = source.LyricPresentation.WithDefaultPrograms(source.Lyrics);
        var program = tree.FindProgram(branchId);
        if (branch is null || program is null) { error = new(BasicLayoutErrorCode.SourceMissing); return false; }
        // SlotSequence 是逐字播放的轮换布局；FollowPrimary／Static 来源（例如翻译）
        // 不能安全地拆成独立片段。先给出明确的领域拒绝，避免最终候选树才返回
        // 不透明的 PresentationInvalid。
        LyricBranchAnimationPolicy policy = program.AnimationPolicy ?? (branch.Role == LyricBranchRole.Primary
            ? LyricBranchAnimationPolicy.Karaoke : LyricBranchAnimationPolicy.Static);
        if (branch.TimingMode != LyricBranchTimingMode.Independent || policy != LyricBranchAnimationPolicy.Karaoke)
        {
            error = new(BasicLayoutErrorCode.UnsupportedSource)
            {
                Detail = "快速布局仅支持独立计时的逐字歌词来源；FollowPrimary／静态来源请使用普通布局。",
            };
            return false;
        }
        HashSet<string> requested = selectedLineIds is null ? [.. branch.Lines.Select(line => line.Id)] : [.. selectedLineIds];
        var ordered = branch.Lines.Where(line => requested.Contains(line.Id)).ToArray();
        if (selectedLineIds is not null && (requested.Count == 0 || requested.Count != selectedLineIds.Count || ordered.Length != requested.Count))
        { error = new(BasicLayoutErrorCode.InvalidRange); return false; }
        if (selectedLineIds is not null && !selectedLineIds.SequenceEqual(ordered.Select(line => line.Id), StringComparer.Ordinal))
        { error = new(BasicLayoutErrorCode.DiscontiguousRange); return false; }
        if (selectedLineIds is not null && branch.Lines.IndexOf(ordered[^1]) - branch.Lines.IndexOf(ordered[0]) + 1 != ordered.Length)
        { error = new(BasicLayoutErrorCode.DiscontiguousRange); return false; }
        var touched = program.Clips.Where(clip => clip.LineIds.Any(requested.Contains)).ToArray();
        bool unassigned = requested.Any(id => !program.Clips.Any(clip => clip.LineIds.Contains(id)));
        if (touched.All(clip => Recognize(tree, clip.ContainerId) == preset)
            && ((!unassigned && selectedLineIds is not null) || Recognize(tree, program.DefaultContainerId) == preset))
        { preview = preview with { AffectedLines = requested.Count }; return true; }

        ArgumentNullException.ThrowIfNull(names);
        var placements = BasicLayoutPresets.Placements(preset);
        if (names.SlotNames.IsDefault || names.SlotNames.Length < placements.Length)
            throw new ArgumentException("Every new slot requires an explicit name.", nameof(names));
        string containerId = NewId("node/");
        var slots = placements.Select((placement, index) => new SceneContainerDefinition
        { Id = NewId("node/"), Name = names.SlotNames[index], ParentId = containerId, Placement = placement }).ToArray();
        var container = new SceneContainerDefinition { Id = containerId, Name = names.LayoutName, ParentId = program.RootContainerId,
            Layout = new() { Kind = LyricContainerLayoutKind.SlotSequence, SlotIds = [.. slots.Select(slot => slot.Id)],
                SlotAnimation = new() { MaxVisibleLines = slots.Length } } };
        List<LyricDisplayClip> clips = [];
        Dictionary<string, LyricDisplayClip[]> splits = new(StringComparer.Ordinal);
        HashSet<string> candidateReplacedContainers = new(StringComparer.Ordinal);
        if (selectedLineIds is null) candidateReplacedContainers.Add(program.DefaultContainerId);
        foreach (var clip in program.Clips)
        {
            if (!touched.Contains(clip) || Recognize(tree, clip.ContainerId) == preset) { clips.Add(clip); continue; }
            if (clip.LineIds.All(requested.Contains))
            {
                candidateReplacedContainers.Add(clip.ContainerId);
                clips.Add(clip with { ContainerId = containerId, ScheduleContext = null });
                continue;
            }
            var context = clip.ScheduleContext ?? new LyricClipScheduleContext { LineIds = clip.LineIds };
            var runs = Runs(clip.LineIds, id => requested.Contains(id) ? 1 : 0);
            int retained = Array.FindIndex(runs, run => !requested.Contains(run[0]));
            var fragments = runs.Select((run, index) => clip with { Id = index == retained ? clip.Id : NewId("clip/"), LineIds = [.. run],
                ContainerId = requested.Contains(run[0]) ? containerId : clip.ContainerId,
                ScheduleContext = requested.Contains(run[0]) ? null : context }).ToArray();
            clips.AddRange(fragments); splits.Add(clip.Id, fragments);
        }
        if (selectedLineIds is not null)
            foreach (var segment in LyricDisplaySegmentResolver.Segments(branch, program).Where(segment => segment.Clip is null
                && segment.Lines.Any(line => requested.Contains(line.Id)) && Recognize(tree, program.DefaultContainerId) != preset))
            {
                var members = segment.Lines.ToDictionary(line => line.Id);
                var context = new LyricClipScheduleContext { LineIds = [.. segment.Lines.Select(line => line.Id)], IsDefaultSegment = true,
                    PreviousLineId = program.Clips.FirstOrDefault(clip => LyricDisplaySegmentResolver.Range(branch, clip).EndUs == segment.StartUs)?.LineIds[^1],
                    NextLineId = program.Clips.FirstOrDefault(clip => LyricDisplaySegmentResolver.Range(branch, clip).StartUs == segment.EndUs)?.LineIds[0] };
                foreach (var run in Runs(segment.Lines.Select(line => line.Id).ToArray(), id => requested.Contains(id) ? 1
                    : members[id].Time is { IsValid: true } ? 0 : -1))
                {
                    bool selected = requested.Contains(run[0]);
                    if (!selected && members[run[0]].Time is not { IsValid: true }) continue;
                    clips.Add(new() { Id = NewId("clip/"), Name = selected ? names.SelectedSegmentName : names.RetainedSegmentName,
                        ContainerId = selected ? containerId : program.DefaultContainerId, LineIds = [.. run], ScheduleContext = selected ? null : context });
                }
            }
        var next = program with { DefaultContainerId = selectedLineIds is null ? containerId : program.DefaultContainerId, Clips = [.. clips] };
        var proposed = tree with { Nodes = tree.Nodes.Add(container).AddRange(slots), Programs = tree.Programs.SetItem(tree.Programs.IndexOf(program), next) };
        if (!PresentationEditor.TrySetTree(source, proposed, out Project result, out ValidationReport report))
        { error = new(BasicLayoutErrorCode.PresentationInvalid) { Validation = report }; return false; }
        HashSet<string> activeContainers = new(StringComparer.Ordinal);
        foreach (var item in proposed.Programs)
        {
            activeContainers.Add(item.DefaultContainerId);
            foreach (var clip in item.Clips) activeContainers.Add(clip.ContainerId);
        }
        var nodeRemaps = candidateReplacedContainers
            .Where(oldId => oldId != containerId && !activeContainers.Contains(oldId))
            .ToDictionary(oldId => oldId, _ => containerId, StringComparer.Ordinal);
        if (!ClipSplitReferences.TryRemap(result, splits, nodeRemaps, out result, out error)) return false;
        if (selectedLineIds is not null)
        {
            // 校验上下文投影确实保留原调度，而不是只保留容器引用。
            try
            {
                var before = LyricDisplayScheduleBuilder.Build(branch, program, tree).Where(item => !requested.Contains(item.Line.Id))
                    .ToDictionary(item => item.Line.Id);
                var after = LyricDisplayScheduleBuilder.Build(branch, next, proposed).Where(item => !requested.Contains(item.Line.Id))
                    .ToDictionary(item => item.Line.Id);
                if (before.Count != after.Count || before.Any(pair => !after.TryGetValue(pair.Key, out var item) || item.SlotIndex != pair.Value.SlotIndex
                    || item.Segment.ContainerId(next) != pair.Value.Segment.ContainerId(program)
                    || item.RevealTimeUs != pair.Value.RevealTimeUs || item.HideTimeUs != pair.Value.HideTimeUs))
                { error = new(BasicLayoutErrorCode.ScheduleConflict); return false; }
            }
            catch (InvalidOperationException conflict) { error = new(BasicLayoutErrorCode.ScheduleFailure) { Detail = conflict.Message }; return false; }
        }
        HashSet<string> sourceClipIds = [.. program.Clips.Select(clip => clip.Id)];
        string[] newClipIds = [.. next.Clips.Where(clip => !sourceClipIds.Contains(clip.Id)).Select(clip => clip.Id)];
        preview = new(source, result, requested.Count, touched.Length, tree.Nodes.Length, splits.Count, newClipIds);
        return true;
    }

    private static string NewId(string prefix) => prefix + Guid.NewGuid().ToString("N");

    private static string[][] Runs(IReadOnlyList<string> ids, Func<string, int> group)
    {
        List<string[]> runs = []; List<string> run = [];
        foreach (string id in ids)
        {
            if (run.Count > 0 && group(run[0]) != group(id)) { runs.Add([.. run]); run.Clear(); }
            run.Add(id);
        }
        if (run.Count > 0) runs.Add([.. run]);
        return [.. runs];
    }
}
