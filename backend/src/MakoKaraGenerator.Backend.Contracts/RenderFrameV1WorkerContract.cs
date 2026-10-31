using System.Buffers.Binary;

namespace MakoKaraGenerator.Backend.Contracts;

/// <summary>跨进程 render-frame-v1 的 40 字节静态背景输入，不包含工程、歌词、资源或脚本。</summary>
public sealed record RenderFrameV1WorkerRequest
{
    /// <summary>固定输入字节数。</summary>
    public const int EncodedLength = 40;
    /// <summary>允许的最大像素宽度；仍受总像素字节预算约束。</summary>
    public const int MaxWidth = 1920;
    /// <summary>允许的最大像素高度；仍受总像素字节预算约束。</summary>
    public const int MaxHeight = 1080;
    /// <summary>输出 BGRA 像素的最大总字节数。</summary>
    public const int MaxPixelBytes = WorkerProtocol.MaxPayloadBytes;
    private const uint Magic = 0x3152464B; // "KFR1" in little-endian.
    private const byte Version = 1;

    /// <summary>创建受尺寸、非负微秒与 4 MiB 输出预算约束的固定输入。</summary>
    public RenderFrameV1WorkerRequest(int width, int height, long timeUs, bool transparentBackground,
        byte backgroundR, byte backgroundG, byte backgroundB, byte backgroundA = 255)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (width > MaxWidth)
            throw new ArgumentOutOfRangeException(nameof(width), "render-frame-v1 输入宽度超出上限。");
        if (height > MaxHeight)
            throw new ArgumentOutOfRangeException(nameof(height), "render-frame-v1 输入高度超出上限。");
        if ((long)width * height * 4 > MaxPixelBytes)
            throw new ArgumentOutOfRangeException(nameof(width), "render-frame-v1 输入像素预算不足。");
        ArgumentOutOfRangeException.ThrowIfNegative(timeUs);
        Width = width;
        Height = height;
        TimeUs = timeUs;
        TransparentBackground = transparentBackground;
        BackgroundR = backgroundR;
        BackgroundG = backgroundG;
        BackgroundB = backgroundB;
        BackgroundA = backgroundA;
    }

    /// <summary>输出像素宽度。</summary>
    public int Width { get; }
    /// <summary>输出像素高度。</summary>
    public int Height { get; }
    /// <summary>渲染目标时间，单位为整数微秒。</summary>
    public long TimeUs { get; }
    /// <summary>为 true 时跳过背景色并输出透明画布。</summary>
    public bool TransparentBackground { get; }
    /// <summary>背景红色分量。</summary>
    public byte BackgroundR { get; }
    /// <summary>背景绿色分量。</summary>
    public byte BackgroundG { get; }
    /// <summary>背景蓝色分量。</summary>
    public byte BackgroundB { get; }
    /// <summary>背景 alpha 分量；返回像素采用预乘语义。</summary>
    public byte BackgroundA { get; }

    /// <summary>固定 40 字节 little-endian 输入；不使用 JSON 或工程文件 DTO。</summary>
    public byte[] Encode()
    {
        byte[] bytes = new byte[EncodedLength];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, Magic);
        bytes[4] = Version;
        bytes[5] = TransparentBackground ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6), 0);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), Width);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(12), Height);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(16), TimeUs);
        bytes[24] = BackgroundR;
        bytes[25] = BackgroundG;
        bytes[26] = BackgroundB;
        bytes[27] = BackgroundA;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28), 0);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(32), 0);
        return bytes;
    }

    /// <summary>解码并拒绝长度、版本、保留字段或取值不符合合同的输入。</summary>
    public static RenderFrameV1WorkerRequest Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != EncodedLength)
            throw new WorkerProtocolException($"render-frame-v1 输入必须为 {EncodedLength} 字节。");
        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes) != Magic || bytes[4] != Version)
            throw new WorkerProtocolException("render-frame-v1 输入 magic/version 不匹配。");
        if ((bytes[5] & ~1) != 0 || BinaryPrimitives.ReadUInt16LittleEndian(bytes[6..]) != 0
            || BinaryPrimitives.ReadUInt32LittleEndian(bytes[28..]) != 0
            || BinaryPrimitives.ReadUInt64LittleEndian(bytes[32..]) != 0)
            throw new WorkerProtocolException("render-frame-v1 输入保留字段不为零。");
        try
        {
            return new RenderFrameV1WorkerRequest(
                BinaryPrimitives.ReadInt32LittleEndian(bytes[8..]),
                BinaryPrimitives.ReadInt32LittleEndian(bytes[12..]),
                BinaryPrimitives.ReadInt64LittleEndian(bytes[16..]),
                (bytes[5] & 1) != 0, bytes[24], bytes[25], bytes[26], bytes[27]);
        }
        catch (ArgumentOutOfRangeException error)
        {
            throw new WorkerProtocolException($"render-frame-v1 输入字段越界：{error.Message}");
        }
    }

    /// <summary>计算固定输入字节的 SHA-256。</summary>
    public static string ComputeInputSha256(ReadOnlySpan<byte> encoded) => WorkerProtocol.ComputeSha256(encoded);
}

/// <summary>跨进程 render-frame-v1 输出元数据和像素所有权。</summary>
public sealed record RenderFrameV1WorkerResult(
    string RequestId,
    byte[] Pixels,
    string InputSha256,
    string OutputSha256,
    int Width,
    int Height,
    int Stride,
    long TimeUs,
    string PixelFormat)
{
    /// <summary>唯一接受的紧密行布局：BGRA 8-bit 各分量，alpha 预乘。</summary>
    public const string RequiredPixelFormat = "bgra8888-premultiplied";

    /// <summary>按原请求验证回执、尺寸、紧密步长、时间、像素预算及独立输出哈希。</summary>
    public static RenderFrameV1WorkerResult FromMessage(RenderFrameV1WorkerRequest request, WorkerMessage message)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(message);
        WorkerProtocol.Validate(message);
        if (message.Kind != "result" || message.Cancelled || message.Error is not null
            || message.OutputSha256 is null || message.FrameWidth is null || message.FrameHeight is null
            || message.FrameStride is null || message.FrameTimeUs is null || message.FrameFormat is null)
            throw new WorkerProtocolException("render-frame-v1 回执缺少帧元数据或终态无效。");
        // 只使用已验证的请求尺寸计算长度，避免不受信回执的整型乘法溢出。
        int stride = request.Width * 4;
        int required = stride * request.Height;
        string inputHash = RenderFrameV1WorkerRequest.ComputeInputSha256(request.Encode());
        if (!string.Equals(message.InputSha256, inputHash, StringComparison.OrdinalIgnoreCase)
            || message.FrameWidth != request.Width || message.FrameHeight != request.Height
            || message.FrameTimeUs != request.TimeUs || message.FrameStride != stride
            || message.FrameFormat != RequiredPixelFormat || message.Payload.Length != required)
            throw new WorkerProtocolException("render-frame-v1 回执不符合原请求与固定输出合同。");
        return new(message.RequestId, message.Payload, inputHash, WorkerProtocol.ComputeSha256(message.Payload),
            request.Width, request.Height, stride, request.TimeUs, RequiredPixelFormat);
    }
}
