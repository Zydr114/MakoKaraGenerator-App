using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace MakoKaraGenerator.Rendering.Skia;

/// <summary>Linux 专属 tmpfs 0600 共享内存池后备实现；映射后立即关闭本地 FD，配合消费端打开即 unlink 保证 SIGKILL 零残留。</summary>
[SupportedOSPlatform("linux")]
internal sealed partial class LinuxPosixShmBacking : IShmPoolBacking
{
    private const long TmpfsMagic = 0x01021994L;
    private const int OpenFlagsCreateExclRdwrCloexec = 0x02 | 0x40 | 0x80 | 0x80000;
    private const uint Mode0600 = 0x180; // 0600 octal
    private const int ProtReadWrite = 0x01 | 0x02;
    private const int MapShared = 0x01;
    private static readonly IntPtr MapFailed = new(-1);

    private IntPtr _pointer;
    private int _disposed;

    private LinuxPosixShmBacking(string resourcePath, IntPtr pointer, ulong totalBytes)
    {
        ResourceName = resourcePath;
        _pointer = pointer;
        TotalBytes = totalBytes;
    }

    public string ResourceName { get; }
    public IntPtr BasePointer => Volatile.Read(ref _disposed) != 0
        ? throw new ObjectDisposedException(nameof(LinuxPosixShmBacking))
        : _pointer;
    public ulong TotalBytes { get; }

    public static LinuxPosixShmBacking Create(ulong totalBytes, string? token = null)
    {
        if (totalBytes is 0 or > 64UL * 1024 * 1024)
            throw new GpuPreviewException("GpuPreviewPoolBudgetExceeded", "SHM preview pool allocation must be within 1..67108864 bytes.");

        string safeToken = ValidateOrGenerateToken(token);
        string tmpfsRoot = ResolveTmpfsDirectory();
        string fullPath = Path.Combine(tmpfsRoot, "mkg-shm-" + safeToken);

        int fd = PosixOpen(fullPath, OpenFlagsCreateExclRdwrCloexec, Mode0600);
        if (fd < 0)
        {
            int err = Marshal.GetLastPInvokeError();
            throw new GpuPreviewException("PreviewShmCreateFailed", $"无法创建 0600 共享内存对象（errno={err}）。");
        }

        try
        {
            if (PosixFchmod(fd, Mode0600) != 0)
            {
                int err = Marshal.GetLastPInvokeError();
                throw new GpuPreviewException("PreviewShmPermissionFailed", $"无法锁定共享内存 0600 权限（errno={err}）。");
            }

            // 预提交 tmpfs 物理页，避免运行期空间不足导致 ReadPixels 触发 SIGBUS。
            int fallocResult = PosixFallocate(fd, 0, checked((nint)totalBytes));
            if (fallocResult != 0)
            {
                if (fallocResult is 28 or 12) // ENOSPC / ENOMEM
                {
                    throw new GpuPreviewException("GpuPreviewOutOfMemory", $"tmpfs 剩余容量不足以提交 {totalBytes} 字节共享内存池（err={fallocResult}）。");
                }

                if (PosixFtruncate(fd, checked((nint)totalBytes)) != 0)
                {
                    int err = Marshal.GetLastPInvokeError();
                    throw new GpuPreviewException("PreviewShmResizeFailed", $"无法设定共享内存池大小（errno={err}）。");
                }
            }

            IntPtr mapped = PosixMmap(IntPtr.Zero, checked((nuint)totalBytes), ProtReadWrite, MapShared, fd, 0);
            if (mapped == MapFailed || mapped == IntPtr.Zero)
            {
                int err = Marshal.GetLastPInvokeError();
                throw new GpuPreviewException("PreviewShmMapFailed", $"无法映射共享内存池（errno={err}）。");
            }

            // 映射成功后内核 VMA 已持有底层对象引用，立即关闭 FD 保持进程描述符零增长。
            _ = PosixClose(fd);
            fd = -1;
            return new LinuxPosixShmBacking(fullPath, mapped, totalBytes);
        }
        catch
        {
            if (fd >= 0) _ = PosixClose(fd);
            _ = PosixUnlink(fullPath);
            throw;
        }
    }

    public static void TryCleanupUnopened(string token)
    {
        if (!TokenPattern().IsMatch(token)) return;
        foreach (string candidateRoot in EnumerateCandidateDirectories())
        {
            string candidate = Path.Combine(candidateRoot, "mkg-shm-" + token);
            _ = PosixUnlink(candidate);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        IntPtr current = Interlocked.Exchange(ref _pointer, IntPtr.Zero);
        if (current != IntPtr.Zero)
        {
            _ = PosixMunmap(current, checked((nuint)TotalBytes));
        }

        // 若消费者在 openReadOnly 中已先行 unlink，此处忽略 ENOENT 幂等兜底。
        _ = PosixUnlink(ResourceName);
    }

    private static string ValidateOrGenerateToken(string? token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        }

        if (!TokenPattern().IsMatch(token))
        {
            throw new GpuPreviewException("PreviewShmTokenInvalid", "SHM 对象令牌格式非法。");
        }

        return token;
    }

    private static IEnumerable<string> EnumerateCandidateDirectories()
    {
        yield return "/dev/shm";
        string? xdg = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (!string.IsNullOrWhiteSpace(xdg) && Path.IsPathRooted(xdg))
        {
            yield return xdg;
        }
    }

    private static unsafe string ResolveTmpfsDirectory()
    {
        Span<long> statfsBuffer = stackalloc long[32];
        foreach (string dir in EnumerateCandidateDirectories())
        {
            if (!Directory.Exists(dir)) continue;
            statfsBuffer.Clear();
            fixed (long* ptr = statfsBuffer)
            {
                if (PosixStatfs(dir, (IntPtr)ptr) == 0 && statfsBuffer[0] == TmpfsMagic)
                {
                    return dir;
                }
            }
        }

        throw new GpuPreviewException("PreviewShmTmpfsUnavailable", "未找到可用的 tmpfs 共享内存挂载点（/dev/shm 或 XDG_RUNTIME_DIR）。");
    }

    [GeneratedRegex("^[a-f0-9]{24,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex TokenPattern();

    [LibraryImport("libc", EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int PosixOpen(string pathname, int flags, uint mode);

    [LibraryImport("libc", EntryPoint = "fchmod", SetLastError = true)]
    private static partial int PosixFchmod(int fd, uint mode);

    [LibraryImport("libc", EntryPoint = "posix_fallocate")]
    private static partial int PosixFallocate(int fd, nint offset, nint len);

    [LibraryImport("libc", EntryPoint = "ftruncate", SetLastError = true)]
    private static partial int PosixFtruncate(int fd, nint length);

    [LibraryImport("libc", EntryPoint = "mmap", SetLastError = true)]
    private static partial IntPtr PosixMmap(IntPtr addr, nuint length, int prot, int flags, int fd, nint offset);

    [LibraryImport("libc", EntryPoint = "munmap", SetLastError = true)]
    private static partial int PosixMunmap(IntPtr addr, nuint length);

    [LibraryImport("libc", EntryPoint = "close", SetLastError = true)]
    private static partial int PosixClose(int fd);

    [LibraryImport("libc", EntryPoint = "unlink", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int PosixUnlink(string pathname);

    [LibraryImport("libc", EntryPoint = "statfs", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int PosixStatfs(string path, IntPtr buf);
}
