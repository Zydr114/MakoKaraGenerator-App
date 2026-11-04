using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Rendering.Skia.Effects.Svg;
using MakoKaraGenerator.Scripting;
using SkiaSharp;

namespace MakoKaraGenerator.Rendering.Skia;

// Fixed package owner. Scene/frame leases outlive retirement; all device destruction uses its lock.
internal sealed class PackageSvgResources : IDisposable
{
    private readonly object _gate = new();
    private readonly object _nativeGate;
    private readonly ISkiaRenderDevice? _device;
    private readonly ImageSurfaceBudgetSession _surfaces;
    private readonly bool _ownsSurfaces;
    private readonly ImageSurfaceBudgetFrame _fixed;
    private readonly SvgNativeImageStore _images;
    private readonly Dictionary<string, SvgVectorProgram> _vectors = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Id, string Variant), SvgAtlasPage> _pages = [];
    private readonly List<SvgNativePictureCompiler> _compilers = [];
    private int _leases;
    private bool _retiring, _disposed;

    private PackageSvgResources(PackageSvgInspection inspection, ISkiaRenderDevice? device,
        ImageSurfaceBudgetSession surfaces, bool ownsSurfaces, PackageGraphImages? images, CancellationToken token)
    {
        Inspection = inspection; _device = device; _nativeGate = device?.SynchronizationRoot ?? new object();
        _surfaces = surfaces; _ownsSurfaces = ownsSurfaces; _fixed = surfaces.BeginFrame();
        _images = new(_fixed, surfaces, images, token);
    }
    internal PackageSvgInspection Inspection { get; }
    internal ISkiaRenderDevice? Device => _device;
    internal bool IsDisposed { get { lock (_gate) return _disposed; } }
    internal bool IsRetiring { get { lock (_gate) return _retiring; } }
    internal long RecordedOperations { get; private set; }
    internal long DecodedImageBytes => _images.DecodedImageBytes;
    internal long BorrowedImageBytes => _images.BorrowedImageBytes;
    internal ImageSurfaceUsage FixedUsage => _fixed.Usage;

    // CPU is a reference raster path. It never reports a mip request or GPU upload as completed.
    internal static PackageSvgResources LoadReference(EffectPackageContents contents, EffectPackageSvgResources resources,
        SvgResourceBudget? limits = null, GraphExecutionBudget? surfaceLimits = null, PackageGraphImages? images = null,
        CancellationToken token = default)
    {
        var surfaces = new ImageSurfaceBudgetSession(surfaceLimits);
        try { return LoadCore(contents, resources, limits ?? SvgResourceBudget.Default, null, surfaces, true, images, token); }
        catch { surfaces.Dispose(); throw; }
    }
    internal static PackageSvgResources LoadWithDevice(EffectPackageContents contents, EffectPackageSvgResources resources,
        SvgResourceBudget limits, ISkiaRenderDevice device, ImageSurfaceBudgetSession surfaces, PackageGraphImages? images, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(device); ArgumentNullException.ThrowIfNull(surfaces);
        return LoadCore(contents, resources, limits, device, surfaces, false, images, token);
    }
    internal static PackageSvgResources LoadReferenceWithSession(EffectPackageContents contents, EffectPackageSvgResources resources,
        SvgResourceBudget limits, ImageSurfaceBudgetSession surfaces, PackageGraphImages? images, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(surfaces);
        return LoadCore(contents, resources, limits, null, surfaces, false, images, token);
    }
    private static PackageSvgResources LoadCore(EffectPackageContents contents, EffectPackageSvgResources resources,
        SvgResourceBudget limits, ISkiaRenderDevice? device, ImageSurfaceBudgetSession surfaces, bool ownsSurfaces,
        PackageGraphImages? images, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(contents); ArgumentNullException.ThrowIfNull(resources);
        limits.Validate(); token.ThrowIfCancellationRequested();
        if (images is not null && (images.PackageId != contents.PackageId || images.Revision != contents.Revision))
            throw new EffectPackageException("PackageSvgResourceOwnershipMismatch", "manifest.json", "共享 role:image 集合不属于本固定包修订。");
        var compiled = SvgStaticCompiler.CompilePackage(contents, resources, limits, token);
        // Bound managed/native recording metadata before beginning any native picture allocation.
        long recordingReserve = checked((long)limits.MaximumPictureOperations * 256), atlasBuildPeak = 0;
        foreach (var resource in compiled.Inspection.Resources)
        {
            var definition = resources.ById[resource.Id];
            foreach (var variant in definition.Variants)
            {
                var plan = resource.AtlasPlans.Single(plan => plan.VariantId == variant.Id);
                long filterBytes = PhysicalFilterBytes(resource, definition, variant);
                long contentBytes = checked((long)variant.Width * variant.Height * 4);
                long additional = checked(contentBytes + plan.Levels[0].Bytes + Math.Max(0, filterBytes - resource.FilterSurfaceBytes));
                atlasBuildPeak = Math.Max(atlasBuildPeak, additional);
            }
        }
        long additionalPrepare = checked(recordingReserve + atlasBuildPeak);
        if (additionalPrepare > limits.MaximumPrepareBytes
            || compiled.Inspection.EstimatedPrepareBytes > limits.MaximumPrepareBytes - additionalPrepare)
            throw new EffectPackageException("PackageSvgBudgetExceeded", "manifest.json", "SVG 准备活动字节、native 录制及 atlas 构建峰值超过预算。");
        var result = new PackageSvgResources(compiled.Inspection, device, surfaces, ownsSurfaces, images, token);
        try
        {
            lock (result._nativeGate)
            {
                foreach (var document in compiled.Documents)
                {
                    token.ThrowIfCancellationRequested();
                    var definition = resources.ById[document.Inspection.Id];
                    var compiler = new SvgNativePictureCompiler(document,
                        limits with { MaximumPictureOperations = checked(limits.MaximumPictureOperations - (int)result.RecordedOperations) }, result._images, token);
                    result._compilers.Add(compiler);
                    SKPicture? picture = compiler.Compile();
                    result.RecordedOperations = checked(result.RecordedOperations + compiler.Operations);
                    try
                    {
                        if (definition.Mode == SvgResourceMode.Vector)
                        {
                            result._vectors.Add(definition.Id, new(picture, document.Inspection, compiler.Operations)); picture = null;
                        }
                        else foreach (var variant in definition.Variants)
                        {
                            token.ThrowIfCancellationRequested();
                            var plan = document.Inspection.AtlasPlans.Single(plan => plan.VariantId == variant.Id);
                            result._pages.Add((definition.Id, variant.Id), result.CreatePage(picture, definition, variant, plan, token));
                        }
                    }
                    finally { picture?.Dispose(); }
                }
            }
            token.ThrowIfCancellationRequested(); return result;
        }
        catch { result.Dispose(); throw; }
    }

    private SvgAtlasPage CreatePage(SKPicture picture, EffectSvgResourceDefinition definition, SvgAtlasVariant variant,
        SvgAtlasPlan plan, CancellationToken token)
    {
        var level = plan.Levels[0]; long baseBytes = level.Bytes;
        long contentBytes = checked((long)variant.Width * variant.Height * 4);
        using var contentReservation = _fixed.Reserve(1, contentBytes, "PrepareSvgAtlasContent");
        using var buildReservation = _fixed.Reserve(1, baseBytes, "PrepareSvgAtlasRaster");
        var inspection = Inspection.Resources.Single(resource => resource.Id == definition.Id);
        long filterBytes = PhysicalFilterBytes(inspection, definition, variant);
        using var filterReservation = filterBytes > 0
            ? _fixed.Reserve(Math.Max(1, inspection.FilterPasses + 2), filterBytes, "PrepareSvgAtlasFilterScratch") : null;
        using var srgb = SKColorSpace.CreateSrgb();
        using var contentSurface = SKSurface.Create(new SKImageInfo(variant.Width, variant.Height, SKColorType.Bgra8888, SKAlphaType.Premul, srgb))
            ?? throw new EffectPackageException("PackageSvgAtlasCreationFailed", definition.SourcePath, "atlas 原点内容表面创建失败。");
        var contentCanvas = contentSurface.Canvas; contentCanvas.Clear(SKColors.Transparent);
        contentCanvas.Scale(variant.Width, variant.Height); contentCanvas.DrawPicture(picture); contentCanvas.Flush();
        token.ThrowIfCancellationRequested();
        using var contentImage = contentSurface.Snapshot()
            ?? throw new EffectPackageException("PackageSvgAtlasCreationFailed", definition.SourcePath, "atlas 原点内容快照失败。");
        using var surface = SKSurface.Create(new SKImageInfo(level.Width, level.Height, SKColorType.Bgra8888, SKAlphaType.Premul, srgb))
            ?? throw new EffectPackageException("PackageSvgAtlasCreationFailed", definition.SourcePath, "atlas 参考表面创建失败。");
        var content = new SKRect(definition.PaddingPx, definition.PaddingPx, definition.PaddingPx + variant.Width, definition.PaddingPx + variant.Height);
        // Integer padding must not perturb geometry/clip AA through a different affine
        // rounding path. Rasterize at the same origin as a vector viewport, then copy
        // pixels 1:1 into the independently padded page before building GPU mips.
        var canvas = surface.Canvas; canvas.Clear(SKColors.Transparent);
        canvas.DrawImage(contentImage, new SKRect(0, 0, variant.Width, variant.Height), content, new SKSamplingOptions(SKFilterMode.Nearest));
        canvas.Flush();
        token.ThrowIfCancellationRequested();
        SKImage? raster = surface.Snapshot() ?? throw new EffectPackageException("PackageSvgAtlasCreationFailed", definition.SourcePath, "atlas 完整快照失败。");
        ImageSurfaceReservation? storage = null; SKImage? image = null;
        try
        {
            long bytes = _device is null ? baseBytes : plan.MipBytes;
            storage = _fixed.Reserve(1, bytes, _device is null ? "HoldSvgAtlasReference" : "UploadSvgAtlasMipChain");
            if (_device is null) { image = raster; raster = null; }
            else
            {
                image = _device.CreateTextureImage(raster, true)
                    ?? throw new EffectPackageException("PackageSvgAtlasMipUploadFailed", definition.SourcePath, "设备未创建完整 mip atlas。");
                if (!image.IsTextureBacked) throw new EffectPackageException("PackageSvgAtlasMipUploadFailed", definition.SourcePath, "设备返回的 atlas 不具备纹理存储。");
                if (ReferenceEquals(image, raster)) raster = null;
            }
            storage.BindImage(image.Handle); token.ThrowIfCancellationRequested();
            var page = new SvgAtlasPage(image, content, definition.PaddingPx, plan, _device, _surfaces, storage);
            image = null; storage = null; return page;
        }
        finally { image?.Dispose(); storage?.Dispose(); raster?.Dispose(); }
    }

    private static long PhysicalFilterBytes(SvgResourceInspection inspection, EffectSvgResourceDefinition definition, SvgAtlasVariant variant)
    {
        double density = Math.Max(1, Math.Max(variant.Width / definition.Viewport.Width, variant.Height / definition.Viewport.Height));
        double bytes = Math.Ceiling(inspection.FilterSurfaceBytes * density * density);
        if (!double.IsFinite(bytes) || bytes >= long.MaxValue)
            throw new EffectPackageException("PackageSvgBudgetExceeded", definition.SourcePath, "atlas filter 物理工作面溢出。");
        return (long)bytes;
    }

    internal IDisposable AcquireLease()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_retiring, this); _leases = checked(_leases + 1); return new Lease(this);
        }
    }
    internal SvgVectorProgram GetVector(ResourceIdentity identity)
    {
        lock (_gate)
        {
            Validate(identity);
            return _vectors.TryGetValue(identity.ExportId, out var program) ? program
                : throw new EffectPackageException("PackageSvgVectorMissing", identity.ExportId, "固定包没有此矢量资源。");
        }
    }
    internal SvgAtlasPage GetSprite(ResourceIdentity identity, string variantId)
    {
        lock (_gate)
        {
            Validate(identity);
            return _pages.TryGetValue((identity.ExportId, variantId), out var page) ? page
                : throw new EffectPackageException("PackageSvgAtlasMissing", identity.ExportId, "固定包没有此 atlas 档位。");
        }
    }
    internal IDisposable BorrowVectorInputs(ResourceIdentity identity, ImageSurfaceBudgetFrame frame)
    {
        lock (_gate)
        {
            _ = GetVector(identity);
            return _images.BorrowInputs(identity.ExportId, frame);
        }
    }
    private void Validate(ResourceIdentity identity)
    {
        ObjectDisposedException.ThrowIf(_disposed, this); ArgumentNullException.ThrowIfNull(identity);
        if (identity.PackageId != Inspection.PackageId || identity.Revision != Inspection.Revision)
            throw new EffectPackageException("PackageSvgResourceOwnershipMismatch", identity.ExportId, "SVG 句柄不属于本固定包修订。");
    }
    public void Dispose()
    {
        bool release; lock (_gate) { if (_retiring) return; _retiring = true; release = _leases == 0; }
        if (release) ReleaseNative();
    }
    private void ReleaseLease()
    {
        bool release; lock (_gate) { if (_leases <= 0) throw new InvalidOperationException("SVG lease 下溢。"); _leases--; release = _retiring && _leases == 0; }
        if (release) ReleaseNative();
    }
    private void ReleaseNative()
    {
        lock (_nativeGate)
        lock (_gate)
        {
            if (_disposed) return; _disposed = true;
            foreach (var page in _pages.Values) page.Dispose(); _pages.Clear();
            foreach (var vector in _vectors.Values) vector.Dispose(); _vectors.Clear();
            foreach (var compiler in _compilers.AsEnumerable().Reverse()) compiler.Dispose(); _compilers.Clear();
            _images.Dispose(); _fixed.Dispose(); if (_ownsSurfaces) _surfaces.Dispose();
        }
    }
    private sealed class Lease(PackageSvgResources owner) : IDisposable
    {
        private PackageSvgResources? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.ReleaseLease();
    }
}

internal sealed class SvgVectorProgram(SKPicture picture, SvgResourceInspection inspection, long recordedOperations) : IDisposable
{
    internal SKPicture Picture { get; } = picture;
    internal SKRect Bounds => Picture.CullRect;
    internal SvgResourceInspection Inspection { get; } = inspection;
    internal long RecordedOperations { get; } = recordedOperations;
    public void Dispose() => Picture.Dispose();
}

internal sealed class SvgAtlasPage(SKImage image, SKRect contentRect, int paddingPx, SvgAtlasPlan plan,
    ISkiaRenderDevice? device, ImageSurfaceBudgetSession surfaces, ImageSurfaceReservation storage) : IDisposable
{
    private bool _disposed;
    internal SKImage Image { get; } = image;
    internal SKRect ContentRect { get; } = contentRect;
    internal int PaddingPx { get; } = paddingPx;
    internal SvgAtlasPlan Plan { get; } = plan;
    internal ISkiaRenderDevice? Device { get; } = device;
    internal bool MipmapsRequested => Device is not null;
    internal bool IsTextureBacked => Image.IsTextureBacked;
    internal long StorageBytes => Device is null ? Plan.Levels[0].Bytes : Plan.MipBytes;
    // The binding exposes no actual mip-level query. Plan and successful upload are separate facts.
    internal ImageSurfaceReservation Borrow(ImageSurfaceBudgetFrame frame)
    { ObjectDisposedException.ThrowIf(_disposed, this); return surfaces.Borrow(frame, Image.Handle, StorageBytes); }
    public void Dispose() { if (_disposed) return; _disposed = true; Image.Dispose(); storage.Dispose(); }
}
