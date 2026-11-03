using System.Runtime.InteropServices;
using MakoKaraGenerator.Rendering.Skia;
using SkiaSharp;

namespace MakoKaraGenerator.Runtime.Hosting;

/// <summary>实际创建、绘制及读回后取得的设备摘要；仅描述本次检查。</summary>
public sealed record RuntimeRenderBackendProbeInfo(string Backend, string? Vendor, string? Device, string? Driver, string? ApiVersion);

/// <summary>用于受管理 Worker 的独立设备检查，不读取工程或运行作者脚本。</summary>
public static class RuntimeRenderBackendProbe
{
    /// <summary>Worker 入口调用一次并固定结果，后续会话不得再次读取环境。</summary>
    public static string ResolveEnvironment() => SkiaRenderBackendOptions.FromEnvironment().Backend == SkiaRenderBackend.Cpu ? "cpu" : "vulkan";

    /// <summary>显式检查固定设备；Vulkan 必须实际为 GPU 表面且完整读回正确。</summary>
    public static RuntimeRenderBackendProbeInfo Probe(string backend, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var device = SkiaRenderDeviceFactory.Create(SkiaRenderBackendOptions.Parse(backend));
        return ProbeRetainedDevice(device, backend, cancellationToken);
    }

    // The caller owns the actual prepared device. This check must not replace or dispose it.
    internal static RuntimeRenderBackendProbeInfo ProbeRetainedDevice(ISkiaRenderDevice device, string backend,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        bool gpu = backend == "vulkan";
        if (device.Info.Backend != backend) throw new InvalidOperationException("实际渲染设备与固定选择不同。");
        SKImageInfo info = new(4, 2, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = device.CreateSurface(info);
        if (gpu ? surface.Context?.Backend != GRBackend.Vulkan : surface.Context is not null)
            throw new InvalidOperationException("实际渲染表面类型不符合固定选择。");
        surface.Canvas.Clear(SKColors.Transparent);
        using var paint = new SKPaint { Color = new SKColor(255, 0, 0, 128), IsAntialias = false, BlendMode = SKBlendMode.Src };
        surface.Canvas.DrawRect(0, 0, 1, 1, paint);
        device.FlushAndSubmit(surface);
        using var snapshot = surface.Snapshot();
        if (snapshot.IsTextureBacked != gpu) throw new InvalidOperationException("实际图像存储类型不符合固定选择。");
        byte[] pixels = new byte[info.BytesSize];
        GCHandle pinned = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            if (!device.ReadPixels(surface, info, pinned.AddrOfPinnedObject(), info.RowBytes))
                throw new InvalidOperationException("渲染设备读回失败。");
        }
        finally { pinned.Free(); }
        cancellationToken.ThrowIfCancellationRequested();
        if (pixels[0] != 0 || pixels[1] != 0 || pixels[2] != 128 || pixels[3] != 128
            || pixels.AsSpan(4).ContainsAnyExcept((byte)0)) throw new InvalidOperationException("渲染设备像素读回不符合预乘合同。");
        return new(backend, Summary(device.Info.Vendor), Summary(device.Info.Device), Summary(device.Info.Driver), Summary(device.Info.ApiVersion));
    }

    private static string? Summary(string? value) => value is null ? null : new(value.Where(character => !char.IsControl(character)).Take(256).ToArray());
}
