using System.Buffers.Binary;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using MakoKaraGenerator.Backend.Contracts;

namespace MakoKaraGenerator.Backend.Worker;

/// <summary>平台无关的底层非阻塞字节流端点抽象；将定长帧定界逻辑与 OS 本地套接字/管道解耦。</summary>
internal interface ILocalStreamEndpoint : IDisposable
{
    bool TryAcceptPeer(int expectedPid);
    int TryRead(Span<byte> destination);
    int TryWrite(ReadOnlySpan<byte> source);
}

/// <summary>纯 .NET 本地流式预览控制通道（4 字节小端长度前缀 + 有界 UTF-8 JSON，SHM 专用，严禁传递附带句柄）。</summary>
internal sealed class LocalStreamPreviewChannel : IGpuStreamChannel
{
    private const int HeaderBytes = 4;
    private const int MaxFramedBytes = HeaderBytes + GpuStreamWorkerContract.MaximumBytes;

    private readonly ILocalStreamEndpoint _endpoint;
    private readonly byte[] _receiveBuffer = new byte[MaxFramedBytes];
    private int _receivedCount;
    private byte[]? _pendingWrite;
    private int _pendingWriteOffset;
    private bool _accepted;
    private bool _disposed;

    internal LocalStreamPreviewChannel(string endpointPath)
        : this(CreatePlatformEndpoint(endpointPath))
    {
    }

    internal LocalStreamPreviewChannel(ILocalStreamEndpoint endpoint)
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
    }

    public bool TryAccept(int expectedPid)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (expectedPid <= 0) throw GpuStreamProtocol.Invalid();
        if (_accepted) return true;
        _accepted = _endpoint.TryAcceptPeer(expectedPid);
        return _accepted;
    }

    public byte[]? TryReceive()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_accepted) return null;

        if (_receivedCount < HeaderBytes)
        {
            int readHeader = _endpoint.TryRead(_receiveBuffer.AsSpan(_receivedCount, HeaderBytes - _receivedCount));
            if (readHeader > 0) _receivedCount += readHeader;
            if (_receivedCount < HeaderBytes) return null;
        }

        uint payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(_receiveBuffer.AsSpan(0, HeaderBytes));
        if (payloadLength is 0 or > GpuStreamWorkerContract.MaximumBytes)
        {
            throw new WorkerProtocolException("预览流消息长度超出 1..16384 字节预算。", "WorkerGpuStreamProtocolInvalid");
        }

        int totalFrameLength = HeaderBytes + (int)payloadLength;
        if (_receivedCount < totalFrameLength)
        {
            int readBody = _endpoint.TryRead(_receiveBuffer.AsSpan(_receivedCount, totalFrameLength - _receivedCount));
            if (readBody > 0) _receivedCount += readBody;
            if (_receivedCount < totalFrameLength) return null;
        }

        byte[] payload = _receiveBuffer.AsSpan(HeaderBytes, (int)payloadLength).ToArray();
        _receivedCount = 0;
        return payload;
    }

    public bool TrySend(byte[] packet, SafeHandle? descriptor = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(packet);
        if (descriptor is not null)
        {
            throw new WorkerProtocolException("SHM 本地流式通道禁止发送系统文件句柄。", "WorkerGpuStreamProtocolInvalid");
        }

        if (packet.Length is 0 or > GpuStreamWorkerContract.MaximumBytes)
        {
            throw new WorkerProtocolException("预览流消息超出字节预算。", "WorkerGpuStreamProtocolInvalid");
        }

        if (!_accepted) return false;

        if (_pendingWrite is not null)
        {
            if (!FlushPendingWrite()) return false;
        }

        byte[] framed = new byte[HeaderBytes + packet.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(framed.AsSpan(0, HeaderBytes), (uint)packet.Length);
        Buffer.BlockCopy(packet, 0, framed, HeaderBytes, packet.Length);

        int written = _endpoint.TryWrite(framed);
        if (written == framed.Length) return true;
        if (written > 0)
        {
            _pendingWrite = framed;
            _pendingWriteOffset = written;
            return FlushPendingWrite();
        }

        return false;
    }

    private bool FlushPendingWrite()
    {
        if (_pendingWrite is null) return true;
        while (_pendingWriteOffset < _pendingWrite.Length)
        {
            int written = _endpoint.TryWrite(_pendingWrite.AsSpan(_pendingWriteOffset));
            if (written <= 0) return false;
            _pendingWriteOffset += written;
        }

        _pendingWrite = null;
        _pendingWriteOffset = 0;
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _endpoint.Dispose();
    }

    internal static ILocalStreamEndpoint CreatePlatformEndpoint(string endpointPath)
    {
        if (string.IsNullOrWhiteSpace(endpointPath) || endpointPath.Contains('\0'))
            throw GpuStreamProtocol.Invalid();

        if (OperatingSystem.IsLinux())
        {
            return new LinuxUdsStreamEndpoint(endpointPath);
        }

        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Windows NamedPipeServerStream (CurrentUserOnly + client PID verification) is deferred under Method B.");
        }

        throw new PlatformNotSupportedException(
            $"LocalStreamPreviewChannel is not supported on platform '{RuntimeInformation.OSDescription}'.");
    }
}

/// <summary>Linux 专属 AF_UNIX SOCK_STREAM 非阻塞端点，校验 SO_PEERCRED 同 UID 与精确消费者 PID。</summary>
[SupportedOSPlatform("linux")]
internal sealed class LinuxUdsStreamEndpoint : ILocalStreamEndpoint
{
    private const int SolSocket = 1;
    private const int SoPeerCred = 17;

    [StructLayout(LayoutKind.Sequential)]
    private struct UCred
    {
        public int Pid;
        public uint Uid;
        public uint Gid;
    }

    private readonly string _socketPath;
    private Socket? _listener;
    private Socket? _peer;
    private bool _disposed;

    internal LinuxUdsStreamEndpoint(string socketPath)
    {
        if (string.IsNullOrWhiteSpace(socketPath) || socketPath.Contains('\0') || !Path.IsPathRooted(socketPath))
            throw GpuStreamProtocol.Invalid();

        _socketPath = socketPath;
        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            listener.Bind(new UnixDomainSocketEndPoint(socketPath));
            File.SetUnixFileMode(socketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            listener.Listen(1);
            listener.Blocking = false;
            _listener = listener;
        }
        catch (Exception error)
        {
            listener.Dispose();
            TryDeleteSocketFile();
            throw new WorkerProtocolException($"无法监听本地预览流套接字：{error.Message}", "WorkerGpuTransportFailed");
        }
    }

    public bool TryAcceptPeer(int expectedPid)
    {
        if (_peer is not null) return true;
        if (_listener is null) throw new WorkerProtocolException("本地监听套接字已关闭。", "WorkerGpuTransportFailed");

        Socket accepted;
        try
        {
            accepted = _listener.Accept();
        }
        catch (SocketException ex) when (ex.SocketErrorCode is SocketError.WouldBlock or SocketError.TryAgain)
        {
            return false;
        }
        catch (Exception error)
        {
            throw new WorkerProtocolException($"本地预览通道 Accept 失败：{error.Message}", "WorkerGpuTransportFailed");
        }

        try
        {
            int rawFd = checked((int)accepted.SafeHandle.DangerousGetHandle());
            UCred cred = default;
            uint credLen = (uint)Marshal.SizeOf<UCred>();
            if (GetSockOpt(rawFd, SolSocket, SoPeerCred, ref cred, ref credLen) != 0 || credLen != Marshal.SizeOf<UCred>())
            {
                accepted.Dispose();
                throw new WorkerProtocolException("无法读取本地消费者进程凭证。", "WorkerGpuTransportFailed");
            }

            if (cred.Uid != GetEffectiveUid() || cred.Pid != expectedPid)
            {
                accepted.Dispose();
                throw new WorkerProtocolException("预览消费者进程身份不匹配。", "WorkerGpuConsumerMismatch");
            }

            accepted.Blocking = false;
            _peer = accepted;
            _listener.Dispose();
            _listener = null;
            TryDeleteSocketFile();
            return true;
        }
        catch
        {
            if (_peer is null) accepted.Dispose();
            throw;
        }
    }

    public int TryRead(Span<byte> destination)
    {
        if (_peer is null) throw new WorkerProtocolException("本地预览对端尚未连接。", "WorkerGpuTransportFailed");
        try
        {
            int read = _peer.Receive(destination, SocketFlags.None, out SocketError errorCode);
            if (errorCode is SocketError.WouldBlock or SocketError.TryAgain)
            {
                return 0;
            }

            if (errorCode != SocketError.Success || read == 0)
            {
                throw new WorkerProtocolException("本地预览消费者连接已断开；不推定槽位已排空。", "WorkerGpuTransportFailed");
            }

            return read;
        }
        catch (WorkerProtocolException)
        {
            throw;
        }
        catch (Exception error)
        {
            throw new WorkerProtocolException($"本地预览通道读取失败：{error.Message}", "WorkerGpuTransportFailed");
        }
    }

    public int TryWrite(ReadOnlySpan<byte> source)
    {
        if (_peer is null) throw new WorkerProtocolException("本地预览对端尚未连接。", "WorkerGpuTransportFailed");
        try
        {
            int written = _peer.Send(source, SocketFlags.None, out SocketError errorCode);
            if (errorCode is SocketError.WouldBlock or SocketError.TryAgain)
            {
                return 0;
            }

            if (errorCode != SocketError.Success)
            {
                throw new WorkerProtocolException("本地预览通道写入失败；不推定消费者已排空。", "WorkerGpuTransportFailed");
            }

            return written;
        }
        catch (WorkerProtocolException)
        {
            throw;
        }
        catch (Exception error)
        {
            throw new WorkerProtocolException($"本地预览通道发送失败：{error.Message}", "WorkerGpuTransportFailed");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _peer?.Dispose(); } catch { }
        _peer = null;
        try { _listener?.Dispose(); } catch { }
        _listener = null;
        TryDeleteSocketFile();
    }

    private void TryDeleteSocketFile()
    {
        try
        {
            if (File.Exists(_socketPath)) File.Delete(_socketPath);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [DllImport("libc", EntryPoint = "getsockopt", SetLastError = true)]
    private static extern int GetSockOpt(int sockfd, int level, int optname, ref UCred optval, ref uint optlen);

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEffectiveUid();
}
