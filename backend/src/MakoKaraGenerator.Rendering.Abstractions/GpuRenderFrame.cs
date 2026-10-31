using System.Collections.Immutable;
using Microsoft.Win32.SafeHandles;

namespace MakoKaraGenerator.Rendering;

/// <summary>可选的本机 GPU 帧会话；不改变既有 CPU 像素与导出入口。</summary>
public interface IGpuRenderFrameSession : IRenderFrameSession
{
    /// <summary>设备是否显式启用本机 GPU 帧输出；不表示任意尺寸或池分配已成功。</summary>
    bool SupportsGpuFrames { get; }
    /// <summary>调用方先预留预算，再实际建立目标池；预算包含尚未排空的旧代际，上限 192 MiB。</summary>
    ValueTask<GpuFramePoolInfo> InitializeGpuOutputAsync(RenderTarget target, ulong maximumAllocationBytes, CancellationToken cancellationToken = default);
    /// <summary>按同一准备语义绘制，返回尚未向外部消费者导出的 Ready 租约。</summary>
    ValueTask<GpuRenderFrame> RenderGpuAsync(RenderRequest request, CancellationToken cancellationToken = default);
    /// <summary>仅显式基准启动配置允许的 GPU 合成图案；sequence 是调用尝试标记，图案身份以返回租约 FrameSequence 为准。</summary>
    ValueTask<GpuRenderFrame> RenderSyntheticGpuAsync(RenderTarget target, long timeUs, ulong sequence, CancellationToken cancellationToken = default);
}

/// <summary>生产者、池与槽位代际共同限定的精确释放标识。</summary>
public sealed record GpuFrameToken(ulong ProducerEpoch, ulong PoolGeneration, ulong FrameSequence, ulong SlotEpoch, uint SlotId);

/// <summary>同一 Vulkan physical device 的公开身份，不包含设备句柄。</summary>
public sealed record GpuDeviceIdentity(string Name, uint VendorId, uint DeviceId, uint DrmRenderMajor, uint DrmRenderMinor, string DeviceUuid);

/// <summary>实际分配后的 GPU 池能力；公开流之前必须取得此结果。</summary>
public sealed record GpuFramePoolInfo(ulong ProducerEpoch, ulong PoolGeneration, int Width, int Height,
    uint SlotCount, ulong AllocationBytes, GpuDeviceIdentity Device)
{
    /// <summary>本会话所有尚存旧／新池的真实 allocation 总量。</summary>
    public ulong RetainedAllocationBytes { get; init; }
}

/// <summary>单平面、预乘 BGRA8888、sRGB、TopLeft 的本机 DMA-BUF 描述；不含 FD 数值。</summary>
public sealed record GpuFrameDescriptor(GpuFrameToken Token, int Width, int Height, uint PlaneStride,
    ulong PlaneOffset, ulong AllocationSize, ulong QueriedSubresourceSize, ulong Modifier,
    GpuDeviceIdentity Device, ulong GpuBlits, ulong CpuPixelReadbacks, ulong QueueIdleWaits);

/// <summary>向外部交付的一次 FD 所有权；释放 FD 不表示消费者 GPU 已完成。</summary>
public sealed class GpuFrameExport(GpuFrameDescriptor descriptor, SafeFileHandle fileDescriptor) : IDisposable
{
    /// <summary>本次精确槽位、尺寸、设备和内存布局。</summary>
    public GpuFrameDescriptor Descriptor { get; } = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
    /// <summary>由交付方在 sendmsg 完成后关闭的原生 FD；禁止经 JSON 传递此整数。</summary>
    public SafeFileHandle FileDescriptor { get; } = fileDescriptor ?? throw new ArgumentNullException(nameof(fileDescriptor));
    /// <summary>仅关闭本次生产方 FD；不释放槽位或推定消费者排空。</summary>
    public void Dispose() => FileDescriptor.Dispose();
}

/// <summary>Ready、已导出与精确消费者完成之间的显式所有权边界。</summary>
public abstract class GpuFrameLease : IDisposable
{
    /// <summary>建立精确槽位代际标识。</summary>
    protected GpuFrameLease(GpuFrameToken token) => Token = token ?? throw new ArgumentNullException(nameof(token));
    /// <summary>本租约的精确标识。</summary>
    public GpuFrameToken Token { get; }
    /// <summary>原子进入 AwaitRelease 后返回一次 owned FD；重复导出失败。</summary>
    public abstract GpuFrameExport ExportForDelivery();
    /// <summary>仅在验证真实原生消费者 GPU 最终引用释放后调用；标识必须完全匹配。</summary>
    public abstract void ReleaseAfterConsumerCompletion(GpuFrameToken token);
    /// <summary>停止该池复用；未知交付、断连和崩溃不构成可重用证明。</summary>
    public abstract void Quarantine();
    /// <summary>未导出时取消并回收；已导出时仅隔离，不授权重写。</summary>
    public abstract void Dispose();
}

/// <summary>没有最终 CPU 像素的 GPU 输出，保留同一准备与真实绘制诊断。</summary>
public sealed class GpuRenderFrame(RenderTarget target, long timeUs, GpuFrameLease lease) : IDisposable
{
    /// <summary>本次目标尺寸。</summary>
    public RenderTarget Target { get; } = target ?? throw new ArgumentNullException(nameof(target));
    /// <summary>本次采样微秒。</summary>
    public long TimeUs { get; } = timeUs;
    /// <summary>调用方负责交付、精确完成释放或隔离的 Ready 租约。</summary>
    public GpuFrameLease Lease { get; } = lease ?? throw new ArgumentNullException(nameof(lease));
    /// <summary>生产图绘制的真实提示。</summary>
    public ImmutableArray<RenderImageEffectDiagnostic> ImageEffectDiagnostics { get; init; } = [];
    /// <summary>材质绘制的真实提示。</summary>
    public ImmutableArray<RenderMaterialDiagnostic> MaterialDiagnostics { get; init; } = [];
    /// <summary>准备与绘制的稳定作者诊断；不以 GPU 成功代替错误。</summary>
    public RuntimeDiagnosticSnapshot DiagnosticSnapshot { get; init; } = RuntimeDiagnosticSnapshot.Empty;
    /// <summary>按租约状态取消未发布帧或隔离已导出帧。</summary>
    public void Dispose() => Lease.Dispose();
}

/// <summary>GPU 路径的明确失败；调用方不得静默回落 CPU 后宣称 GPU 成功。</summary>
public sealed class GpuPreviewException(string code, string message) : InvalidOperationException(message)
{
    /// <summary>可供有限能力／错误响应使用的稳定失败代码。</summary>
    public string Code { get; } = code;
}
