using System.Collections.Immutable;
using MakoKaraGenerator.Core.Effects;
using SkiaSharp;

namespace MakoKaraGenerator.Rendering.Skia;

/// <summary>渲染后端图像表面；外部借用图像和本图拥有的图像明确分开。</summary>
public sealed class SkiaGraphSurface : IDisposable
{
    private readonly SKImage _image;
    private readonly bool _ownsImage;
    private readonly ImageSurfaceReservation? _reservation;
    private readonly ISkiaRenderDevice? _device;
    private readonly Action? _releaseDeviceLease;
    private readonly ColorPixelValidation? _colorPixelValidation;
    private bool _disposed;
    private SkiaGraphSurface(SKImage image, GraphSurfaceDescriptor descriptor, ShaderSurfaceKind kind, bool ownsImage,
        ISkiaRenderDevice? device, ImageSurfaceReservation? reservation = null, Action? releaseDeviceLease = null,
        ColorPixelValidation? colorPixelValidation = null)
    { _image = image; Descriptor = descriptor; Kind = kind; _ownsImage = ownsImage; _device = device; _reservation = reservation; _releaseDeviceLease = releaseDeviceLease; _colorPixelValidation = colorPixelValidation; }
    /// <summary>借用固定输入图像；释放包装不释放调用者图像。</summary>
    public static SkiaGraphSurface Borrow(SKImage image, GraphSurfaceDescriptor descriptor, ShaderSurfaceKind kind)
    { ArgumentNullException.ThrowIfNull(image); ArgumentNullException.ThrowIfNull(descriptor); return new(image, descriptor, kind, false, null); }
    internal static SkiaGraphSurface Borrow(SKImage image, GraphSurfaceDescriptor descriptor, ShaderSurfaceKind kind, ISkiaRenderDevice device)
    { ArgumentNullException.ThrowIfNull(device); ArgumentNullException.ThrowIfNull(image); ArgumentNullException.ThrowIfNull(descriptor); return new(image, descriptor, kind, false, device); }
    internal static SkiaGraphSurface Own(SKImage image, GraphSurfaceDescriptor descriptor, ShaderSurfaceKind kind,
        ISkiaRenderDevice device, ImageSurfaceReservation? reservation = null, Action? releaseDeviceLease = null)
    {
        ArgumentNullException.ThrowIfNull(device);
        reservation?.BindImage(image.Handle);
        // 只有内部快照由本包装拥有；外部 Borrow 可能引用仍会改变的像素内存。
        return new(image, descriptor, kind, true, device, reservation, releaseDeviceLease, new());
    }
    /// <summary>真实图像；包装已释放时报错。</summary>
    public SKImage Image { get { ObjectDisposedException.ThrowIf(_disposed, this); return _image; } }
    /// <summary>不可变逻辑几何。</summary>
    public GraphSurfaceDescriptor Descriptor { get; }
    /// <summary>颜色或 raw alpha 覆盖。</summary>
    public ShaderSurfaceKind Kind { get; }
    /// <summary>创建本图像的设备；外部借用图像没有可验证的设备身份。</summary>
    internal ISkiaRenderDevice? Device => _device;
    internal SkiaGraphSurface BorrowAs(ShaderSurfaceKind kind)
        => new(Image, Descriptor, kind, false, _device, colorPixelValidation: _colorPixelValidation);
    internal bool HasValidatedColorPixels(ISkiaRenderDevice device)
        => ReferenceEquals(_device, device) && _colorPixelValidation?.IsValid == true;
    internal bool HasOwnedImageOn(ISkiaRenderDevice device)
        => !_disposed && ReferenceEquals(_device, device) && _colorPixelValidation?.IsLive == true;
    internal void MarkColorPixelsValidated(ISkiaRenderDevice device)
    {
        if (ReferenceEquals(_device, device)) _colorPixelValidation?.MarkValidated();
    }
    internal SKImage ImageForDevice(ISkiaRenderDevice target, out SKImage? raster)
    {
        var image = Image;
        var converted = ReferenceEquals(_device, target) ? image : _device?.RasterizeImage(image) ?? image.ToRasterImage(true);
        if (converted is null)
            throw new InvalidOperationException("跨 Skia device 图像无法转换为可传递像素。");
        raster = ReferenceEquals(converted, image) ? null : converted;
        return converted;
    }
    /// <summary>只释放本包装拥有的图像；幂等。</summary>
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        try
        {
            if (_ownsImage)
            {
                _colorPixelValidation?.Invalidate();
                _image.Dispose();
            }
        }
        finally { try { _reservation?.Dispose(); } finally { _releaseDeviceLease?.Invoke(); } }
    }

    // 凭据只保存成功事实，不拥有图像或设备；非拥有别名不延长原生命周期和预算。
    private sealed class ColorPixelValidation
    {
        private int _state;
        internal bool IsValid => Volatile.Read(ref _state) == 1;
        internal bool IsLive => Volatile.Read(ref _state) != 2;
        internal void MarkValidated() => Interlocked.CompareExchange(ref _state, 1, 0);
        internal void Invalidate() => Interlocked.Exchange(ref _state, 2);
    }
}
/// <summary>帧输入来源和受管资源身份必须与声明匹配。</summary>
/// <param name="Surface">借用表面。</param><param name="Source">真实来源。</param><param name="ResourceId">ResourceImage 身份，不是路径。</param>
public sealed record SkiaGraphInput(SkiaGraphSurface Surface, GraphInputSource Source, string? ResourceId = null);
/// <summary>图帧产物和资源预估；旁路原图仍由调用者拥有。</summary>
public sealed class ImageGraphResult : IDisposable
{
    private readonly bool _ownsOutput, _ownsCoverage;
    private readonly ImageSurfaceReservations? _inputReservations;
    private readonly Action? _releaseDeviceLease;
    private bool _disposed;
    internal ImageGraphResult(CompiledImageGraphDefinition plan, SkiaGraphSurface output, SkiaGraphSurface? coverage, bool ownsOutput, bool ownsCoverage,
        ImageGraphFrameContext context, ImmutableArray<ImageGraphBypassDiagnostic> diagnostics, int peakSurfaces, long peakBytes,
        ImageSurfaceReservations? inputReservations = null, Action? releaseDeviceLease = null)
    { Plan = plan; Output = output; Coverage = coverage; _ownsOutput = ownsOutput; _ownsCoverage = ownsCoverage; Context = context; Diagnostics = diagnostics; PeakSurfaces = peakSurfaces; PeakBytes = peakBytes; _inputReservations = inputReservations; _releaseDeviceLease = releaseDeviceLease; }
    internal CompiledImageGraphDefinition Plan { get; }
    internal ImageGraphFrameContext Context { get; }
    /// <summary>最终颜色图。</summary>
    public SkiaGraphSurface Output { get; }
    /// <summary>替换合成的独立 raw 覆盖；普通合成或旁路为空。</summary>
    public SkiaGraphSurface? Coverage { get; }
    /// <summary>明确背景旁路清单。</summary>
    public ImmutableArray<ImageGraphBypassDiagnostic> Diagnostics { get; }
    /// <summary>执行计划的最大活动表面数，包含输入和产物。</summary>
    public int PeakSurfaces { get; }
    /// <summary>最大活动 BGRA 预估字节，不是完整 native filter scratch 测量。</summary>
    public long PeakBytes { get; }
    /// <summary>释放本图产物；不夺取借用输入所有权。</summary>
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        try
        {
            try { if (_ownsOutput) Output.Dispose(); }
            finally { if (_ownsCoverage && !ReferenceEquals(Coverage, Output)) Coverage?.Dispose(); }
        }
        finally
        {
            try { _inputReservations?.Dispose(); }
            finally { _releaseDeviceLease?.Invoke(); }
        }
    }
}
