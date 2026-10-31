using MakoKaraGenerator.Backend.Contracts;

namespace MakoKaraGenerator.Backend.Hosting;

public sealed partial class ProjectSessionRegistry
{
    internal Func<Task>? BackgroundPreparationCheckpoint { get; set; }

    private async Task<ProjectCommandExecutionResult> ExecuteBackgroundCommandAsync(string clientId, ProjectSession session,
        ProjectCommandRequest request)
    {
        var captured = await session.CaptureBackgroundCommandAsync(request).ConfigureAwait(false);
        if (captured.Error is { } captureError) return ProjectCommandExecutionResult.Rejected(captureError);
        IDisposable? reference = null;
        try
        {
            StoredAsset? asset = null;
            if (request.Command is SetBackgroundAssetCommandDto set)
            {
                reference = _resources.AcquireReference(clientId, set.AssetId ?? string.Empty);
                asset = _resources.GetAuthorized(clientId, set.AssetId!);
                // Full media IO stays outside the project writer. The reference fixes retention
                // while the second writer checks the captured session and project generation.
                await Task.Run(() => VerifyFixedAudioSource(clientId, asset)).ConfigureAwait(false);
            }
            if (BackgroundPreparationCheckpoint is { } checkpoint) await checkpoint().ConfigureAwait(false);
            return await session.CommitBackgroundCommandAsync(request, captured.Capture!,
                () => session.OwnerClientId == clientId && TryGet(clientId, session.ProjectSessionId, out ProjectSession current) && ReferenceEquals(current, session),
                _ => asset!, id => _resources.AcquireReference(clientId, id)).ConfigureAwait(false);
        }
        catch (ResourceStoreException error)
        { return ProjectCommandExecutionResult.Rejected(new(error.Code, error.Message, "command.assetId")); }
        finally { reference?.Dispose(); }
    }
}
