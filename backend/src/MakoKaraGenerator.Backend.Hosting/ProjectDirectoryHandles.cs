using MakoKaraGenerator.Application;
using MakoKaraGenerator.Backend.Contracts;

namespace MakoKaraGenerator.Backend.Hosting;

internal sealed class ProjectDirectoryHandles(ProjectPersistenceOptions options)
{
    internal sealed record Handle(string Owner, FileHandleResponse Response, string Root, string Path);
    private readonly object _gate = new();
    private readonly Dictionary<string, Handle> _handles = new(StringComparer.Ordinal);
    private readonly IReadOnlyDictionary<string, string> _roots = options.AllowedProjectRoots.ToDictionary(
        pair => pair.Key, pair => ProjectWriteLease.ResolvePhysicalPath(pair.Value), StringComparer.Ordinal);

    internal BackendOperationResult<FileHandleResponse> Create(string clientId, FileHandleRequest request)
    {
        if (!_roots.TryGetValue(request.RootGrantId, out string? root)) return Reject<FileHandleResponse>("工程根未获启动器授权。");
        if (request.Access is not ("read" or "readWrite")) return Reject<FileHandleResponse>("目录访问模式无效。");
        string path;
        try { path = Resolve(root, request.RelativePath); }
        catch (Exception error) when (error is IOException or ArgumentException or NotSupportedException)
        { return Reject<FileHandleResponse>("工程目录包含非法路径或链接。"); }
        lock (_gate)
        {
            if (_handles.Values.Count(handle => handle.Owner == clientId) >= options.MaxHandlesPerClient)
                return BackendOperationResult<FileHandleResponse>.Rejected(new(BackendErrorCode.ResourceQuotaExceeded, "目录句柄已达到上限。"));
            var response = new FileHandleResponse("handle-" + Guid.NewGuid().ToString("N"), request.RootGrantId,
                request.RelativePath.Replace('\\', '/'), request.Access == "readWrite");
            _handles.Add(response.HandleId, new(clientId, response, root, path));
            return BackendOperationResult<FileHandleResponse>.Success(response);
        }
    }

    internal void RemoveClient(string clientId)
    {
        lock (_gate)
            foreach (string id in _handles.Where(pair => pair.Value.Owner == clientId).Select(pair => pair.Key).ToArray())
                _handles.Remove(id);
    }

    internal Handle Get(string clientId, string id, bool write)
    {
        Handle handle;
        lock (_gate)
        {
            if (!_handles.TryGetValue(id, out handle!) || handle.Owner != clientId)
                throw new ResourceStoreException(BackendErrorCode.ResourceUnauthorized, "工程目录句柄不属于当前客户端。");
        }
        if (write && !handle.Response.CanWrite) throw new ResourceStoreException(BackendErrorCode.ReadOnlySession, "工程目录句柄只读。");
        string path = Resolve(handle.Root, handle.Response.RelativePath);
        if (path != handle.Path) throw new ResourceStoreException(BackendErrorCode.PathNotAllowed, "工程目录身份已改变。");
        return handle;
    }

    internal bool IsAuthorized(string path)
    {
        foreach (string root in _roots.Values)
        {
            string relative = Path.GetRelativePath(root, path);
            try { if (Resolve(root, relative) == Path.GetFullPath(path)) return true; }
            catch (Exception error) when (error is IOException or ArgumentException or NotSupportedException) { }
        }
        return false;
    }

    internal static string Resolve(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':'))
            throw new IOException("相对目录不能为空或是绝对路径。");
        string[] parts = relative.Split(['/', '\\'], StringSplitOptions.None);
        if (parts.Any(part => part.Length == 0 || part is "." or "..")) throw new IOException("相对目录包含越界片段。");
        string current = root;
        foreach (string part in parts)
        {
            current = Path.Combine(current, part);
            if (new DirectoryInfo(current).LinkTarget is not null || new FileInfo(current).LinkTarget is not null)
                throw new IOException("相对目录不能包含链接。");
            if (File.Exists(current)) throw new IOException("目录路径不能包含普通文件。");
        }
        return Path.GetFullPath(current);
    }

    internal static void EnsurePathHasNoLinks(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if (new FileInfo(current).LinkTarget is not null || new DirectoryInfo(current).LinkTarget is not null)
                throw new IOException("固定资源路径不能经过文件或目录链接。");
    }

    internal static void EnsureNoLinks(string directory)
    {
        if (!Directory.Exists(directory)) return;
        int entries = 0;
        var pending = new Stack<string>(); pending.Push(directory);
        while (pending.Count > 0)
        {
            foreach (string entry in Directory.EnumerateFileSystemEntries(pending.Pop()))
            {
                if (++entries > 100_000) throw new IOException("工程闭包条目数量超过可安全检查上限。");
                FileSystemInfo info = Directory.Exists(entry) ? new DirectoryInfo(entry) : new FileInfo(entry);
                if (info.LinkTarget is not null) throw new IOException("工程闭包不能包含链接。");
                if (info is DirectoryInfo) pending.Push(entry);
            }
        }
    }

    private static BackendOperationResult<T> Reject<T>(string message) =>
        BackendOperationResult<T>.Rejected(new(BackendErrorCode.PathNotAllowed, message));
}
