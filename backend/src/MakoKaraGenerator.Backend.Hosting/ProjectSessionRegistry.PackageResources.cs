using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Rendering;

namespace MakoKaraGenerator.Backend.Hosting;

public sealed partial class ProjectSessionRegistry
{
    private readonly object _packageAssetGate = new();
    private PackageAssetLeaseStore? _packageAssets;

    private PackageAssetLeaseStore PackageAssets
    {
        get
        {
            lock (_packageAssetGate) return _packageAssets ??= new PackageAssetLeaseStore(_resources);
        }
    }

    /// <summary>以客户端回执幂等地把工程包内已验证文件惰性投影为现有 asset。</summary>
    public Task<BackendOperationResult<ProjectPackageResourceAssetResponse>> MaterializePackageResourceAsync(
        string clientId, string sessionId, ProjectPackageResourceAssetRequest request,
        CancellationToken cancellationToken = default) =>
        Receipts.ExecuteAsync(clientId, request.Metadata,
            "POST /api/v1/project-sessions/" + sessionId + "/resources/package-asset",
            request with { Metadata = null },
            () => MaterializePackageResourceCoreAsync(clientId, sessionId, request.ExpectedRevision, request.ResourceId, CancellationToken.None),
            cancellationToken);

    private async Task<BackendOperationResult<ProjectPackageResourceAssetResponse>> MaterializePackageResourceCoreAsync(
        string clientId, string sessionId, string? expectedRevision, string resourceId,
        CancellationToken cancellationToken = default)
    {
        if (!TryGet(clientId, sessionId, out ProjectSession session))
            return BackendOperationResult<ProjectPackageResourceAssetResponse>.Rejected(MissingSessionError(sessionId));
        BackendOperationResult<PackageResourcePayload> payload = await session.ReadPackageResourceAsync(
            expectedRevision, resourceId, cancellationToken).ConfigureAwait(false);
        if (!payload.Accepted) return BackendOperationResult<ProjectPackageResourceAssetResponse>.Rejected(payload.Error!);

        PackageResourcePayload value = payload.Response!;
        PackageClosureInspection inspection = value.Inspection;
        if (!inspection.IsValidated || inspection.UnsupportedPaths.Contains(value.PackagePath, StringComparer.Ordinal))
            return BackendOperationResult<ProjectPackageResourceAssetResponse>.Rejected(new(
                BackendErrorCode.CommandNotSupported, "包资源闭包未通过当前有限读取能力。", nameof(resourceId),
                FailureCode: inspection.FailureCode ?? "PackageClosureUnsupported"));
        try
        {
            StoredAsset asset = await PackageAssets.MaterializeAsync(clientId, sessionId, value.ResourceId,
                value.PackagePath, value.PackageRole, value.Content, cancellationToken).ConfigureAwait(false);
            AssetResponse response = new(asset.AssetId, asset.Sha256, asset.Size, asset.ContentType, asset.FileName, asset.ExpiresAtUtc);
            return BackendOperationResult<ProjectPackageResourceAssetResponse>.Success(new(
                sessionId, session.ProjectRevision.ToString(System.Globalization.CultureInfo.InvariantCulture), value.ResourceId,
                value.PackageId, value.PackageRevision, value.PackagePath, value.PackageRole, response,
                "/api/v1/assets/" + asset.AssetId + "/content"));
        }
        catch (ResourceStoreException error)
        {
            return BackendOperationResult<ProjectPackageResourceAssetResponse>.Rejected(new(error.Code,
                "包资源不能发布为当前客户端的受控 asset。", error.Field));
        }
    }

    internal void ReleasePackageAssetsForSession(string sessionId)
    {
        lock (_packageAssetGate) _packageAssets?.ReleaseSession(sessionId);
    }

}
