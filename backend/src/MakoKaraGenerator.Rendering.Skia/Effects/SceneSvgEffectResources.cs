using System.Collections.Immutable;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering.Skia;

/// <summary>场景借用的固定 SVG 矢量及 atlas；持包租约，不拥有包的 native 存储。</summary>
/// <remarks>调用方在管线及帧消费者释放后关闭场景，再释放固定包与设备。</remarks>
public sealed class SceneSvgEffectResources : IDisposable
{
    private readonly Dictionary<VectorHandle, PackageSvgResources> _vectors = [];
    private readonly Dictionary<SpriteHandle, PackageSvgResources> _sprites = [];
    private readonly ISkiaRenderDevice _device;
    private List<IDisposable>? _leases = [];

    internal SceneSvgEffectResources(IEnumerable<PackageSvgResources> packages, ISkiaRenderDevice device,
        long maximumFrameOperations = 200_000, long maximumFrameFilterBytes = 128L * 1024 * 1024)
    {
        ArgumentNullException.ThrowIfNull(packages); ArgumentNullException.ThrowIfNull(device);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumFrameOperations);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumFrameFilterBytes);
        _device = device; MaximumFrameOperations = maximumFrameOperations; MaximumFrameFilterBytes = maximumFrameFilterBytes;
        var inspections = ImmutableArray.CreateBuilder<PackageSvgInspection>();
        try
        {
            foreach (var package in packages)
            {
                ArgumentNullException.ThrowIfNull(package); ValidateDevice(package, device);
                _leases.Add(package.AcquireLease());
                var inspection = package.Inspection; inspections.Add(inspection);
                ResourceCatalog catalog = new(inspection.PackageId, inspection.Revision, [], [], [],
                    [.. inspection.Resources.Where(resource => resource.Mode == SvgResourceMode.Vector).Select(resource => resource.Id)],
                    inspection.Resources.Where(resource => resource.Mode == SvgResourceMode.Atlas).ToImmutableDictionary(
                        resource => resource.Id, resource => resource.AtlasPlans.Select(plan => plan.VariantId).ToImmutableArray(), StringComparer.Ordinal));
                foreach (var resource in inspection.Resources)
                {
                    if (resource.Mode == SvgResourceMode.Vector) _vectors.Add(catalog.GetVector(resource.Id), package);
                    else foreach (var plan in resource.AtlasPlans) _sprites.Add(catalog.GetSprite(resource.Id, plan.VariantId), package);
                }
            }
            Inspections = inspections.ToImmutable();
        }
        catch { try { Dispose(); } catch { /* 保留原始目录／租约失败。 */ } throw; }
    }

    internal ImmutableArray<PackageSvgInspection> Inspections { get; }
    internal long MaximumFrameOperations { get; }
    internal long MaximumFrameFilterBytes { get; }

    internal SvgVectorProgram GetVector(VectorHandle handle, ISkiaRenderDevice device)
    {
        Validate(device); ArgumentNullException.ThrowIfNull(handle);
        if (!_vectors.TryGetValue(handle, out var package))
            throw new PreparedFrameException("SvgVectorResourceUnavailable", "没有此完整包修订的固定 SVG 矢量资源。");
        ValidateDevice(package, device); return package.GetVector(handle.Identity);
    }
    internal SvgAtlasPage GetSprite(SpriteHandle handle, ISkiaRenderDevice device)
    {
        Validate(device); ArgumentNullException.ThrowIfNull(handle);
        if (!_sprites.TryGetValue(handle, out var package))
            throw new PreparedFrameException("SvgAtlasResourceUnavailable", "没有此完整包修订及档位的固定 SVG atlas。");
        ValidateDevice(package, device); return package.GetSprite(handle.Identity, handle.VariantId);
    }
    internal IDisposable BorrowVectorInputs(VectorHandle handle, ImageSurfaceBudgetFrame frame, ISkiaRenderDevice device)
    {
        _ = GetVector(handle, device);
        return _vectors[handle].BorrowVectorInputs(handle.Identity, frame);
    }
    private void Validate(ISkiaRenderDevice device)
    {
        ObjectDisposedException.ThrowIf(_leases is null, this);
        if (!ReferenceEquals(_device, device))
            throw new PreparedFrameException("SvgDeviceMismatch", "SVG 场景只允许在创建它的同一设备代次绘制。");
    }
    private static void ValidateDevice(PackageSvgResources package, ISkiaRenderDevice device)
    {
        if (!ReferenceEquals(package.Device, device) && !(package.Device is null && device is CpuSkiaRenderDevice))
            throw new PreparedFrameException("SvgDeviceMismatch", "固定 SVG 资源不属于当前设备；CPU 参考图片不能冒充 GPU mip。");
    }
    /// <summary>关闭查找入口并归还全部场景租约；不主动释放包资源。</summary>
    public void Dispose()
    {
        var leases = Interlocked.Exchange(ref _leases, null);
        if (leases is null) return;
        List<Exception> failures = [];
        foreach (var lease in leases.AsEnumerable().Reverse())
            try { lease.Dispose(); } catch (Exception error) { failures.Add(error); }
        if (failures.Count > 0) throw new AggregateException("SVG 场景租约释放失败。", failures);
    }
}
