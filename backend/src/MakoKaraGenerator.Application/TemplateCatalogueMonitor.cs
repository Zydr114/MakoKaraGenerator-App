namespace MakoKaraGenerator.Application;

// Filesystem events are hints, never directory content or revision authority.
internal sealed class TemplateCatalogueMonitor(string indexPath, Action<bool> invalidated) : IDisposable
{
    private readonly object _gate = new();
    private readonly string _root = Path.GetDirectoryName(Path.GetFullPath(indexPath))!;
    private FileSystemWatcher? _watcher;
    private bool _disposed;
    internal void EnsureWatching()
    {
        lock (_gate)
        {
            if (_disposed) return;
            string directory = _root;
            while (!Directory.Exists(directory)) directory = Path.GetDirectoryName(directory)
                ?? throw new DirectoryNotFoundException(_root);
            bool recursive = string.Equals(directory, _root, StringComparison.Ordinal);
            if (_watcher?.Path == directory && _watcher.IncludeSubdirectories == recursive) return;
            _watcher?.Dispose();
            var watcher = new FileSystemWatcher(directory)
            {
                IncludeSubdirectories = recursive,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            watcher.Changed += OnChanged; watcher.Created += OnChanged; watcher.Deleted += OnChanged;
            watcher.Renamed += (_, args) => { Hint(args.OldFullPath); Hint(args.FullPath); };
            watcher.Error += (_, _) => invalidated(true);
            _watcher = watcher; watcher.EnableRaisingEvents = true;
        }
    }
    private void OnChanged(object sender, FileSystemEventArgs args) => Hint(args.FullPath);
    private void Hint(string path)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        bool index = string.Equals(path, Path.GetFullPath(indexPath), comparison);
        string resources = Path.Combine(_root, "packages");
        bool package = string.Equals(path, resources, comparison) || path.StartsWith(resources + Path.DirectorySeparatorChar, comparison);
        bool ancestor = string.Equals(path, _root, comparison) || _root.StartsWith(path + Path.DirectorySeparatorChar, comparison);
        if (index || package || ancestor) invalidated(package || ancestor);
    }
    public void Dispose()
    {
        lock (_gate) { _disposed = true; _watcher?.Dispose(); _watcher = null; }
        GC.SuppressFinalize(this);
    }
}
