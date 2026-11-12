using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering;

namespace MakoKaraGenerator.Backend.Hosting;

public sealed partial class ProjectSession
{
    internal Task<BackendOperationResult<ProjectResolvedStyleResponse>> ReadResolvedStyleAsync(string? expectedRevision,
        string? branchId, string? lineId, string? tokenId, string? clipId, string? nodeId, CancellationToken cancellationToken) =>
        ReadModelAsync(expectedRevision, () =>
        {
            BackendOperationResult<ProjectResolvedStyleResponse> Reject(BackendErrorCode code, string message, string field) =>
                BackendOperationResult<ProjectResolvedStyleResponse>.Rejected(new(code, message, field));
            if (!IsStableId(branchId) || !IsStableId(lineId) || tokenId is not null && !IsStableId(tokenId)
                || clipId is not null && !IsStableId(clipId) || nodeId is not null && !IsStableId(nodeId))
                return Reject(BackendErrorCode.InvalidRequest, "样式解析需要长度受限的稳定来源、行及可选目标身份。", "query");
            Project project = Workspace.Project;
            try
            {
                GuardNodeCreationTree(project);
                var tree = project.LyricPresentation.WithDefaultPrograms(project.Lyrics);
                if (tree.Nodes.Length > OrchestrationNodeCreationLimits.MaxNodes) throw new ReadModelBudgetExceededException();
                var branch = project.Lyrics.Branches.FirstOrDefault(item => item.Id == branchId);
                var line = branch?.Lines.FirstOrDefault(item => item.Id == lineId);
                var program = tree.FindProgram(branchId!);
                if (line is null || program is null || tokenId is not null && !line.Tokens.Any(token => token.Id == tokenId))
                    return Reject(BackendErrorCode.ResourceNotFound, "样式来源、行、元素或展示程序不存在。", "target");
                var clip = program.Clips.SingleOrDefault(item => item.LineIds.Contains(line.Id));
                if (clipId is not null && clipId != clip?.Id)
                    return Reject(BackendErrorCode.InvalidRequest, "clipId 必须等于该行实际所属片段。", "clipId");
                string containerId = clip?.ContainerId ?? program.DefaultContainerId;
                var container = tree.FindNode(containerId);
                if (container?.Layout is not { } layout || !Under(tree, containerId, program.RootContainerId))
                    return Reject(BackendErrorCode.CommandRejected, "该行容器归属已失效。", "target");
                string actualNodeId = containerId;
                string contextKind = "container";
                if (branch!.TimingMode == LyricBranchTimingMode.Independent && EffectivePolicy(program, branch) == LyricBranchAnimationPolicy.Karaoke)
                {
                    var occurrence = LyricDisplayScheduleBuilder.Build(branch, program, tree).SingleOrDefault(item => item.Line.Id == line.Id);
                    if (occurrence is { SlotIndex: >= 0 })
                    {
                        if (occurrence.SlotIndex >= layout.SlotIds.Length) return Reject(BackendErrorCode.CommandRejected, "该行实际槽位已失效。", "target");
                        actualNodeId = layout.SlotIds[occurrence.SlotIndex]; contextKind = "scheduledSlot";
                    }
                }
                if (nodeId is not null && nodeId != actualNodeId)
                    return Reject(BackendErrorCode.InvalidRequest, "nodeId 必须等于该行实际推导的容器或槽位。", "nodeId");
                List<string> path = [];
                HashSet<string> seen = new(StringComparer.Ordinal);
                string? cursor = actualNodeId;
                while (cursor is not null)
                {
                    if (!seen.Add(cursor) || tree.FindNode(cursor) is not { } node)
                        return Reject(BackendErrorCode.CommandRejected, "样式节点父链循环或失效。", "target");
                    if (path.Count >= OrchestrationNodeCreationLimits.MaxDepth) throw new ReadModelBudgetExceededException();
                    path.Add(cursor); cursor = node.ParentId;
                }
                path.Reverse();
                // Runtime resolves from the dynamic text element, so even its direct container
                // and slot are ancestors and require IncludeDescendants=true.
                string textNodeId = SceneDefinitionBuilder.TextId(branchId!, lineId!);
                path.Add(textNodeId);
                ReadModelBudget budget = new(); budget.Item(project.PresetApplications.Items.Length + path.Count);
                budget.Text(ProjectSessionId, RevisionText, branchId, lineId, tokenId, clip?.Id, containerId, actualNodeId);
                foreach (string id in path) budget.Text(id);
                foreach (var application in project.PresetApplications.Items)
                { budget.Text(application.Id); budget.Item(application.Identities.Length + application.Snapshot.Properties.Length); }
                var resolved = new PresetStyleResolver(project.LyricStyle, project.Lyrics, project.PresetApplications)
                    .ResolveLyrics(line.Id, tokenId, clip?.Id, path);
                // 过渡期仍按旧合同的 26 项输出来源，每项取其主属性的来源（P3 第 7 步改版）。
                ResolvedStylePropertySourceDto[] sources = [.. Enum.GetValues<TemplateProperty>().Select(property =>
                {
                    string name = LegacyStylePropertyMap.JsonName(property), source = resolved.Sources[LegacyStylePropertyMap.Primary(property)];
                    budget.Item(); budget.Text(name, source); return new ResolvedStylePropertySourceDto(name, source);
                })];
                return BoundedReadModel(new ProjectResolvedStyleResponse(ProjectSessionId, RevisionText,
                    new(branchId!, lineId!, tokenId, clip?.Id, containerId, actualNodeId, textNodeId, [.. path], contextKind),
                    ProjectCanonicalStyle(resolved.Style, budget), sources));
            }
            catch (InvalidNodeCreationTreeException)
            { return Reject(BackendErrorCode.CommandRejected, "样式上下文展示树存在失效引用。", "target"); }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException)
            { return Reject(BackendErrorCode.CommandRejected, "静态样式上下文或来源无效：" + error.Message, "target"); }
        }, cancellationToken, "resolved-style");
}
