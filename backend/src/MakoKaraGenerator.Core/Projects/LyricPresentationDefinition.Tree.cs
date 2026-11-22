using System.Collections.Immutable;
using MakoKaraGenerator.Core.Lyrics;

namespace MakoKaraGenerator.Core.Projects;

/// <summary>独立通用容器树、显示程序及源行覆盖；帧几何不写盘。</summary>
public sealed record LyricPresentationDefinition
{
    /// <summary>未配置展示树。</summary>
    public static LyricPresentationDefinition Empty { get; } = new();
    /// <summary>唯一的静态容器定义。</summary>
    public ImmutableArray<SceneContainerDefinition> Nodes { get; init; } = [];
    /// <summary>源分支的显示程序。</summary>
    public ImmutableArray<LyricDisplayProgram> Programs { get; init; } = [];
    /// <summary>源行局部展示覆盖。</summary>
    public ImmutableArray<LyricLinePresentation> LineOverrides { get; init; } = [];
    /// <summary>现有窗口使用的临时分支视图，不持久化。</summary>
    public ImmutableArray<LyricBranchPresentation> Branches => [.. Programs.Select(program => Resolve(program.BranchId))];
    /// <summary>读取原生节点。</summary>
    public SceneContainerDefinition? FindNode(string id) => Nodes.FirstOrDefault(node => node.Id == id);
    /// <summary>读取源分支的显示程序。</summary>
    public LyricDisplayProgram? FindProgram(string branchId) => Programs.FirstOrDefault(program => program.BranchId == branchId);
    /// <summary>读取源行覆盖。</summary>
    public LyricLinePresentation? FindLine(string lineId) => LineOverrides.FirstOrDefault(line => line.LineId == lineId);
    /// <summary>把原生树投影给现有窗口。</summary>
    public LyricBranchPresentation Resolve(string branchId)
    {
        if (FindProgram(branchId) is not { } program) return new() { BranchId = branchId };
        var root = FindNode(program.RootContainerId) ?? new() { Id = program.RootContainerId };
        var layout = FindNode(program.DefaultContainerId)?.Layout ?? new();
        return new() { BranchId = branchId, Placement = root.Placement, ReferenceRect = root.ReferenceRect,
            AnimationPolicy = program.AnimationPolicy, DefaultPlacement = layout.DefaultPlacement,
            SafeArea = layout.SafeArea, MaxWidthRatio = layout.MaxWidthRatio,
            SlotSequenceProfile = layout.SlotAnimation?.ToProfile(layout.SlotIds.Where(id => FindNode(id) is not null).Select(id => FindNode(id)!)),
            Lines = [.. LineOverrides.Where(line => line.BranchId == branchId)] };
    }
    /// <summary>读取指定显示容器的内部布局；位置与源行覆盖仍来自共同程序。</summary>
    public LyricBranchPresentation ResolveDisplay(string branchId, string containerId)
    {
        var view = Resolve(branchId);
        var layout = FindNode(containerId)?.Layout ?? new LyricContainerLayout();
        return view with { DefaultPlacement = layout.DefaultPlacement, SafeArea = layout.SafeArea,
            MaxWidthRatio = layout.MaxWidthRatio,
            SlotSequenceProfile = layout.SlotAnimation?.ToProfile(layout.SlotIds.Select(id => FindNode(id)!)) };
    }
    /// <summary>通过现有窗口更新原生节点；不保存第二份分支配置。</summary>
    public LyricPresentationDefinition SetBranch(LyricBranchPresentation branch) => UpsertBranch(branch, false);
    /// <summary>建立默认或测试展示树。</summary>
    public static LyricPresentationDefinition FromBranches(IReadOnlyList<LyricBranchPresentation> branches)
    {
        var tree = Empty;
        foreach (var branch in branches) tree = tree.UpsertBranch(branch, true);
        return tree;
    }
    /// <summary>为尚无程序的源分支补默认投影；返回值只由调用方采用。</summary>
    public LyricPresentationDefinition WithDefaultPrograms(LyricsDocument lyrics)
    {
        var tree = this;
        foreach (var branch in lyrics.Branches)
            if (tree.FindProgram(branch.Id) is null) tree = tree.UpsertBranch(new() { BranchId = branch.Id }, true);
        return tree;
    }
    private LyricPresentationDefinition UpsertBranch(LyricBranchPresentation branch, bool ensure)
    {
        var existing = FindProgram(branch.BranchId);
        if ((!ensure || existing is not null) && Resolve(branch.BranchId) == branch) return this;
        var program = existing ?? new() { Id = "program/" + Uri.EscapeDataString(branch.BranchId), BranchId = branch.BranchId,
            RootContainerId = LyricSceneIds.Root(branch.BranchId), DefaultContainerId = LyricSceneIds.Regular(branch.BranchId) };
        var nodes = Nodes;
        void Put(SceneContainerDefinition node)
        {
            int index = Array.FindIndex(nodes.ToArray(), item => item.Id == node.Id);
            nodes = index < 0 ? nodes.Add(node) : nodes.SetItem(index, node);
        }
        var root = FindNode(program.RootContainerId) ?? new() { Id = program.RootContainerId };
        Put(root with { Placement = branch.Placement, ReferenceRect = branch.ReferenceRect });
        var regular = nodes.FirstOrDefault(node => node.Id == program.DefaultContainerId) ?? new() { Id = program.DefaultContainerId, ParentId = root.Id };
        var previousSlots = regular.Layout?.SlotIds ?? [];
        var oldProfile = regular.Layout?.SlotAnimation?.ToProfile(previousSlots.Select(id => FindNode(id)!));
        var slotIds = branch.SlotSequenceProfile is { } profile
            ? profile.Slots.Select(slot =>
            {
                int oldIndex = oldProfile is null ? -1 : Array.FindIndex(oldProfile.Slots.ToArray(), old => old.Id == slot.Id);
                return oldIndex < 0 ? LyricSceneIds.Slot(branch.BranchId, slot.Id) : previousSlots[oldIndex];
            }).ToImmutableArray() : [];
        Put(regular with { Layout = new() { Kind = branch.SlotSequenceProfile is null ? LyricContainerLayoutKind.Auto : LyricContainerLayoutKind.SlotSequence,
            DefaultPlacement = branch.DefaultPlacement, SafeArea = branch.SafeArea, MaxWidthRatio = branch.MaxWidthRatio,
            SlotIds = slotIds, SlotAnimation = branch.SlotSequenceProfile is null ? null : SlotAnimationSpec.FromProfile(branch.SlotSequenceProfile) } });
        if (branch.SlotSequenceProfile is { } slots)
            for (int i = 0; i < slots.Slots.Length; i++)
            {
                var slot = FindNode(slotIds[i]) ?? new SceneContainerDefinition { Id = slotIds[i], ParentId = regular.Id };
                Put(slot with { Placement = slots.Slots[i].Placement });
            }
        int programIndex = Array.FindIndex(Programs.ToArray(), item => item.Id == program.Id);
        program = program with { AnimationPolicy = branch.AnimationPolicy };
        var programs = programIndex < 0 ? Programs.Add(program) : Programs.SetItem(programIndex, program);
        var lines = LineOverrides.Where(line => line.BranchId != branch.BranchId)
            .Concat(branch.Lines.Select(line => line with { BranchId = branch.BranchId })).ToImmutableArray();
        var result = this with { Nodes = nodes, Programs = programs, LineOverrides = lines };
        return result == this ? this : result;
    }
    /// <summary>显式删除源对象时清理程序／片段关联；保留未使用容器，不修正读盘数据。</summary>
    public LyricPresentationDefinition RemoveDeletedSources(LyricsDocument lyrics)
    {
        var programs = Programs.Where(program => lyrics.Branches.Any(branch => branch.Id == program.BranchId)).Select(program =>
        {
            var branch = lyrics.Branches.First(source => source.Id == program.BranchId);
            return program with { Clips = [.. program.Clips.Select(clip => clip with
                { LineIds = [.. clip.LineIds.Where(id => branch.Lines.Any(line => line.Id == id))],
                    ScheduleContext = clip.ScheduleContext is not { } context ? null : context with
                    { LineIds = [.. context.LineIds.Where(id => branch.Lines.Any(line => line.Id == id))],
                        PreviousLineId = branch.Lines.Any(line => line.Id == context.PreviousLineId) ? context.PreviousLineId : null,
                        NextLineId = branch.Lines.Any(line => line.Id == context.NextLineId) ? context.NextLineId : null } }).Where(clip => !clip.LineIds.IsEmpty)],
                LeadIns = [.. program.LeadIns.Where(cue => branch.Lines.Any(line => line.Id == cue.TargetLineId))],
                LeadInGeneration = program.LeadInGeneration is not { } settings ? null : settings with
                    { ExcludedLineIds = [.. settings.ExcludedLineIds.Where(id => branch.Lines.Any(line => line.Id == id))] } };
        }).ToImmutableArray();
        var lines = LineOverrides.Where(line => lyrics.Branches.Any(branch => branch.Id == line.BranchId
            && branch.Lines.Any(source => source.Id == line.LineId))).ToImmutableArray();
        var result = this with { Programs = programs, LineOverrides = lines };
        return result == this ? this : result;
    }
    /// <summary>显式清除行或分支时间时清理失去行时间的片段成员、调度边界与前奏引用；保留容器与单句覆盖。</summary>
    public LyricPresentationDefinition RemoveUntimedReferences(LyricsDocument lyrics)
    {
        var programs = Programs.Select(program =>
        {
            var branch = lyrics.Branches.FirstOrDefault(source => source.Id == program.BranchId);
            if (branch is null) return program;
            var timedIds = branch.Lines
                .Where(line => line.Time is { IsValid: true })
                .Select(line => line.Id)
                .ToHashSet(StringComparer.Ordinal);
            var lineIndexById = new Dictionary<string, int>(branch.Lines.Length, StringComparer.Ordinal);
            for (int i = 0; i < branch.Lines.Length; i++)
                lineIndexById[branch.Lines[i].Id] = i;

            var nextClips = ImmutableArray.CreateBuilder<LyricDisplayClip>(program.Clips.Length);
            foreach (var clip in program.Clips)
            {
                ImmutableArray<string> kept = [.. clip.LineIds.Where(timedIds.Contains)];
                if (kept.IsEmpty) continue;
                bool contiguous = true;
                if (kept.Length != clip.LineIds.Length)
                {
                    if (!lineIndexById.TryGetValue(kept[0], out int firstIndex))
                    {
                        contiguous = false;
                    }
                    else
                    {
                        for (int i = 1; i < kept.Length; i++)
                        {
                            if (!lineIndexById.TryGetValue(kept[i], out int idx) || idx != firstIndex + i)
                            {
                                contiguous = false;
                                break;
                            }
                        }
                    }
                }
                if (!contiguous) continue;

                LyricClipScheduleContext? nextContext = clip.ScheduleContext;
                if (nextContext is { } ctx)
                {
                    bool invalid = ctx.LineIds.Any(id => !timedIds.Contains(id))
                        || (ctx.PreviousLineId is not null && !timedIds.Contains(ctx.PreviousLineId))
                        || (ctx.NextLineId is not null && !timedIds.Contains(ctx.NextLineId));
                    if (invalid || kept.Length != clip.LineIds.Length)
                        nextContext = null;
                }

                nextClips.Add(kept.SequenceEqual(clip.LineIds) && nextContext == clip.ScheduleContext
                    ? clip
                    : clip with { LineIds = kept, ScheduleContext = nextContext });
            }

            ImmutableArray<LyricLeadIn> nextLeadIns = [.. program.LeadIns.Where(cue => timedIds.Contains(cue.TargetLineId))];
            return program with { Clips = nextClips.ToImmutable(), LeadIns = nextLeadIns };
        }).ToImmutableArray();
        var result = this with { Programs = programs };
        return result == this ? this : result;
    }
    /// <summary>按容器、程序及覆盖内容比较。</summary>
    public bool Equals(LyricPresentationDefinition? other) => other is not null && Nodes.SequenceEqual(other.Nodes)
        && Programs.SequenceEqual(other.Programs) && LineOverrides.SequenceEqual(other.LineOverrides);
    /// <inheritdoc/>
    public override int GetHashCode()
    {
        HashCode hash = new(); foreach (var node in Nodes) hash.Add(node); foreach (var program in Programs) hash.Add(program);
        foreach (var line in LineOverrides) hash.Add(line); return hash.ToHashCode();
    }
}
