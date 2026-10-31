namespace MakoKaraGenerator.Backend.Hosting;

public sealed partial class ProjectSessionRegistry
{
    /// <summary>只由ClientRegistry的锁外claim回调调用；保留所有持久数据和有效结果所有权。</summary>
    public async Task<bool> TryReclaimClientAsync(string clientId, Func<string, Task>? cleanupPreview = null)
    {
        await _membershipGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_sessions.Values.Any(session => session.OwnerClientId == clientId && !session.IsClosed)
                || Receipts.HasProtectedClient(clientId) || HasSceneMeasurement(clientId) || HasLyricsExport(clientId) || HasTemplateTransfer(clientId)
                || HasAuthorDraft(clientId)) return false;
            lock (_jobGate)
            {
                if (_jobs.Values.Any(job => job.Owner == clientId && (!job.Completion.IsCompleted
                    || job.Read().ExpiresAtUtc > _timeProvider.GetUtcNow()))) return false;
            }
            // Expired previews already have no recovery guarantee. Drain workers before checking
            // references, and before removing any resumable client-local indexes.
            if (cleanupPreview is not null) await cleanupPreview(clientId).ConfigureAwait(false);
            if (_resources.HasClientReferences(clientId)) return false;
            lock (_jobGate)
            {
                foreach (BackendJob job in _jobs.Values.Where(job => job.Owner == clientId).ToArray())
                {
                    if (!job.Completion.IsCompleted || job.Read().ExpiresAtUtc > _timeProvider.GetUtcNow()) return false;
                    job.Dispose();
                }
                foreach (BackendJob job in _jobs.Values.Where(job => job.Owner == clientId).ToArray()) _jobs.TryRemove(job.Id, out _);
            }
            // No filesystem mutations: persisted projects, reports, ledger and uploaded assets
            // keep their existing retention/TTL. All remaining cleanup is in-memory and idempotent.
            _handles?.RemoveClient(clientId);
            RemoveLyricsExports(clientId);
            Receipts.RemoveClient(clientId);
            _events.RemoveClient(clientId);
            return true;
        }
        finally { _membershipGate.Release(); }
    }
}
