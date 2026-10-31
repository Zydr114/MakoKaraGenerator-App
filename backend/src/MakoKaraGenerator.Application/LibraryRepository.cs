using System.Diagnostics;
using MakoKaraGenerator.Core.Persistence;

namespace MakoKaraGenerator.Application;

// Shared write protocol only; each typed library keeps its own schema and closure validator.
internal sealed class LibraryRepository<TItem, TSnapshot>
{
    private readonly Func<string, TSnapshot> _read;
    private readonly Func<TSnapshot, LibraryIndexRevision> _revision;
    private readonly Func<TSnapshot, bool> _invalid;
    private readonly Func<string, TItem[], LibraryIndexRevision, CancellationToken, TSnapshot> _write;
    internal LibraryRepository(string path, Func<string, TSnapshot> read, Func<TSnapshot, LibraryIndexRevision> revision,
        Func<TSnapshot, bool> invalid, Func<string, TItem[], LibraryIndexRevision, CancellationToken, TSnapshot> write)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = System.IO.Path.GetFullPath(path); _read = read; _revision = revision; _invalid = invalid; _write = write;
    }
    internal string Path { get; }
    internal event Action<TSnapshot>? Changed;
    internal TSnapshot Read() => _read(Path);
    internal TSnapshot Save(LibraryIndexRevision expected, IEnumerable<TItem> items, CancellationToken token)
    {
        TItem[] captured = items.ToArray();
        TSnapshot result;
        bool changed;
        using (LibraryCommitLease lease = LibraryCommitLease.TryEnter(Path, token))
        {
            _ = RequireCurrent(expected);
            token.ThrowIfCancellationRequested();
            result = _write(Path, captured, expected, token);
            changed = _revision(result) != expected;
        }
        if (changed) Notify(result);
        return result;
    }
    internal async Task<TSnapshot> SaveAsync(LibraryIndexRevision expected, IEnumerable<TItem> items, CancellationToken token)
    {
        TItem[] captured = items.ToArray();
        TSnapshot result;
        using (LibraryCommitLease lease = await LibraryCommitLease.EnterAsync(Path, token).ConfigureAwait(false))
            result = await Task.Run(() => { _ = RequireCurrent(expected); return _write(Path, captured, expected, token); }, token).ConfigureAwait(false);
        // Publication is the commit point. Do not inspect a late cancellation or re-read an externally changed index here.
        if (_revision(result) != expected) Notify(result);
        return result;
    }
    internal async Task<TSnapshot> CheckCurrentAsync(LibraryIndexRevision expected, CancellationToken token)
    {
        using LibraryCommitLease lease = await LibraryCommitLease.EnterAsync(Path, token).ConfigureAwait(false);
        return await Task.Run(() => RequireCurrent(expected), token).ConfigureAwait(false);
    }
    private TSnapshot RequireCurrent(LibraryIndexRevision expected)
    {
        TSnapshot current = Read();
        if (_invalid(current)) throw new IOException("Library index or resource closure is invalid; original data was retained.");
        if (_revision(current) != expected) throw new LibraryRevisionConflictException(Path);
        return current;
    }
    private void Notify(TSnapshot snapshot)
    {
        if (Changed is not { } listeners) return;
        foreach (Action<TSnapshot> listener in listeners.GetInvocationList().Cast<Action<TSnapshot>>())
        {
            try { listener(snapshot); }
            catch (Exception error) { Trace.TraceError("Committed library notification failed: {0}", error); }
        }
    }
}
