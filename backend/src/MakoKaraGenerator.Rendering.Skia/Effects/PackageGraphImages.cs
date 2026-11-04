using MakoKaraGenerator.Core.Effects;
using SkiaSharp;

namespace MakoKaraGenerator.Rendering.Skia;

/// <summary>完整解码的包图片集合；输入借用至集合释放，位置／密度由宿主显式指定，不拉伸到目标。</summary>
public sealed class PackageGraphImages : IDisposable
{
    private readonly Dictionary<string, SKImage> _images = new(StringComparer.Ordinal);
    private readonly ISkiaRenderDevice? _device;
    private bool _disposed;
    private PackageGraphImages(EffectPackageContents contents, ISkiaRenderDevice? device)
    { _device = device; PackageId = contents.PackageId; Revision = contents.Revision; }
    internal string PackageId { get; }
    internal string Revision { get; }
    // Borrowers must retain this owner until their recorded pictures have retired.
    internal SKImage BorrowImage(string path) => Get(path);
    /// <summary>完整解码所有图片，包括未使用项；逐项及总表面／字节预算在像素分配前检查。</summary>
    public static PackageGraphImages Load(EffectPackageContents contents, GraphExecutionBudget? budget = null, CancellationToken cancellationToken = default)
        => LoadCore(contents, budget, null, cancellationToken);

    internal static PackageGraphImages LoadWithDevice(EffectPackageContents contents, GraphExecutionBudget? budget,
        ISkiaRenderDevice device, CancellationToken cancellationToken = default)
        => LoadCore(contents, budget, device, cancellationToken);

    private static PackageGraphImages LoadCore(EffectPackageContents contents, GraphExecutionBudget? budget,
        ISkiaRenderDevice? device, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(contents); cancellationToken.ThrowIfCancellationRequested();
        var limits = budget ?? GraphExecutionBudget.Default;
        if (limits.MaximumBytes <= 0 || limits.MaximumSurfaces <= 0) throw new ArgumentException("图片集合预算必须为正。", nameof(budget));
        var result = new PackageGraphImages(contents, device); long bytes = 0;
        try
        {
            foreach (var file in contents.Files.Where(file => file.Role == "image"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (result._images.Count >= limits.MaximumSurfaces || bytes >= limits.MaximumBytes)
                    throw new EffectPackageException("PackageImageBudgetExceeded", file.Path, "完整包图片超过总表面／字节预算。");
                var image = new PackageImageValidator(limits with { MaximumBytes = limits.MaximumBytes - bytes }).Decode(file, cancellationToken);
                result._images.Add(file.Path, image); bytes = checked(bytes + (long)image.Width * image.Height * 4);
            }
            cancellationToken.ThrowIfCancellationRequested(); return result;
        }
        catch { result.Dispose(); throw; }
    }
    /// <summary>读取原始像素尺寸；宿主用它显式建立图像几何，不默认使用画布大小。</summary>
    public (int Width, int Height) GetPixelSize(string resourcePath)
    { var image = Get(resourcePath); return (image.Width, image.Height); }
    /// <summary>建立指定来源的借用输入；明确的密度／框必须对应原始像素尺寸，变换由图节点完成。</summary>
    public SkiaGraphInput GetInput(GraphInputDefinition input, GraphSurfaceDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(input); ArgumentNullException.ThrowIfNull(descriptor);
        if (input.Source != GraphInputSource.ResourceImage && !input.Choices.ContainsValue(GraphInputSource.ResourceImage)
            || input.ResourceId is null || input.Kind != ShaderSurfaceKind.Color)
            throw new EffectPackageException("PackageGraphImageSourceMismatch", "inputs." + input.Id, "输入没有声明固定包图片来源。");
        var image = Get(input.ResourceId);
        if (!descriptor.Bounds.IsValid || descriptor.Space != input.Space || descriptor.LocalToCanvas != Core.Projects.SceneMatrix.Identity
            || !double.IsFinite(descriptor.DensityX) || !double.IsFinite(descriptor.DensityY) || descriptor.DensityX <= 0 || descriptor.DensityY <= 0
            || descriptor.Bounds.Width * descriptor.DensityX != image.Width || descriptor.Bounds.Height * descriptor.DensityY != image.Height)
            throw new EffectPackageException("PackageGraphImageGeometryInvalid", "inputs." + input.Id, "须明确原始图片的框、空间及像素密度；使用 Transform 做位置／空间变换。");
        var surface = _device is null
            ? SkiaGraphSurface.Borrow(image, descriptor, ShaderSurfaceKind.Color)
            : SkiaGraphSurface.Borrow(image, descriptor, ShaderSurfaceKind.Color, _device);
        return new(surface, GraphInputSource.ResourceImage, input.ResourceId);
    }
    private SKImage Get(string path)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _images.TryGetValue(path, out var image) ? image : throw new EffectPackageException("PackageGraphImageMissing", path, "固定包没有此图片。");
    }
    /// <summary>幂等释放全部完整图像；调用者须先释放借用输入及图结果。</summary>
    public void Dispose()
    { if (_disposed) return; _disposed = true; foreach (var image in _images.Values) image.Dispose(); _images.Clear(); }
}
