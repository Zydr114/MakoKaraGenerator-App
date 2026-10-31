using System.Collections.Concurrent;
using MakoKaraGenerator.Core.Persistence;

namespace MakoKaraGenerator.Application;

/// <summary>同索引的进程内互斥及OS文件范围锁；锁文件保留，但其存在不代表持锁。</summary>
public sealed class LibraryCommitLease : IDisposable
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> LocalGates =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly FileStream _stream;
    private readonly SemaphoreSlim _local;
    private bool _disposed;
    private LibraryCommitLease(FileStream stream, SemaphoreSlim local) { _stream = stream; _local = local; }
    /// <summary>协作锁路径；应用从不删除或替换该文件。</summary>
    public static string LockPath(string indexPath) => Path.GetFullPath(indexPath) + ".commit.lock";
    /// <summary>立即尝试提交权；不在UI线程等待另一作者的磁盘提交。</summary>
    public static LibraryCommitLease TryEnter(string indexPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string path = Prepare(indexPath); SemaphoreSlim local = LocalGates.GetOrAdd(path, _ => new(1, 1));
        if (!local.Wait(0, cancellationToken)) throw new LibraryBusyException();
        try { return new(OpenAndLock(path), local); }
        catch { local.Release(); throw; }
    }
    /// <summary>异步等待协作提交权，取消只撤回本请求；持锁进程退出后OS自动释放锁。</summary>
    public static async Task<LibraryCommitLease> EnterAsync(string indexPath, CancellationToken cancellationToken = default)
    {
        string path = Prepare(indexPath); SemaphoreSlim local = LocalGates.GetOrAdd(path, _ => new(1, 1));
        await local.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try { return new(OpenAndLock(path), local); }
                catch (LibraryBusyException) { await Task.Delay(30, cancellationToken).ConfigureAwait(false); }
            }
        }
        catch { local.Release(); throw; }
    }
    private static string Prepare(string indexPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexPath);
        LibraryStoragePath.RequireNoLinks(indexPath);
        string path = LockPath(indexPath);
        LibraryStoragePath.RequireNoLinks(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return path;
    }
    private static FileStream OpenAndLock(string path)
    {
        FileStream? stream = null;
        try
        {
            LibraryStoragePath.RequireNoLinks(path);
            stream = new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
            // FileShare on Unix is best-effort; explicit Lock must succeed or this write is refused.
            if (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("Library commit locks require Linux or Windows.");
            if (OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("macOS is not a supported host.");
            stream.Lock(0, 1);
            return stream;
        }
        catch (IOException error) when ((error.HResult & 0xffff) is 11 or 32 or 33)
        { stream?.Dispose(); throw new LibraryBusyException(); }
        catch { stream?.Dispose(); throw; }
    }
    /// <summary>关闭OS持有的文件描述符，再释放进程内提交权；重复释放幂等。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _stream.Dispose(); }
        finally { _local.Release(); GC.SuppressFinalize(this); }
    }
}

/// <summary>当前库有另一项提交，调用方可重试但不能绕过协作锁。</summary>
public sealed class LibraryBusyException : IOException
{
    /// <summary>构造尚未取得提交权的结果。</summary>
    public LibraryBusyException() : base("Library commit is busy.") { }
}
