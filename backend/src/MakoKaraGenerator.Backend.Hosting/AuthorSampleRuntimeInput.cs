using System.Security.Cryptography;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Rendering;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Runtime.Hosting.Authoring;

namespace MakoKaraGenerator.Backend.Hosting;

internal static class AuthorSampleRuntimeInput
{
    internal static async Task<BackendPreparedProjectRuntimeInput> PrepareAsync(
        ScriptSampleRequest request, CancellationToken token)
    {
        await using TemplateSampleLease media = new(Path.Combine(Path.GetTempPath(), "makokara-author-samples"));
        TemplateSampleResources resources = await media.LoadAsync(token).ConfigureAwait(false);
        ProjectSnapshot snapshot = await AddMediaIdentityAsync(ScriptSampleBuilder.Build(request, resources), resources, token).ConfigureAwait(false);
        snapshot = ProjectSnapshot.FromProject(ProjectSnapshotSourceProject.Restore(snapshot));
        using BackendProjectRuntimeInputLease lease = BackendProjectRuntimeInputLease.CreateOwnedTemplateSample(snapshot, media.DirectoryPath);
        BackendOperationResult<BackendPreparedProjectRuntimeInput> prepared = await lease.PrepareAsync(token).ConfigureAwait(false);
        if (!prepared.Accepted) throw new AuthorSamplePreparationException(prepared.Error!);
        // Prepared input owns the copied media closure; the generated source directory can now be removed.
        return prepared.Response!;
    }

    private static async Task<ProjectSnapshot> AddMediaIdentityAsync(ProjectSnapshot snapshot,
        TemplateSampleResources resources, CancellationToken token)
    {
        static async Task<(string Hash, long Size)> Identity(string path, CancellationToken cancellationToken)
        {
            await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return (Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)), stream.Length);
        }
        var audio = await Identity(resources.AudioPath, token).ConfigureAwait(false);
        var background = await Identity(resources.BackgroundPath, token).ConfigureAwait(false);
        return snapshot with
        {
            AudioMedia = new(audio.Hash, audio.Size, Path.GetFileName(resources.AudioPath)),
            Background = snapshot.Background with { Media = new(background.Hash, background.Size, Path.GetFileName(resources.BackgroundPath)) },
        };
    }
}

internal sealed class AuthorSamplePreparationException(BackendErrorResponse error) : Exception(error.Message)
{
    internal BackendErrorResponse Error { get; } = error;
}
