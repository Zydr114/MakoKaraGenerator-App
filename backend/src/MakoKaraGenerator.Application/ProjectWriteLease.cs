using System.Security.Cryptography;
using System.Text;

namespace MakoKaraGenerator.Application;

/// <summary>参与本合同的桌面与后端对同一物理工程目录只能持有一个写租约。</summary>
public sealed class ProjectWriteLease : IDisposable
{
    private static readonly object Gate = new();
    private static readonly HashSet<string> Held = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private FileStream? _lock;
    private int _references = 1;
    private bool _ownerReleased;
    private ProjectWriteLease(string directory, FileStream stream) => (DirectoryPath, _lock) = (directory, stream);
    /// <summary>规范化后的物理工程目录。</summary>
    public string DirectoryPath { get; }

    /// <summary>取得进程内和跨进程的唯一写锁；锁文件可留存，进程结束后操作系统释放锁。</summary>
    public static ProjectWriteLease Acquire(string directoryPath)
    {
        string physical = ResolvePhysicalPath(directoryPath);
        string key = OperatingSystem.IsWindows() ? physical.ToUpperInvariant() : physical;
        lock (Gate)
        {
            if (!Held.Add(physical)) throw new ProjectWriteLeaseConflictException(physical);
            FileStream? stream = null;
            try
            {
                // TMPDIR/XDG profile can differ between a desktop and server launched by the same user.
                string root = OperatingSystem.IsWindows()
                    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MakoKaraGenerator", "project-write-leases")
                    : Path.Combine("/tmp", "MakoKaraGenerator-write-leases-" + Environment.UserName);
                if (OperatingSystem.IsWindows()) Directory.CreateDirectory(root);
                else Directory.CreateDirectory(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                string file = Path.Combine(root, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))) + ".lock");
                // FileShare.None uses native sharing rules on Windows and a nonblocking flock on Unix,
                // including macOS. The persistent inode is never removed while another process may use it.
                try { stream = new FileStream(file, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
                catch (IOException exception) { throw new ProjectWriteLeaseConflictException(physical, exception); }
                return new(physical, stream);
            }
            catch { stream?.Dispose(); Held.Remove(physical); throw; }
        }
    }

    /// <summary>解析已有祖先的链接，未创建的工程目录保持其物理父目录身份。</summary>
    public static string ResolvePhysicalPath(string directoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        string full = Path.GetFullPath(directoryPath);
        string root = Path.GetPathRoot(full)!;
        string current = root;
        foreach (string part in full[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            var info = new DirectoryInfo(current);
            if (info.LinkTarget is not null)
                current = info.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? throw new IOException("工程目录链接无法解析。");
        }
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(current));
    }

    /// <summary>安全点之后释放租约；不删除锁文件，避免另一进程锁住已删除的旧节点。</summary>
    public void Dispose()
    {
        lock (Gate)
        {
            if (_ownerReleased) return;
            _ownerReleased = true;
            ReleaseCore();
        }
    }

    internal IDisposable Retain()
    {
        lock (Gate)
        {
            ObjectDisposedException.ThrowIf(_lock is null, this);
            _references++;
            return new Reference(this);
        }
    }

    private void ReleaseCore()
    {
            if (--_references != 0 || _lock is not { } stream) return;
            _lock = null;
            stream.Dispose();
            Held.Remove(DirectoryPath);
    }

    private sealed class Reference(ProjectWriteLease owner) : IDisposable
    {
        private ProjectWriteLease? _owner = owner;
        public void Dispose()
        { lock (Gate) { ProjectWriteLease? current = _owner; _owner = null; current?.ReleaseCore(); } }
    }
}

/// <summary>另一参与租约合同的会话已经拥有该工程目录。</summary>
public sealed class ProjectWriteLeaseConflictException : IOException
{
    /// <summary>建立明确的目录写冲突诊断。</summary>
    public ProjectWriteLeaseConflictException(string directoryPath, Exception? inner = null)
        : base($"工程目录已由另一个会话持有写租约：{directoryPath}", inner) { }
}

/// <summary>保存期间持有候选目标，实际发布成功后才能转交工作区。</summary>
public sealed class ProjectWriteLeaseReservation : IDisposable
{
    internal ProjectWriteLeaseReservation(ProjectWorkspace workspace, long generation, string path, ProjectWriteLease? lease,
        IDisposable? reference = null, ProjectWriteLease? existingLease = null)
        => (Workspace, Generation, DirectoryPath, Lease, _reference, _effectiveLease) = (workspace, generation, path, lease, reference, lease ?? existingLease);
    private IDisposable? _reference;
    private readonly ProjectWriteLease? _effectiveLease;
    internal ProjectWorkspace Workspace { get; }
    internal long Generation { get; }
    internal ProjectWriteLease? Lease { get; private set; }
    /// <summary>本次保存的规范目标。</summary>
    public string DirectoryPath { get; }
    /// <summary>为被另存为重定位的已接受备份保留同一实际写目标。</summary>
    public IDisposable? RetainReference() => _effectiveLease?.Retain();
    internal ProjectWriteLease? Take() { ProjectWriteLease? value = Lease; Lease = null; return value; }
    /// <summary>失败或取消时释放未转交的候选，旧工程租约不变。</summary>
    public void Dispose() { Lease?.Dispose(); Lease = null; _reference?.Dispose(); _reference = null; }
}
