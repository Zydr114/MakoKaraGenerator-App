using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using MakoKaraGenerator.Backend.Contracts;

namespace MakoKaraGenerator.Backend.Worker;

// This wrapper knows packet/FD ownership only; it never authorizes a GPU release.
internal interface IGpuStreamChannel : IDisposable
{
    bool TryAccept(int expectedPid);
    byte[]? TryReceive();
    bool TrySend(byte[] packet, SafeFileHandle? descriptor = null);
}

internal sealed class GpuNativeChannel : IGpuStreamChannel
{
    private IntPtr _listener;
    private IntPtr _peer;
    private readonly byte[] _receive = new byte[GpuStreamWorkerContract.MaximumBytes];
    internal GpuNativeChannel(string path)
    {
        Error error = Error.Create();
        if (string.IsNullOrEmpty(path) || path.Contains('\0')) throw GpuStreamProtocol.Invalid();
        Check(Listen(Encoding.UTF8.GetBytes(path + "\0"), out _listener, ref error));
    }
    public bool TryAccept(int expectedPid)
    {
        if (_peer != IntPtr.Zero) return true;
        Error error = Error.Create();
        int status = Accept(_listener, out IntPtr peer, out uint pid, ref error);
        if (status == 1) return false;
        Check(status);
        if (pid != expectedPid)
        {
            _ = Close(peer, ref error);
            throw new WorkerProtocolException("GPU consumer 进程身份不匹配。", "WorkerGpuConsumerMismatch");
        }
        _peer = peer;
        return true;
    }
    public byte[]? TryReceive()
    {
        Error error = Error.Create();
        int status = Receive(_peer, _receive, (uint)_receive.Length, out uint count, ref error);
        if (status == 1) return null;
        Check(status);
        return _receive.AsSpan(0, checked((int)count)).ToArray();
    }
    public bool TrySend(byte[] packet, SafeFileHandle? descriptor = null)
    {
        ArgumentNullException.ThrowIfNull(packet);
        if (packet.Length is 0 or > GpuStreamWorkerContract.MaximumBytes)
            throw new WorkerProtocolException("GPU 消息超出字节预算。", "WorkerGpuStreamProtocolInvalid");
        bool retained = false;
        try
        {
            descriptor?.DangerousAddRef(ref retained);
            int[]? fds = descriptor is null ? null : [checked((int)descriptor.DangerousGetHandle())];
            Error error = Error.Create();
            int status = Send(_peer, packet, (uint)packet.Length, fds, (uint)(fds?.Length ?? 0), ref error);
            if (status == 1) return false;
            Check(status);
            return true;
        }
        finally { if (retained) descriptor!.DangerousRelease(); }
    }
    public void Dispose()
    {
        Error error = Error.Create();
        if (_peer != IntPtr.Zero) { _ = Close(_peer, ref error); _peer = IntPtr.Zero; }
        if (_listener != IntPtr.Zero) { _ = Close(_listener, ref error); _listener = IntPtr.Zero; }
    }
    private static void Check(int status)
    {
        if (status != 0) throw new WorkerProtocolException("GPU 本机句柄通道失败；不推定消费者已排空。", "WorkerGpuTransportFailed");
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct Error
    {
        public uint Size, Version;
        public int Status, VkResult;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Message;
        internal static Error Create() => new() { Size = (uint)Marshal.SizeOf<Error>(), Version = 1, Message = string.Empty };
    }
    [DllImport("mako_gpu_preview", EntryPoint = "mkg_gpu_channel_listen", CallingConvention = CallingConvention.Cdecl)]
    private static extern int Listen(byte[] path, out IntPtr listener, ref Error error);
    [DllImport("mako_gpu_preview", EntryPoint = "mkg_gpu_channel_accept", CallingConvention = CallingConvention.Cdecl)]
    private static extern int Accept(IntPtr listener, out IntPtr peer, out uint pid, ref Error error);
    [DllImport("mako_gpu_preview", EntryPoint = "mkg_gpu_channel_send", CallingConvention = CallingConvention.Cdecl)]
    private static extern int Send(IntPtr peer, byte[] bytes, uint count, int[]? fds, uint fdCount, ref Error error);
    [DllImport("mako_gpu_preview", EntryPoint = "mkg_gpu_channel_receive_control", CallingConvention = CallingConvention.Cdecl)]
    private static extern int Receive(IntPtr peer, [Out] byte[] bytes, uint capacity, out uint count, ref Error error);
    [DllImport("mako_gpu_preview", EntryPoint = "mkg_gpu_channel_close", CallingConvention = CallingConvention.Cdecl)]
    private static extern int Close(IntPtr channel, ref Error error);
}
