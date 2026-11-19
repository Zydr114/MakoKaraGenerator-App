using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace MakoKaraGenerator.Rendering.Skia;

/// <summary>Windows 专属 Local\ 命名空间共享内存池后备实现；强制当前用户 SID 独占受保护 DACL 并严格配对 UnmapViewOfFile + CloseHandle。</summary>
[SupportedOSPlatform("windows")]
internal sealed partial class WindowsShmBacking : IShmPoolBacking
{
    private const uint TokenQuery = 0x0008u;
    private const uint TokenUserClass = 1u;
    private const uint SddlRevision1 = 1u;
    private const uint PageReadWrite = 0x04u;
    private const uint SecCommit = 0x08000000u;
    private const uint FileMapReadWrite = 0x0004u | 0x0002u;
    private const int ErrorAlreadyExists = 183;
    private const int ErrorInsufficientBuffer = 122;
    private static readonly IntPtr InvalidHandleValue = new(-1);

    private IntPtr _sectionHandle;
    private IntPtr _pointer;
    private int _disposed;

    private WindowsShmBacking(string resourceName, IntPtr sectionHandle, IntPtr pointer, ulong totalBytes)
    {
        ResourceName = resourceName;
        _sectionHandle = sectionHandle;
        _pointer = pointer;
        TotalBytes = totalBytes;
    }

    public string ResourceName { get; }

    public IntPtr BasePointer
    {
        get
        {
            IntPtr current = Volatile.Read(ref _pointer);
            return Volatile.Read(ref _disposed) != 0 || current == IntPtr.Zero
                ? throw new ObjectDisposedException(nameof(WindowsShmBacking))
                : current;
        }
    }

    public ulong TotalBytes { get; }

    public static WindowsShmBacking Create(ulong totalBytes, string? token = null)
    {
        if (totalBytes is 0 or > 64UL * 1024 * 1024)
            throw new GpuPreviewException("GpuPreviewPoolBudgetExceeded", "SHM preview pool allocation must be within 1..67108864 bytes.");

        string safeToken = ValidateOrGenerateToken(token);
        string resourceName = "Local\\mkg-shm-" + safeToken;
        string userSid = GetCurrentUserSidString();
        string sddl = $"D:P(A;;GA;;;{userSid})";

        if (ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl, SddlRevision1, out IntPtr securityDescriptor, IntPtr.Zero) == 0 ||
            securityDescriptor == IntPtr.Zero)
        {
            int err = Marshal.GetLastPInvokeError();
            throw new GpuPreviewException("PreviewShmSecurityFailed", $"无法构造当前用户 SID 共享内存安全描述符（win32={err}）。");
        }

        IntPtr sectionHandle = IntPtr.Zero;
        try
        {
            var sa = new SecurityAttributes
            {
                Length = (uint)Marshal.SizeOf<SecurityAttributes>(),
                SecurityDescriptor = securityDescriptor,
                InheritHandle = 0,
            };

            uint maxHigh = (uint)(totalBytes >> 32);
            uint maxLow = (uint)(totalBytes & 0xFFFF_FFFFUL);
            sectionHandle = CreateFileMappingW(InvalidHandleValue, ref sa, PageReadWrite | SecCommit, maxHigh, maxLow, resourceName);
            int lastError = Marshal.GetLastPInvokeError();
            if (sectionHandle == IntPtr.Zero)
            {
                if (lastError is 8 or 1455) // ERROR_NOT_ENOUGH_MEMORY / ERROR_COMMITMENT_LIMIT
                {
                    throw new GpuPreviewException("GpuPreviewOutOfMemory", $"系统提交内存不足以分配 {totalBytes} 字节共享内存池（win32={lastError}）。");
                }

                throw new GpuPreviewException("PreviewShmCreateFailed", $"无法创建 Windows 共享内存对象（win32={lastError}）。");
            }

            if (lastError == ErrorAlreadyExists)
            {
                throw new GpuPreviewException("PreviewShmAlreadyExists", $"Windows 共享内存对象已存在，拒绝复用：{resourceName}。");
            }

            IntPtr mapped = MapViewOfFile(sectionHandle, FileMapReadWrite, 0, 0, checked((nuint)totalBytes));
            if (mapped == IntPtr.Zero)
            {
                int err = Marshal.GetLastPInvokeError();
                throw new GpuPreviewException("PreviewShmMapFailed", $"无法映射 Windows 共享内存视图（win32={err}）。");
            }

            IntPtr transferredHandle = sectionHandle;
            sectionHandle = IntPtr.Zero;
            return new WindowsShmBacking(resourceName, transferredHandle, mapped, totalBytes);
        }
        finally
        {
            if (sectionHandle != IntPtr.Zero)
            {
                _ = CloseHandle(sectionHandle);
            }

            _ = LocalFree(securityDescriptor);
        }
    }

    public void Dispose()
    {
        ReleaseNative();
        GC.SuppressFinalize(this);
    }

    ~WindowsShmBacking() => ReleaseNative();

    private void ReleaseNative()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        IntPtr currentPtr = Interlocked.Exchange(ref _pointer, IntPtr.Zero);
        if (currentPtr != IntPtr.Zero)
        {
            _ = UnmapViewOfFile(currentPtr);
        }

        IntPtr currentHandle = Interlocked.Exchange(ref _sectionHandle, IntPtr.Zero);
        if (currentHandle != IntPtr.Zero)
        {
            _ = CloseHandle(currentHandle);
        }
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

    private static unsafe string GetCurrentUserSidString()
    {
        if (OpenProcessToken(GetCurrentProcess(), TokenQuery, out IntPtr tokenHandle) == 0 || tokenHandle == IntPtr.Zero)
        {
            int err = Marshal.GetLastPInvokeError();
            throw new GpuPreviewException("PreviewShmSecurityFailed", $"无法打开当前进程访问令牌（win32={err}）。");
        }

        try
        {
            _ = GetTokenInformation(tokenHandle, TokenUserClass, IntPtr.Zero, 0, out uint needed);
            int queryErr = Marshal.GetLastPInvokeError();
            if (needed == 0 || (queryErr != ErrorInsufficientBuffer && queryErr != 0))
            {
                throw new GpuPreviewException("PreviewShmSecurityFailed", $"无法查询当前进程用户 SID 长度（win32={queryErr}）。");
            }

            byte[] buffer = new byte[needed];
            fixed (byte* ptr = buffer)
            {
                if (GetTokenInformation(tokenHandle, TokenUserClass, (IntPtr)ptr, needed, out _) == 0)
                {
                    int err = Marshal.GetLastPInvokeError();
                    throw new GpuPreviewException("PreviewShmSecurityFailed", $"无法读取当前进程用户 SID（win32={err}）。");
                }

                // TOKEN_USER 首字段即为 SID_AND_ATTRIBUTES.Sid (PSID)
                IntPtr sidPtr = *(IntPtr*)ptr;
                if (sidPtr == IntPtr.Zero || ConvertSidToStringSidW(sidPtr, out IntPtr sidStringPtr) == 0 || sidStringPtr == IntPtr.Zero)
                {
                    int err = Marshal.GetLastPInvokeError();
                    throw new GpuPreviewException("PreviewShmSecurityFailed", $"无法格式化当前进程用户 SID（win32={err}）。");
                }

                try
                {
                    string? sid = Marshal.PtrToStringUni(sidStringPtr);
                    if (string.IsNullOrWhiteSpace(sid))
                    {
                        throw new GpuPreviewException("PreviewShmSecurityFailed", "当前进程用户 SID 字符串为空。");
                    }

                    return sid;
                }
                finally
                {
                    _ = LocalFree(sidStringPtr);
                }
            }
        }
        finally
        {
            _ = CloseHandle(tokenHandle);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public uint Length;
        public IntPtr SecurityDescriptor;
        public int InheritHandle;
    }

    [GeneratedRegex("^[a-f0-9]{24,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex TokenPattern();

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GetCurrentProcess();

    [LibraryImport("advapi32.dll", SetLastError = true)]
    private static partial int OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    private static partial int GetTokenInformation(IntPtr tokenHandle, uint tokenInformationClass, IntPtr tokenInformation, uint tokenInformationLength, out uint returnLength);

    [LibraryImport("advapi32.dll", EntryPoint = "ConvertSidToStringSidW", SetLastError = true)]
    private static partial int ConvertSidToStringSidW(IntPtr sid, out IntPtr stringSid);

    [LibraryImport("advapi32.dll", EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int ConvertStringSecurityDescriptorToSecurityDescriptorW(string stringSecurityDescriptor, uint stringSDRevision, out IntPtr securityDescriptor, IntPtr securityDescriptorSize);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileMappingW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr CreateFileMappingW(IntPtr hFile, ref SecurityAttributes lpFileMappingAttributes, uint flProtect, uint dwMaximumSizeHigh, uint dwMaximumSizeLow, string lpName);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr MapViewOfFile(IntPtr hFileMappingObject, uint dwDesiredAccess, uint dwFileOffsetHigh, uint dwFileOffsetLow, nuint dwNumberOfBytesToMap);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int UnmapViewOfFile(IntPtr lpBaseAddress);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int CloseHandle(IntPtr hObject);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr LocalFree(IntPtr hMem);
}
