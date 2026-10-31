using System.Text;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Backend.Hosting;

public sealed partial class ProjectSession
{
    internal Task<BackendOperationResult<ProjectGeometryReferenceResponse>> ReadGeometryReferenceAsync(
        string? expectedRevision, string? nodeId, CancellationToken cancellationToken) => ReadModelAsync(expectedRevision, () =>
        {
            if (string.IsNullOrWhiteSpace(nodeId) || Encoding.UTF8.GetByteCount(nodeId) > ProjectGeometryReferenceLimits.MaxTargetIdBytes)
                return GeometryRejected(BackendErrorCode.InvalidRequest, "必须提供 1..1024 UTF-8 字节的 nodeId。", "nodeId");

            Project project = Workspace.Project;
            var saved = project.LyricPresentation;
            // Bound work before WithDefaultPrograms, whose compatibility projection scans
            // programs and line overrides and may add two nodes for each missing source.
            long items = (long)saved.Nodes.Length + saved.Programs.Length + saved.LineOverrides.Length + project.Lyrics.Branches.Length;
            if (items > ProjectGeometryReferenceLimits.MaxInputItems || saved.Nodes.Length > ProjectGeometryReferenceLimits.MaxNodes
                || project.Lyrics.Branches.Length > ProjectGeometryReferenceLimits.MaxNodes)
                return GeometryBudget();
            Dictionary<string, SceneContainerDefinition> savedNodes = new(StringComparer.Ordinal);
            foreach (SceneContainerDefinition node in saved.Nodes)
            {
                if (string.IsNullOrWhiteSpace(node.Id) || !savedNodes.TryAdd(node.Id, node)
                    || node.Placement is not { IsValid: true } || !node.ReferenceRect.IsValid) return InvalidGeometry();
                items += node.Layout?.SlotIds.Length ?? 0;
                if (items > ProjectGeometryReferenceLimits.MaxInputItems) return GeometryBudget();
            }
            long idBytes = 0;
            foreach (string? id in saved.Nodes.SelectMany(n => new[] { n.Id, n.ParentId })
                .Concat(saved.Nodes.Where(n => n.Layout is not null).SelectMany(n => n.Layout!.SlotIds))
                .Concat(saved.LineOverrides.SelectMany(line => new[] { line.BranchId, line.LineId }))
                .Concat(saved.Programs.Select(p => p.BranchId)).Concat(project.Lyrics.Branches.Select(b => b.Id)))
            {
                idBytes += id is null ? 0 : Encoding.UTF8.GetByteCount(id);
                if (idBytes > ProjectGeometryReferenceLimits.MaxInputIdBytes) return GeometryBudget();
            }
            // The legacy materializer may inspect an old slot profile while filling a
            // missing source program, even though this endpoint never returns layout.
            foreach (SceneContainerDefinition node in saved.Nodes)
                if (node.Layout is { SlotAnimation: not null } layout && layout.SlotIds.Any(id => id is null || !savedNodes.ContainsKey(id)))
                    return InvalidGeometry();
            if (project.Lyrics.Branches.Any(branch => string.IsNullOrWhiteSpace(branch.Id))) return InvalidGeometry();
            HashSet<string> projectedIds = new(savedNodes.Keys, StringComparer.Ordinal);
            HashSet<string> programBranches = new(saved.Programs.Select(program => program.BranchId), StringComparer.Ordinal);
            foreach (var branch in project.Lyrics.Branches)
                if (programBranches.Add(branch.Id))
                {
                    projectedIds.Add(LyricSceneIds.Root(branch.Id));
                    projectedIds.Add(LyricSceneIds.Regular(branch.Id));
                    if (projectedIds.Count > ProjectGeometryReferenceLimits.MaxNodes) return GeometryBudget();
                }
            // Default programs contribute deterministic implicit nodes, just as orchestration GET does.
            var tree = saved.WithDefaultPrograms(project.Lyrics);
            if (tree.Nodes.Length > ProjectGeometryReferenceLimits.MaxNodes) return GeometryBudget();
            Dictionary<string, SceneContainerDefinition> nodes = new(StringComparer.Ordinal);
            foreach (SceneContainerDefinition node in tree.Nodes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(node.Id) || !nodes.TryAdd(node.Id, node)
                    || node.Placement is not { IsValid: true } || !node.ReferenceRect.IsValid)
                    return InvalidGeometry();
            }
            if (!nodes.TryGetValue(nodeId, out SceneContainerDefinition? target))
                return GeometryRejected(BackendErrorCode.ResourceNotFound, "编排节点不存在。", "nodeId");
            // Validate every chain before invoking the shared recursive resolver: malformed
            // imported trees cannot overflow the stack or escape as an HTTP 500.
            foreach (SceneContainerDefinition node in tree.Nodes)
            {
                HashSet<string> path = new(StringComparer.Ordinal);
                SceneContainerDefinition current = node;
                while (true)
                {
                    if (!path.Add(current.Id)) return InvalidGeometry();
                    if (path.Count > ProjectGeometryReferenceLimits.MaxDepth) return GeometryBudget();
                    if (current.ParentId is null) break;
                    if (!nodes.TryGetValue(current.ParentId, out current!)) return InvalidGeometry();
                }
            }
            if (project.Composition.CanvasWidth <= 0 || project.Composition.CanvasHeight <= 0) return InvalidGeometry();
            var resolved = SceneTreeGeometryResolver.Resolve(tree.Nodes, project.Composition.CanvasWidth, project.Composition.CanvasHeight);
            var canvas = new SceneTreeGeometry(project.Composition.CanvasWidth, project.Composition.CanvasHeight, SceneMatrix.Identity, new(0, 0));
            if (resolved.Values.Any(value => !ValidReference(value))) return InvalidGeometry();
            var reference = resolved[nodeId];
            var parent = target.ParentId is null ? canvas : resolved[target.ParentId];
            return BoundedReadModel(new ProjectGeometryReferenceResponse(ProjectSessionId, RevisionText, nodeId, target.ParentId,
                project.Composition.CanvasWidth, project.Composition.CanvasHeight, GeometryDto(reference), GeometryDto(parent)));
        }, cancellationToken, "geometry");

    private static bool ValidReference(SceneTreeGeometry value)
    {
        var matrix = value.WorldMatrix;
        return value.Width > 0 && double.IsFinite(value.Width) && value.Height > 0 && double.IsFinite(value.Height)
            && new[] { matrix.A, matrix.B, matrix.C, matrix.D, matrix.Tx, matrix.Ty, value.Pivot.X, value.Pivot.Y }.All(double.IsFinite)
            && matrix.TryInverse(out _) && ReferenceCorners(value).All(p => double.IsFinite(p.X) && double.IsFinite(p.Y));
    }

    private static CanvasPoint[] ReferenceCorners(SceneTreeGeometry value) =>
        [value.WorldMatrix.Map(new(0, 0)), value.WorldMatrix.Map(new(value.Width, 0)),
            value.WorldMatrix.Map(new(value.Width, value.Height)), value.WorldMatrix.Map(new(0, value.Height))];

    private static ProjectGeometryReferenceDto GeometryDto(SceneTreeGeometry value) => new(value.Width, value.Height,
        new(value.WorldMatrix.A, value.WorldMatrix.B, value.WorldMatrix.C, value.WorldMatrix.D, value.WorldMatrix.Tx, value.WorldMatrix.Ty),
        new(value.Pivot.X, value.Pivot.Y), [.. ReferenceCorners(value).Select(p => new ProjectGeometryPointDto(p.X, p.Y))]);

    private static BackendOperationResult<ProjectGeometryReferenceResponse> InvalidGeometry() => GeometryRejected(
        BackendErrorCode.CommandRejected, "工程固定容器参考树或数值无效。", "geometry", "GeometryReferenceInvalid");
    private static BackendOperationResult<ProjectGeometryReferenceResponse> GeometryBudget() => GeometryRejected(
        BackendErrorCode.ResourceTooLarge, "几何参考输入超过节点、深度、输入项数或 ID 字节预算。", "geometry", "GeometryReferenceBudgetExceeded");
    private static BackendOperationResult<ProjectGeometryReferenceResponse> GeometryRejected(BackendErrorCode code,
        string message, string field, string? failureCode = null) => BackendOperationResult<ProjectGeometryReferenceResponse>.Rejected(
            new(code, message, field, FailureCode: failureCode));
}
