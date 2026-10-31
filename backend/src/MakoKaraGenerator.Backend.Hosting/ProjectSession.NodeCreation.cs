using System.Globalization;
using System.Text.Json;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Validation;

namespace MakoKaraGenerator.Backend.Hosting;

public sealed partial class ProjectSession
{
    internal static BackendErrorResponse? CheckNodeCreationIntent(ProjectCommandDto command)
    {
        if (command is not (CreateOrchestrationNodeCommandDto or CopyOrchestrationSubtreeCommandDto)) return null;
        if (!EditingCommandTypeMatches(command)) return NodeCreationInvalid();
        if (command.Target is not null || command.StartUs is not null || command.EndUs is not null || command.AnalysisJobId is not null)
            return NodeCreationInvalid();
        string? name = command is CreateOrchestrationNodeCommandDto createName ? createName.Name
            : ((CopyOrchestrationSubtreeCommandDto)command).Name;
        if (name?.Length > OrchestrationNodeCreationLimits.MaxNameLength) return NodeCreationInvalid();
        bool valid = command switch
        {
            CreateOrchestrationNodeCommandDto create => (create.ParentId is null || IsStableId(create.ParentId))
                && (create.Kind is "group" or "auto" && create.SlotCount is null
                    || create.Kind == "slotSequence" && create.SlotCount is >= 1 and <= OrchestrationNodeCreationLimits.MaxSlots),
            CopyOrchestrationSubtreeCommandDto copy => IsStableId(copy.NodeId),
            _ => false,
        };
        if (!valid) return NodeCreationInvalid();
        return JsonSerializer.SerializeToUtf8Bytes(command, ReadModelJson).Length > OrchestrationNodeCreationLimits.MaxIntentBytes
            ? NodeCreationBudget() : null;
    }

    private void TryPrepareNodeCreation(Project before, ProjectCommandDto command, out Project after,
        out OrchestrationStructureCommandResultDto? result, out BackendErrorResponse? error)
    {
        after = before; result = null; error = CheckNodeCreationIntent(command);
        if (error is not null) return;
        try
        {
            // Bound and inspect the saved tree before the compatibility materializer or
            // Core validator can traverse it. In particular, missing slot references can
            // throw while WithDefaultPrograms builds a legacy slot profile.
            GuardNodeCreationTree(before);
            if (PresentationValidator.Validate(before).HasErrors) throw new InvalidNodeCreationTreeException();
            HashSet<string> projectedIds = new(before.LyricPresentation.Nodes.Select(n => n.Id), StringComparer.Ordinal);
            HashSet<string> programBranches = new(before.LyricPresentation.Programs.Select(p => p.BranchId), StringComparer.Ordinal);
            foreach (var branch in before.Lyrics.Branches)
            {
                if (string.IsNullOrWhiteSpace(branch.Id)) throw new InvalidNodeCreationTreeException();
                if (!programBranches.Add(branch.Id)) continue;
                projectedIds.Add(LyricSceneIds.Root(branch.Id)); projectedIds.Add(LyricSceneIds.Regular(branch.Id));
                if (projectedIds.Count > OrchestrationNodeCreationLimits.MaxNodes) throw new ReadModelBudgetExceededException();
            }
            var tree = before.LyricPresentation.WithDefaultPrograms(before.Lyrics);
            Project input = ReferenceEquals(tree, before.LyricPresentation) ? before : before with { LyricPresentation = tree };
            GuardNodeCreationTree(input);
            if (PresentationValidator.Validate(input).HasErrors) throw new InvalidNodeCreationTreeException();
            _ = BuildOrchestration(input, RevisionText);
            HashSet<string> originalIds = new(tree.Nodes.Select(n => n.Id), StringComparer.Ordinal);
            string rootId;
            if (command is CreateOrchestrationNodeCommandDto create)
            {
                if (create.ParentId is { } parentId)
                {
                    var parent = tree.FindNode(parentId);
                    if (parent is null) { error = StructureRejected("父节点不存在。", "command.parentId", "TargetStale"); return; }
                    if (parent is not { Layout: null, Content: null })
                    { error = StructureRejected("创建父节点必须是已有普通组。", "command.parentId", "ContainerInvalid"); return; }
                }
                int slots = create.SlotCount ?? 0;
                if (tree.Nodes.Length + slots + 1 > OrchestrationNodeCreationLimits.MaxNodes) throw new ReadModelBudgetExceededException();
                HashSet<string> reservedIds = new(originalIds, StringComparer.Ordinal);
                rootId = NewId();
                List<SceneContainerDefinition> added = [];
                for (int i = 0; i < slots; i++) added.Add(new() { Id = NewId(), ParentId = rootId });
                var root = new SceneContainerDefinition { Id = rootId, Name = create.Name, ParentId = create.ParentId,
                    Layout = create.Kind switch
                    {
                        "auto" => new LyricContainerLayout(),
                        "slotSequence" => new LyricContainerLayout { Kind = LyricContainerLayoutKind.SlotSequence,
                            SlotIds = [.. added.Select(n => n.Id)], SlotAnimation = new() { MaxVisibleLines = Math.Min(2, slots) } },
                        _ => null,
                    } };
                added.Insert(0, root);
                // Candidate depth and complete input budgets are checked before Core work.
                GuardNodeCreationTree(input with { LyricPresentation = tree with { Nodes = tree.Nodes.AddRange(added) } });
                if (!SceneTreeEditor.TryAddNodes(input, added, out after, out _)) throw new InvalidNodeCreationTreeException();

                string NewId()
                {
                    string id;
                    do { id = "node/" + Guid.NewGuid().ToString("N"); } while (!reservedIds.Add(id));
                    return id;
                }
            }
            else if (command is CopyOrchestrationSubtreeCommandDto copy)
            {
                if (tree.FindNode(copy.NodeId!) is null)
                { error = StructureRejected("源节点不存在。", "command.nodeId", "TargetStale"); return; }
                HashSet<string> descendants = [copy.NodeId!];
                while (true)
                {
                    int count = descendants.Count;
                    foreach (var node in tree.Nodes)
                        if (node.ParentId is { } parent && descendants.Contains(parent)) descendants.Add(node.Id);
                    if (descendants.Count > OrchestrationNodeCreationLimits.MaxCopiedNodes
                        || tree.Nodes.Length + descendants.Count > OrchestrationNodeCreationLimits.MaxNodes)
                        throw new ReadModelBudgetExceededException();
                    if (descendants.Count == count) break;
                }
                if (!SceneTreeEditor.TryCopySubtree(input, copy.NodeId!, copy.Name, out rootId, out after, out _))
                    throw new InvalidNodeCreationTreeException();
            }
            else { error = NodeCreationInvalid(); return; }

            GuardNodeCreationTree(after);
            string[] addedIds = [rootId, .. after.LyricPresentation.Nodes.Where(n => n.Id != rootId && !originalIds.Contains(n.Id)).Select(n => n.Id)];
            if (addedIds.Length != after.LyricPresentation.Nodes.Length - tree.Nodes.Length
                || addedIds.Any(id => tree.FindNode(id) is not null)) throw new InvalidNodeCreationTreeException();
            result = new(command.Type["orchestration.".Length..], rootId, after.LyricPresentation.FindNode(rootId)!.ParentId,
                null, null, true, addedIds);
            string revision = checked(ProjectRevision + 1).ToString(CultureInfo.InvariantCulture);
            _ = BuildOrchestration(after, revision);
            ProjectChangeSetDto changes = new(["orchestration", "renderInputs"], [], [], [], true);
            ProjectCommandResponse upper = new(ProjectSessionId, RevisionText, revision, true, true, true, false,
                command.Type, null, changes,
                EventCursor: HostInstanceId + ":" + OwnerClientId + ":" + long.MaxValue.ToString(CultureInfo.InvariantCulture), StructureResult: result);
            if (JsonSerializer.SerializeToUtf8Bytes(BackendOperationResult<ProjectCommandResponse>.Success(upper)).Length > _branchReceiptMaxBytes)
                throw new ReadModelBudgetExceededException();
        }
        catch (ReadModelBudgetExceededException) { after = before; result = null; error = NodeCreationBudget(); }
        catch (Exception exception) when (exception is InvalidNodeCreationTreeException or ArgumentException or InvalidOperationException or KeyNotFoundException)
        { after = before; result = null; error = StructureRejected("源或候选节点树未通过完整工程展示校验。", "command", "PresentationInvalid"); }
    }

    private static void GuardNodeCreationTree(Project project)
    {
        var tree = project.LyricPresentation;
        if (tree.Nodes.Length > OrchestrationNodeCreationLimits.MaxNodes) throw new ReadModelBudgetExceededException();
        // The shared counter enforces MaxInputItems/MaxInputCharacters (same numeric
        // bounds as read models). This includes source lyric traversal, not only the
        // fields returned by orchestration GET; long lyrics may therefore be refused.
        ReadModelBudget budget = new();
        budget.Item(tree.Nodes.Length + tree.Programs.Length + tree.LineOverrides.Length + project.Lyrics.Branches.Length);
        Dictionary<string, SceneContainerDefinition> nodes = new(StringComparer.Ordinal);
        foreach (var node in tree.Nodes)
        {
            budget.Text(node.Id, node.Name, node.ParentId);
            if (string.IsNullOrWhiteSpace(node.Id) || !nodes.TryAdd(node.Id, node)) throw new InvalidNodeCreationTreeException();
            if (node.Layout is { } layout)
            { budget.Item(layout.SlotIds.Length); foreach (string id in layout.SlotIds) budget.Text(id); }
            if (node.Content is CountdownSequenceContent sequence)
            { budget.Item(sequence.MarkNodeIds.Length); foreach (string id in sequence.MarkNodeIds) budget.Text(id);
                _ = ProjectSceneAppearance(sequence.Appearance, budget); }
            else if (node.Content is TextMarkContent mark) { budget.Text(mark.Text); _ = ProjectLocalStyle(mark.AppearanceOverride?.ToStyleOverride(), budget); }
            else if (node.Content is not null) throw new InvalidNodeCreationTreeException();
        }
        foreach (var node in tree.Nodes)
        {
            HashSet<string> path = new(StringComparer.Ordinal);
            var current = node;
            while (true)
            {
                if (!path.Add(current.Id)) throw new InvalidNodeCreationTreeException();
                if (path.Count > OrchestrationNodeCreationLimits.MaxDepth) throw new ReadModelBudgetExceededException();
                if (current.ParentId is null) break;
                if (!nodes.TryGetValue(current.ParentId, out current!)) throw new InvalidNodeCreationTreeException();
            }
            if (node.Layout is { } layout && layout.SlotIds.Any(id => id is null || !nodes.TryGetValue(id, out var slot) || slot.ParentId != node.Id))
                throw new InvalidNodeCreationTreeException();
            if (node.Content is CountdownSequenceContent sequence && sequence.MarkNodeIds.Any(id => id is null
                || !nodes.TryGetValue(id, out var mark) || mark.ParentId != node.Id || mark.Content is not TextMarkContent))
                throw new InvalidNodeCreationTreeException();
        }
        foreach (var program in tree.Programs)
        {
            budget.Text(program.Id, program.BranchId, program.RootContainerId, program.DefaultContainerId);
            budget.Item(program.Clips.Length + program.LeadIns.Length);
            foreach (var clip in program.Clips)
            {
                budget.Text(clip.Id, clip.Name, clip.ContainerId); budget.Item(clip.LineIds.Length);
                foreach (string id in clip.LineIds) budget.Text(id);
                if (clip.ScheduleContext is { } context)
                {
                    budget.Item(context.LineIds.Length); budget.Text(context.PreviousLineId, context.NextLineId);
                    foreach (string id in context.LineIds) budget.Text(id);
                }
            }
            foreach (var cue in program.LeadIns) budget.Text(cue.Id, cue.Name, cue.TargetLineId, cue.IndicatorContainerId);
            if (program.LeadInGeneration is { } generation)
            {
                budget.Item(generation.ExcludedLineIds.Length); budget.Text(generation.IndicatorContainerId);
                foreach (string id in generation.ExcludedLineIds) budget.Text(id);
            }
        }
        foreach (var line in tree.LineOverrides) budget.Text(line.BranchId, line.LineId);
        // Core validation derives line time from tokens, so bound those traversals too.
        foreach (var branch in project.Lyrics.Branches)
        {
            budget.Text(branch.Id); budget.Item(branch.Lines.Length);
            foreach (var line in branch.Lines)
            {
                budget.Text(line.Id); budget.Item(line.Tokens.Length);
                foreach (var token in line.Tokens) budget.Text(token.Id);
            }
        }
    }

    private sealed class InvalidNodeCreationTreeException : Exception;
    private static BackendErrorResponse NodeCreationInvalid() => new(BackendErrorCode.InvalidRequest,
        "节点创建或复制字段无效；kind、名称、身份及槽位数量必须符合公开闭合格式。", "command");
    private static BackendErrorResponse NodeCreationBudget() => new(BackendErrorCode.ResourceTooLarge,
        "节点意图、源或候选树、复制数量、完整读投影或回执超过预算；工程未改变。", "command");
}
