using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Rendering;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Backend.Hosting;

internal sealed record PackageResourcePayload(
    string ResourceId, string PackageId, string PackageRevision, string PackagePath, string PackageRole,
    byte[] Content, PackageClosureInspection Inspection);

public sealed partial class ProjectSession
{
    internal Task<BackendOperationResult<PackageResourcePayload>> ReadPackageResourceAsync(
        string? expectedRevision, string resourceId, CancellationToken token) =>
        ReadModelAsync(expectedRevision, () =>
        {
            if (string.IsNullOrWhiteSpace(resourceId))
                return BackendOperationResult<PackageResourcePayload>.Rejected(new(BackendErrorCode.InvalidRequest,
                    "包资源标识不能为空。", nameof(resourceId)));
            foreach (EffectPackageContents package in Packages())
            {
                if (resourceId == "packageManifest:" + package.Revision)
                    return BackendOperationResult<PackageResourcePayload>.Success(new(resourceId, package.PackageId,
                        package.Revision, "manifest.json", "manifest", package.Manifest.ToArray(), PackageProjectSnapshotCodec.InspectResourceClosure(package)));
                string prefix = "packageFile:" + package.Revision + ":";
                if (!resourceId.StartsWith(prefix, StringComparison.Ordinal)) continue;
                string path = resourceId[prefix.Length..];
                EffectPackageFile? file = package.Files.FirstOrDefault(item => item.Path == path);
                if (file is not null)
                    return BackendOperationResult<PackageResourcePayload>.Success(new(resourceId, package.PackageId,
                        package.Revision, file.Path, file.Role, file.Content.ToArray(), PackageProjectSnapshotCodec.InspectResourceClosure(package)));
            }
            return BackendOperationResult<PackageResourcePayload>.Rejected(new(BackendErrorCode.ResourceNotFound,
                "工程包资源不存在或已不属于当前修订。", nameof(resourceId)));
        }, token, "package-resource");

    private IEnumerable<EffectPackageContents> Packages()
    {
        foreach (EffectPackageContents package in Workspace.Project.Effects.Packages) yield return package;
        foreach (PresetApplication application in Workspace.Project.PresetApplications.Items)
            foreach (EffectPackageContents package in application.Snapshot.Packages) yield return package;
    }
}
