using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering.Skia;

/// <summary>帧外固定的真实包图集合；导出句柄关联到实际计划，调用者拥有全部图／图片生命周期。</summary>
public sealed partial class CompiledPackageGraphExports : IDisposable
{
    private readonly EffectPackageDefinition _definition;
    private readonly Dictionary<string, CompiledImageGraph> _graphs = new(StringComparer.Ordinal);
    private readonly PackageGraphImages _images;
    internal PackageGraphImages Images => _images;
    private bool _disposed;
    private CompiledPackageGraphExports(EffectPackageDefinition definition, PackageGraphImages images)
    {
        _definition = definition; _images = images;
        Resources = EffectPackageResourceCatalog.Create(definition);
    }
    /// <summary>已经过本 native 集合完整资源门禁的固定目录；不代表作者源码已经编译或工程已启用。</summary>
    public ResourceCatalog Resources { get; }
    /// <summary>检查全部程序、图片、图和回退；失败／取消释放已建立对象，不在帧内编译源码。</summary>
    public static CompiledPackageGraphExports Compile(EffectPackageDefinition definition, GraphExecutionBudget? imageBudget = null,
        CancellationToken cancellationToken = default)
        => CompileCore(definition, imageBudget, null, cancellationToken);

    internal static CompiledPackageGraphExports CompileWithDevice(EffectPackageDefinition definition, GraphExecutionBudget? imageBudget,
        ISkiaRenderDevice device, bool pixelChecks = true, CancellationToken cancellationToken = default)
        => CompileCore(definition, imageBudget, device, cancellationToken, pixelChecks);

    private static CompiledPackageGraphExports CompileCore(EffectPackageDefinition definition, GraphExecutionBudget? imageBudget,
        ISkiaRenderDevice? device, CancellationToken cancellationToken, bool pixelChecks = true)
    {
        ArgumentNullException.ThrowIfNull(definition); cancellationToken.ThrowIfCancellationRequested();
        if (!definition.SvgResources.ById.IsEmpty)
            PackageSvgValidation.Validate(definition.Contents, definition.SvgResources, new PackageSvgValidator(), cancellationToken);
        new PackageShaderValidator().Validate(definition.ShaderPrograms, cancellationToken);
        new PackageMaterialValidator().Validate(definition.Materials, cancellationToken);
        var images = device is null
            ? PackageGraphImages.Load(definition.Contents, imageBudget, cancellationToken)
            : PackageGraphImages.LoadWithDevice(definition.Contents, imageBudget, device, cancellationToken);
        CompiledPackageGraphExports? result = null;
        try
        {
            result = new(definition, images);
            foreach (var pair in definition.ImageGraphs.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try { result._graphs.Add(pair.Key, device is null
                    ? CompiledImageGraph.Compile(pair.Value.Plan, cancellationToken)
                    : CompiledImageGraph.Compile(pair.Value.Plan, device, pixelChecks, cancellationToken)); }
                catch (ShaderProgramException error) { throw new EffectPackageException(error.Code, pair.Key + "." + error.Field, error.Message, error); }
                catch (ImageGraphException error) { throw new EffectPackageException(error.Code, pair.Key + "." + error.Field, error.Message, error); }
            }
            cancellationToken.ThrowIfCancellationRequested(); return result;
        }
        catch { if (result is null) images.Dispose(); else result.Dispose(); throw; }
    }
    /// <summary>把真实图导出转换为生产捕获程序；框须由当前目标明确提供，返回程序借用本集合图。</summary>
    public SceneImageGraphProgram CreateProgram(GraphHandle handle, GraphRect bounds)
    {
        ObjectDisposedException.ThrowIf(_disposed, this); ArgumentNullException.ThrowIfNull(handle);
        if (!Resources.Contains(handle)) throw new EffectPackageException("InvalidGraphResource", "exports", "图句柄不属于本固定导出修订。");
        var export = _definition.GetGraph(handle.Identity.ExportId);
        return SceneImageGraphProgram.FromPackage(handle, _graphs[export.GraphPath], _definition.ImageGraphs[export.GraphPath], bounds);
    }
    /// <summary>借用真实材质图；绑定区域和透明回退由后续材质执行适配负责，不伪造 GraphHandle。</summary>
    public CompiledImageGraph GetMaterialGraph(MaterialHandle handle)
    {
        ObjectDisposedException.ThrowIf(_disposed, this); ArgumentNullException.ThrowIfNull(handle);
        if (!Resources.Contains(handle)) throw new EffectPackageException("InvalidMaterialResource", "exports", "材质句柄不属于本固定导出修订。");
        return _graphs[_definition.GetMaterial(handle.Identity.ExportId).GraphPath];
    }
    /// <summary>读取真实包图片，尺寸／空间不默认适配目标；输入借用图片并应在集合前释放。</summary>
    public SkiaGraphInput GetImageInput(GraphInputDefinition input, GraphSurfaceDescriptor descriptor)
    { ObjectDisposedException.ThrowIf(_disposed, this); return _images.GetInput(input, descriptor); }
    /// <summary>读取完整解码的实际包图片尺寸，宿主据此显式设置输入几何。</summary>
    public (int Width, int Height) GetImagePixelSize(string resourcePath)
    { ObjectDisposedException.ThrowIf(_disposed, this); return _images.GetPixelSize(resourcePath); }
    /// <summary>幂等释放固定图和图片；不得在仍使用借用程序或图片时提前释放。</summary>
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        foreach (var graph in _graphs.Values) graph.Dispose(); _graphs.Clear(); _images.Dispose();
    }
}
