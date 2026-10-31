using System.Globalization;
using System.Security.Cryptography;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Rendering;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Backend.Hosting;

public sealed partial class ProjectSession
{
    internal Task<BackendOperationResult<ProjectResourcesResponse>> ReadProjectResourcesAsync(string? expectedRevision,
        int offset, int limit, Func<string, StoredAsset?> findAudio, CancellationToken token) =>
        ReadModelAsync(expectedRevision, () =>
        {
            var resources = new Dictionary<string, ProjectResourceDto>(StringComparer.Ordinal);
            ReadModelBudget budget = new();
            void Add(ProjectResourceDto value)
            {
                budget.Item(); budget.Text(value.ResourceId, value.Kind, value.Status, value.Sha256, value.AssetId,
                    value.ContentUrl, value.PreparationStatus, value.WaveformStatus); budget.Text(value.Roles);
                if (resources.TryGetValue(value.ResourceId, out ProjectResourceDto? old))
                    resources[value.ResourceId] = old with { Roles = [.. old.Roles.Concat(value.Roles).Distinct(StringComparer.Ordinal)] };
                else resources.Add(value.ResourceId, value);
            }
            Project project = Workspace.Project;
            if (project.AudioPath is { } path)
            {
                StoredAsset? audio = string.IsNullOrEmpty(AudioAssetId) ? null : findAudio(AudioAssetId);
                bool identity = audio is not null && (project.AudioMedia is not { } media || media.Hash == audio.Sha256 && media.ByteLength == audio.Size);
                bool available = identity && File.Exists(audio!.StoragePath) && File.Exists(path);
                string? hash = project.AudioMedia?.Hash ?? audio?.Sha256;
                Add(new(hash is null ? "project.audio" : "media:" + hash, "media", ["project.audio"], available ? "available" : "missing",
                    (project.AudioMedia?.ByteLength ?? audio?.Size)?.ToString(CultureInfo.InvariantCulture), hash,
                    identity ? audio!.AssetId : null, available ? "/api/v1/assets/" + audio!.AssetId + "/content" : null,
                    identity && PreparedAudioSourcePath == path ? "prepared" : "unavailable",
                    WaveformAudioMatches() && _waveform?.Metadata(ProjectSessionId) is { Available: true } ? "available" : _waveformUnavailable));
            }
            if (project.Background.HasMedia)
            {
                MediaFileRevision? media = project.Background.Media;
                StoredAsset? background = project.Background.Path is { } backgroundPath && _backgroundHistory.TryGetValue(backgroundPath, out var state)
                    ? findAudio(state.Asset.AssetId) : null;
                bool available = background is not null && media?.Hash == background.Sha256 && media.ByteLength == background.Size
                    && File.Exists(background.StoragePath) && File.Exists(project.Background.Path);
                Add(new(media is null ? "project.background" : "media:" + media.Hash, "media", ["project.background"],
                    available ? "available" : !File.Exists(project.Background.Path) ? "missing" : "identityUnavailable",
                    media?.ByteLength.ToString(CultureInfo.InvariantCulture), media?.Hash,
                    available ? background!.AssetId : null, available ? "/api/v1/assets/" + background!.AssetId + "/content" : null,
                    available ? "prepared" : "unavailable"));
            }
            if (project.AudioAnalysis is { } analysis)
            {
                if (analysis.Data is { } data)
                    Add(new("analysis:" + data.ContentHash, "audioAnalysis", ["project.audioAnalysis"], "embedded",
                        data.ContentByteLength.ToString(CultureInfo.InvariantCulture), data.ContentHash));
                else Add(new("project.audioAnalysis", "audioAnalysis", ["project.audioAnalysis"], analysis.MissingStream ? "noStream" : "failed"));
            }
            void Package(EffectPackageContents package, string role)
            {
                PackageClosureInspection closure = PackageProjectSnapshotCodec.InspectResourceClosure(package);
                ProjectPackageClosureSummaryDto summary = new(package.PackageId, package.Revision,
                    closure.ManifestSizeBytes.ToString(CultureInfo.InvariantCulture), closure.ManifestSha256,
                    closure.TotalSizeBytes.ToString(CultureInfo.InvariantCulture), closure.FileCount,
                    closure.ValidatedFileCount, closure.UnsupportedPaths.Length,
                    closure.IsValidated ? "validated" : "unsupported");
                string packageStatus = closure.IsValidated ? "validated" : "unsupported";
                Add(new("packageManifest:" + package.Revision, "effectPackageManifest", [role + ":" + package.PackageId + ":" + package.Revision], packageStatus,
                    closure.ManifestSizeBytes.ToString(CultureInfo.InvariantCulture), closure.ManifestSha256,
                    PackageId: package.PackageId, PackageRevision: package.Revision, PackagePath: "manifest.json", PackageRole: "manifest",
                    PackageClosure: summary));
                foreach (EffectPackageFile file in package.Files)
                {
                    bool supported = closure.IdentityValid && closure.RoleReferencesValid
                        && !closure.UnsupportedPaths.Contains(file.Path, StringComparer.Ordinal);
                    Add(new("packageFile:" + package.Revision + ":" + file.Path, "effectPackageFile",
                        [role + ":" + package.PackageId + ":" + file.Path], supported ? "validated" : "unsupported",
                        file.Content.Length.ToString(CultureInfo.InvariantCulture), file.Sha256,
                        PackageId: package.PackageId, PackageRevision: package.Revision, PackagePath: file.Path, PackageRole: file.Role));
                }
            }
            foreach (EffectPackageContents package in project.Effects.Packages) Package(package, "project.effects");
            foreach (PresetApplication application in project.PresetApplications.Items)
            {
                budget.Item(); budget.Text(application.Id, application.Snapshot.Id);
                byte[] canonical = StyleTemplateLibrary.EncodeForIdentity([application.Snapshot]);
                string hash = Convert.ToHexStringLower(SHA256.HashData(canonical));
                Add(new("templateSnapshot:" + hash, "templateSnapshot", ["presetApplication:" + application.Id], "embedded",
                    canonical.Length.ToString(CultureInfo.InvariantCulture), hash));
                foreach (EffectPackageContents package in application.Snapshot.Packages) Package(package, "presetApplication:" + application.Id);
            }
            ProjectResourceDto[] complete = [.. resources.Values.OrderBy(value => value.ResourceId, StringComparer.Ordinal)];
            // Bound the complete reference closure, not just the requested page; no hidden truncation.
            BoundedReadModel(new ProjectResourcesResponse(ProjectSessionId, RevisionText, 0, complete.Length, null, complete));
            if (PageError(offset, limit, complete.Length) is { } error)
                return BackendOperationResult<ProjectResourcesResponse>.Rejected(error);
            ProjectResourceDto[] page = [.. complete.Skip(offset).Take(limit)];
            return BoundedReadModel(new ProjectResourcesResponse(ProjectSessionId, RevisionText, offset,
                complete.Length, NextOffset(offset, page.Length, complete.Length), page));
        }, token, "resources");
}

public sealed partial class ProjectSessionRegistry
{
    /// <summary>精确修订下工程当前引用的完整有界资源清单；客户端其他上传不会混入。</summary>
    public Task<BackendOperationResult<ProjectResourcesResponse>> ReadProjectResourcesAsync(string clientId, string sessionId,
        string? expectedRevision, int offset = 0, int limit = 100, CancellationToken cancellationToken = default) =>
        TryGet(clientId, sessionId, out ProjectSession session)
            ? session.ReadProjectResourcesAsync(expectedRevision, offset, limit, id =>
            {
                try { return _resources.GetAuthorized(clientId, id); }
                catch (ResourceStoreException) { return null; }
            }, cancellationToken)
            : Task.FromResult(BackendOperationResult<ProjectResourcesResponse>.Rejected(MissingSessionError(sessionId)));
}
