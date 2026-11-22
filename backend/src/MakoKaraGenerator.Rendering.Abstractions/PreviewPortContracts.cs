using System.Collections.Immutable;
using System.Runtime.InteropServices;

namespace MakoKaraGenerator.Rendering;

/// <summary>连续预览帧传输模式；公共层不假定特定操作系统内核对象。</summary>
public enum PreviewTransportKind
{
    /// <summary>跨平台三槽共享内存模式（支持 Vulkan 与 CPU 光栅）。</summary>
    Shm,
    /// <summary>零拷贝共享 GPU 缓冲区模式。</summary>
    DmaBuf,
}

/// <summary>连续预览帧像素存储格式（均为预乘 Alpha）。</summary>
public enum PreviewPixelFormat
{
    /// <summary>每像素 4 字节 BGRA8888 预乘 Alpha。</summary>
    Bgra8888,
    /// <summary>每像素 4 字节 RGBA8888 预乘 Alpha。</summary>
    Rgba8888,
}

/// <summary>预览传输与像素格式规范字符串转换。</summary>
public static class PreviewWireConventions
{
    /// <summary>CPU 光栅渲染设备标识。</summary>
    public const string ApiCpu = "cpu";
    /// <summary>Vulkan GPU 渲染设备标识。</summary>
    public const string ApiVulkan = "vulkan";
    /// <summary>预留的 Metal GPU 渲染设备标识。</summary>
    public const string ApiMetal = "metal";

    /// <summary>转换为协议规范传输名。</summary>
    public static string ToWireName(this PreviewTransportKind transport) => transport switch
    {
        PreviewTransportKind.Shm => "shm",
        PreviewTransportKind.DmaBuf => "dma-buf",
        _ => throw new ArgumentOutOfRangeException(nameof(transport), transport, "未知预览传输类型。"),
    };

    /// <summary>解析协议规范传输名；未知值明确报错，禁止静默回退。</summary>
    public static PreviewTransportKind ParseTransport(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Equals("shm", StringComparison.OrdinalIgnoreCase)) return PreviewTransportKind.Shm;
        if (value.Equals("dma-buf", StringComparison.OrdinalIgnoreCase)) return PreviewTransportKind.DmaBuf;
        throw new GpuPreviewException("PreviewTransportInvalid", $"未知预览传输模式 '{value}'；仅允许 shm 或 dma-buf。");
    }

    /// <summary>转换为协议规范像素格式名。</summary>
    public static string ToWireName(this PreviewPixelFormat format) => format switch
    {
        PreviewPixelFormat.Bgra8888 => "bgra8888",
        PreviewPixelFormat.Rgba8888 => "rgba8888",
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "未知预览像素格式。"),
    };

    /// <summary>解析协议规范像素格式名；未知值明确报错。</summary>
    public static PreviewPixelFormat ParsePixelFormat(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Equals("bgra8888", StringComparison.OrdinalIgnoreCase)) return PreviewPixelFormat.Bgra8888;
        if (value.Equals("rgba8888", StringComparison.OrdinalIgnoreCase)) return PreviewPixelFormat.Rgba8888;
        throw new GpuPreviewException("PreviewUnsupportedPixelFormat", $"不支持的预览像素格式 '{value}'；仅允许 bgra8888 或 rgba8888。");
    }

    /// <summary>规范化并校验渲染设备 API 名称。</summary>
    public static string NormalizeBackendApi(string api)
    {
        ArgumentNullException.ThrowIfNull(api);
        if (api.Equals(ApiCpu, StringComparison.OrdinalIgnoreCase)) return ApiCpu;
        if (api.Equals(ApiVulkan, StringComparison.OrdinalIgnoreCase)) return ApiVulkan;
        if (api.Equals(ApiMetal, StringComparison.OrdinalIgnoreCase)) return ApiMetal;
        throw new GpuPreviewException("PreviewUnsupportedBackendApi", $"不支持的预览设备 API '{api}'；允许值为 cpu、vulkan 或 metal。");
    }
}

/// <summary>可选的物理 GPU 选卡提示；SHM 模式下传空以允许独显渲染与核显显示解耦。</summary>
/// <param name="VendorId">PCI 厂商 ID。</param>
/// <param name="DeviceId">PCI 设备 ID。</param>
/// <param name="DrmRenderMajor">可选的 DRM Render 节点主设备号。</param>
/// <param name="DrmRenderMinor">可选的 DRM Render 节点次设备号。</param>
/// <param name="DeviceLuidHex">可选的 8 字节设备 LUID 十六进制表示。</param>
/// <param name="MetalRegistryId">可选的 Metal Registry ID。</param>
public sealed record PreferredGpuDevice(
    uint? VendorId = null,
    uint? DeviceId = null,
    uint? DrmRenderMajor = null,
    uint? DrmRenderMinor = null,
    string? DeviceLuidHex = null,
    ulong? MetalRegistryId = null);

/// <summary>平台中立的预览渲染设备公开身份，不假定特定内核子系统字段必填。</summary>
/// <param name="BackendApi">渲染后端 API（cpu、vulkan 或 metal）。</param>
/// <param name="Name">设备可读名称。</param>
/// <param name="VendorId">可选的 PCI 厂商 ID。</param>
/// <param name="DeviceId">可选的 PCI 设备 ID。</param>
/// <param name="DrmRenderMajor">可选的 DRM Render 节点主设备号。</param>
/// <param name="DrmRenderMinor">可选的 DRM Render 节点次设备号。</param>
/// <param name="DeviceUuid">可选的设备 UUID 十六进制字符串。</param>
/// <param name="DeviceLuidHex">可选的设备 LUID 十六进制字符串。</param>
/// <param name="MetalRegistryId">可选的 Metal Registry ID。</param>
public sealed record PreviewDeviceIdentity(
    string BackendApi,
    string Name,
    uint? VendorId = null,
    uint? DeviceId = null,
    uint? DrmRenderMajor = null,
    uint? DrmRenderMinor = null,
    string? DeviceUuid = null,
    string? DeviceLuidHex = null,
    ulong? MetalRegistryId = null)
{
    /// <summary>从既有 Vulkan GPU 身份构造平台中立设备描述。</summary>
    public static PreviewDeviceIdentity FromGpuDevice(GpuDeviceIdentity device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return new(PreviewWireConventions.ApiVulkan, device.Name, device.VendorId, device.DeviceId,
            device.DrmRenderMajor, device.DrmRenderMinor, device.DeviceUuid);
    }
}

/// <summary>连续预览池初始化选项。</summary>
/// <param name="Transport">显式指定的传输模式；为空时按平台与设备默认规则解析。</param>
/// <param name="PixelFormat">目标像素格式（默认 bgra8888）。</param>
/// <param name="StrideBytes">可选的显式行步长字节数；为 0 时使用紧凑步长 width * 4。</param>
/// <param name="PreferredGpuDevice">可选的同卡零拷贝选卡提示。</param>
/// <param name="ShmToken">可选的共享内存对象随机标识令牌，供宿主在异常窗口幂等兜底清理。</param>
public sealed record PreviewOutputOptions(
    PreviewTransportKind? Transport = null,
    PreviewPixelFormat PixelFormat = PreviewPixelFormat.Bgra8888,
    uint StrideBytes = 0,
    PreferredGpuDevice? PreferredGpuDevice = null,
    string? ShmToken = null);

/// <summary>实际分配后的预览池元数据；同时覆盖 SHM 与零拷贝池。</summary>
/// <param name="TransportKind">实际生效的传输模式。</param>
/// <param name="BackendApi">实际生效的渲染设备 API。</param>
/// <param name="ProducerEpoch">生产者实例随机代次。</param>
/// <param name="PoolGeneration">本会话池重建代次。</param>
/// <param name="Width">帧像素宽度。</param>
/// <param name="Height">帧像素高度。</param>
/// <param name="PixelFormat">池锁定的预乘像素格式。</param>
/// <param name="StrideBytes">单行字节步长。</param>
/// <param name="SlotCount">固定槽位数（3 槽）。</param>
/// <param name="AllocationBytes">本池实际分配总字节数。</param>
/// <param name="Device">渲染设备身份。</param>
/// <param name="ShmResourceName">SHM 模式下的跨进程共享内存标识；非 SHM 模式为空。</param>
public sealed record PreviewPoolInfo(
    PreviewTransportKind TransportKind,
    string BackendApi,
    ulong ProducerEpoch,
    ulong PoolGeneration,
    int Width,
    int Height,
    PreviewPixelFormat PixelFormat,
    uint StrideBytes,
    uint SlotCount,
    ulong AllocationBytes,
    PreviewDeviceIdentity Device,
    string? ShmResourceName = null)
{
    /// <summary>本会话所有尚存旧／新预览池的真实分配总量。</summary>
    public ulong RetainedAllocationBytes { get; init; }
}

/// <summary>生产者、池与槽位代际共同限定的精确预览帧释放标识。</summary>
/// <param name="ProducerEpoch">生产者代次。</param>
/// <param name="PoolGeneration">池代次。</param>
/// <param name="FrameSequence">严格递增帧序号。</param>
/// <param name="SlotEpoch">槽位复用代次。</param>
/// <param name="SlotId">槽位索引（0..2）。</param>
public sealed record PreviewFrameToken(
    ulong ProducerEpoch,
    ulong PoolGeneration,
    ulong FrameSequence,
    ulong SlotEpoch,
    uint SlotId)
{
    /// <summary>与既有 GPU 帧令牌互转。</summary>
    public GpuFrameToken ToGpuToken() => new(ProducerEpoch, PoolGeneration, FrameSequence, SlotEpoch, SlotId);

    /// <summary>从既有 GPU 帧令牌构造。</summary>
    public static PreviewFrameToken FromGpuToken(GpuFrameToken token)
    {
        ArgumentNullException.ThrowIfNull(token);
        return new(token.ProducerEpoch, token.PoolGeneration, token.FrameSequence, token.SlotEpoch, token.SlotId);
    }
}

/// <summary>预览帧分阶段主机耗时统计（微秒）。</summary>
/// <param name="RenderDurationUs">PrepareAsync 资源准备与 Skia 绘制命令录制的纯主机侧耗时（微秒）。</param>
/// <param name="PortCaptureDurationUs">IPreviewFrameTarget.Commit() 端口捕获与同步等待耗时（微秒）。</param>
public sealed record PreviewFrameTimings(long RenderDurationUs, long PortCaptureDurationUs);

/// <summary>预览帧底层拷贝与同步计数器。</summary>
/// <param name="GpuBlits">GPU Blit 次数。</param>
/// <param name="CpuPixelReadbacks">CPU 像素回读次数（DMA-BUF 为 0，SHM 为 1）。</param>
/// <param name="QueueIdleWaits">GPU 队列同步等待次数。</param>
public sealed record PreviewFrameCounters(ulong GpuBlits, ulong CpuPixelReadbacks, ulong QueueIdleWaits);

/// <summary>平台中立的已就绪预览帧描述。</summary>
/// <param name="TransportKind">传输模式。</param>
/// <param name="BackendApi">渲染后端 API。</param>
/// <param name="Token">精确帧与槽位租约标识。</param>
/// <param name="Width">像素宽度。</param>
/// <param name="Height">像素高度。</param>
/// <param name="PixelFormat">预乘像素格式。</param>
/// <param name="StrideBytes">行步长字节数。</param>
/// <param name="SlotOffsetBytes">在共享池内的起始字节偏移。</param>
/// <param name="SlotSizeBytes">本槽位有效字节容量。</param>
/// <param name="AllocationBytes">所属槽位或缓冲实际分配字节数。</param>
/// <param name="Device">渲染设备身份。</param>
/// <param name="Counters">拷贝与回读计数器。</param>
/// <param name="DmaBufModifier">可选的 DMA-BUF 格式修饰符。</param>
/// <param name="QueriedSubresourceBytes">可选的子资源布局查询字节数。</param>
public sealed record PreviewFrameDescriptor(
    PreviewTransportKind TransportKind,
    string BackendApi,
    PreviewFrameToken Token,
    int Width,
    int Height,
    PreviewPixelFormat PixelFormat,
    uint StrideBytes,
    ulong SlotOffsetBytes,
    ulong SlotSizeBytes,
    ulong AllocationBytes,
    PreviewDeviceIdentity Device,
    PreviewFrameCounters Counters,
    ulong? DmaBufModifier = null,
    ulong? QueriedSubresourceBytes = null);

/// <summary>向通道交付的一次预览帧载荷；SHM 模式下无附带系统句柄。</summary>
/// <param name="descriptor">帧与槽位元数据描述。</param>
/// <param name="auxiliaryHandle">可选的底层传输附带句柄（仅零拷贝模式使用，SHM 模式为空）。</param>
public sealed class PreviewFrameDelivery(PreviewFrameDescriptor descriptor, SafeHandle? auxiliaryHandle = null) : IDisposable
{
    /// <summary>本次精确槽位、像素格式、步长与计数器描述。</summary>
    public PreviewFrameDescriptor Descriptor { get; } = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
    /// <summary>可选的附带句柄；发送完成后由通道释放生产方本地副本。</summary>
    public SafeHandle? AuxiliaryHandle { get; } = auxiliaryHandle;
    /// <summary>仅释放生产方本地附带句柄，不代表消费者完成或归还槽位。</summary>
    public void Dispose() => AuxiliaryHandle?.Dispose();
}

/// <summary>预览帧 Ready、已导出与消费者精确完成之间的所有权边界。</summary>
public abstract class PreviewFrameLease : IDisposable
{
    /// <summary>建立精确槽位代际标识。</summary>
    protected PreviewFrameLease(PreviewFrameToken token) => Token = token ?? throw new ArgumentNullException(nameof(token));
    /// <summary>本租约的精确标识。</summary>
    public PreviewFrameToken Token { get; }
    /// <summary>原子进入待释放状态并返回交付描述；重复导出失败。</summary>
    public abstract PreviewFrameDelivery ExportForDelivery();
    /// <summary>仅在消费者确认上屏或丢弃完毕后归还槽位；标识必须完全匹配。</summary>
    public abstract void ReleaseAfterConsumerCompletion(PreviewFrameToken token);
    /// <summary>未知完成或断连时隔离所属池，禁止重写未知槽位。</summary>
    public abstract void Quarantine();
    /// <summary>未导出时取消并归还 Free 槽位；已导出时仅隔离。</summary>
    public abstract void Dispose();
}

/// <summary>双阶段预览端口产出的单帧结果，携带拆分耗时与真实运行时诊断。</summary>
public sealed class PreviewRenderFrame(
    RenderTarget target,
    long timeUs,
    PreviewFrameLease lease,
    PreviewFrameTimings timings) : IDisposable
{
    /// <summary>本次目标尺寸。</summary>
    public RenderTarget Target { get; } = target ?? throw new ArgumentNullException(nameof(target));
    /// <summary>本次采样微秒。</summary>
    public long TimeUs { get; } = timeUs;
    /// <summary>调用方负责交付、精确完成释放或隔离的 Ready 租约。</summary>
    public PreviewFrameLease Lease { get; } = lease ?? throw new ArgumentNullException(nameof(lease));
    /// <summary>绘制命令录制与端口捕获拆分耗时。</summary>
    public PreviewFrameTimings Timings { get; } = timings ?? throw new ArgumentNullException(nameof(timings));
    /// <summary>生产图绘制的真实提示。</summary>
    public ImmutableArray<RenderImageEffectDiagnostic> ImageEffectDiagnostics { get; init; } = [];
    /// <summary>材质绘制的真实提示。</summary>
    public ImmutableArray<RenderMaterialDiagnostic> MaterialDiagnostics { get; init; } = [];
    /// <summary>准备与绘制的稳定作者诊断。</summary>
    public RuntimeDiagnosticSnapshot DiagnosticSnapshot { get; init; } = RuntimeDiagnosticSnapshot.Empty;
    /// <summary>按租约状态取消未发布帧或隔离已导出帧。</summary>
    public void Dispose() => Lease.Dispose();
}

/// <summary>平台无关的连续预览帧会话（覆盖 GPU·DMA-BUF、GPU·SHM 与 CPU·SHM）。</summary>
public interface IPreviewRenderSession : IRenderFrameSession
{
    /// <summary>当前会话是否启用连续预览帧输出。</summary>
    bool SupportsPreviewFrames { get; }
    /// <summary>当前会话绑定的底层渲染后端标识（vulkan 或 cpu）。</summary>
    string BackendApi { get; }
    /// <summary>按指定传输与像素格式初始化三槽预览池。</summary>
    ValueTask<PreviewPoolInfo> InitializePreviewOutputAsync(
        RenderTarget target,
        ulong maximumAllocationBytes,
        PreviewOutputOptions? options = null,
        CancellationToken cancellationToken = default);
    /// <summary>按固定工程准备与双阶段端口绘制单帧，返回 Ready 租约与拆分耗时。</summary>
    ValueTask<PreviewRenderFrame> RenderPreviewAsync(RenderRequest request, CancellationToken cancellationToken = default);
    /// <summary>仅显式基准开关允许的合成图案绘制（支持双像素格式验证）。</summary>
    ValueTask<PreviewRenderFrame> RenderSyntheticPreviewAsync(
        RenderTarget target,
        long timeUs,
        ulong sequence,
        PreviewPixelFormat? pixelFormat = null,
        CancellationToken cancellationToken = default);
}
