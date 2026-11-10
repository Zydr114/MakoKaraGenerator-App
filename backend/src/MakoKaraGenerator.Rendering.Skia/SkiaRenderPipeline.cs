using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Media;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Text;
using SkiaSharp;

namespace MakoKaraGenerator.Rendering.Skia;

/// <summary>
/// Skia 渲染后端：把项目快照与时刻画成一帧像素。
/// </summary>
/// <remarks>
/// <para>
/// 实现 docs/development.md §9.4 的两阶段策略：<b>整行文本只整形一次并确定元素边界，
/// 再在稳定布局上做逐字裁切</b>。因此高亮推进时整行不会重排、不会跳动，
/// 也不需要在每个元素上重复排版。
/// </para>
/// <para>
/// 绘制坐标始终是<b>画布坐标</b>：目标尺寸与画布尺寸的比值被施加在画布变换上。
/// 于是"整画布导出"与"缩小预览"走的是同一段几何代码，草稿预览只是同一张画面的缩略图
/// （docs/development.md §10.3）。
/// </para>
/// <para>
/// 样式全部来自<b>快照</b>（基线 → 行覆盖 → 元素覆盖），请求里不再单独携带样式：
/// 样式现在属于工程数据，"用哪一份样式"因此只有唯一来源（ADR-0024）。
/// </para>
/// <para>
/// 本类型不保存任何与帧有关的可变状态，因此可以从界面线程（预览）与后台线程（导出）
/// 各持一个实例安全调用。布局引擎本身不跨线程共享（native 字体对象，
/// docs/decisions.md ADR-0007 的教训）。
/// </para>
/// </remarks>
public sealed partial class SkiaRenderPipeline : IRenderPipeline, ILineGeometryProvider, IDisposable
{
    private readonly SkiaTextLayoutEngine _layoutEngine;
    private readonly bool _ownsLayoutEngine;
    private readonly IBackgroundFrameSourceResolver _backgroundResolver;
    private readonly ImageSurfaceBudgetSession _surfaceBudget;
    private readonly bool _ownsSurfaceBudget;
    private readonly SceneImageEffectResources? _imageEffects;
    private readonly SceneSvgEffectResources? _svgEffects;
    private readonly ISkiaRenderDevice _device;
    private readonly SkiaRenderDeviceLifetime? _deviceLifetime;
    private readonly SceneSpriteProgram? _spriteProgram;
    private readonly Lazy<SceneOpacityProgram> _isolationResources = new(static () => new());
    private readonly SceneCompositionProgram _compositionProgram;
    private readonly LayoutCache _layouts;
    private readonly bool _ownsLayouts;
    // 关闭时（正式预览／导出）图、材质与空原始部件判定都不为检查同步提交或读回中间产物。
    private readonly bool _graphPixelChecks;
    // 只在设备锁内的单次 RenderToOutput 期间存在：主画布与各材质／图背景输入共用同一背景帧。
    private BackgroundFrameScope? _backgroundScope;
    private bool _disposed;

    /// <summary>创建一个渲染管线。</summary>
    /// <param name="layoutEngine">
    /// 文字布局引擎；为 <see langword="null"/> 时自建一个（此时由本实例负责释放）。
    /// </param>
    /// <param name="backgroundResolver">
    /// 背景帧来源的解析器；为 <see langword="null"/> 时使用只认识图片背景的默认实现。
    /// <b>视频背景将来在组合根传入自己的解析器即可接入</b>，本类的绘制代码不变（ADR-0036）。
    /// 传入的实例由调用方负责释放。
    /// </param>
    /// <param name="surfaceBudget">固定会话中间表面账本；null 时自建，传入时借用且不释放。</param>
    public SkiaRenderPipeline(
        SkiaTextLayoutEngine? layoutEngine = null,
        IBackgroundFrameSourceResolver? backgroundResolver = null,
        ImageSurfaceBudgetSession? surfaceBudget = null)
        : this(layoutEngine, backgroundResolver, surfaceBudget, null, false, null, true) { }

    /// <summary>创建借用帧外固定图资源的生产管线；资源集合由调用方在管线后释放。</summary>
    public SkiaRenderPipeline(SkiaTextLayoutEngine? layoutEngine, IBackgroundFrameSourceResolver? backgroundResolver,
        ImageSurfaceBudgetSession? surfaceBudget, SceneImageEffectResources? imageEffects, bool ownsLayoutEngine = false,
        SceneSvgEffectResources? svgEffects = null)
        : this(layoutEngine, backgroundResolver, surfaceBudget, imageEffects, ownsLayoutEngine, null, true, svgEffects: svgEffects) { }

    internal SkiaRenderPipeline(SkiaTextLayoutEngine? layoutEngine, IBackgroundFrameSourceResolver? backgroundResolver,
        ImageSurfaceBudgetSession? surfaceBudget, SceneImageEffectResources? imageEffects, bool ownsLayoutEngine,
        ISkiaRenderDevice? device, bool ownsDevice, LayoutCache? layouts = null, SceneSvgEffectResources? svgEffects = null,
        bool graphPixelChecks = true)
    {
        // Create the shared device before allocating any other native resource. A failed
        // explicit Vulkan initialization therefore cannot leak the layout engine or budget.
        _device = device ?? SkiaRenderDeviceFactory.Create();
        _deviceLifetime = device is null || ownsDevice ? new(_device) : null;
        _ownsLayoutEngine = layoutEngine is null || ownsLayoutEngine;
        _ownsSurfaceBudget = surfaceBudget is null;
        _imageEffects = imageEffects;
        _svgEffects = svgEffects;
        _layouts = layouts ?? new();
        _ownsLayouts = layouts is null;
        _graphPixelChecks = graphPixelChecks;
        SkiaTextLayoutEngine? initializedLayout = null;
        ImageSurfaceBudgetSession? initializedBudget = null;
        IBackgroundFrameSourceResolver? initializedResolver = null;
        try
        {
            _layoutEngine = initializedLayout = layoutEngine ?? new SkiaTextLayoutEngine();
            _backgroundResolver = initializedResolver = backgroundResolver ?? new BackgroundFrameSourceResolver();
            _surfaceBudget = initializedBudget = surfaceBudget ?? new();
            if (imageEffects is { HasSpriteImages: true }) _spriteProgram = new();
            _compositionProgram = new(_device, _surfaceBudget.Limits, _deviceLifetime, pixelChecks: graphPixelChecks);
        }
        catch
        {
            _spriteProgram?.Dispose();
            if (_ownsLayoutEngine) initializedLayout?.Dispose();
            if (_ownsSurfaceBudget) initializedBudget?.Dispose();
            if (backgroundResolver is null) initializedResolver?.Dispose();
            _deviceLifetime?.Dispose();
            throw;
        }
    }

    /// <inheritdoc/>
    public string BackendName => _device.Info.Backend.Equals("vulkan", StringComparison.OrdinalIgnoreCase)
        ? "Skia（Vulkan Ganesh GPU）" : "Skia（CPU 光栅）";

    /// <inheritdoc/>
    public RenderFrame Render(RenderRequest request) => RenderCore(request, null);

    private RenderFrame RenderCore(RenderRequest request, SceneFrame? measuredScene)
    {
        lock (_device.SynchronizationRoot)
            return RenderToOutput(request, measuredScene, (surface, info, target, diagnostics) =>
            {
                int stride = checked(target.PixelWidth * 4);
                byte[] buffer = ArrayPool<byte>.Shared.Rent(checked(stride * target.PixelHeight));
                try
                {
                    _device.FlushAndSubmit(surface);
                    unsafe
                    {
                        fixed (byte* pixels = buffer)
                            if (!_device.ReadPixels(surface, info, (IntPtr)pixels, stride))
                                throw new InvalidOperationException($"Skia {_device.Info.Backend} 表面 readback 失败（{_device.Info}）。");
                    }
                    return new RenderFrame(buffer, target, PixelFormat.Bgra8888Premultiplied, request.TimeUs, pooledBuffer: true)
                    { MaterialDiagnostics = diagnostics.Materials, ImageEffectDiagnostics = diagnostics.Images, DiagnosticSnapshot = diagnostics.Scene };
                }
                catch { ArrayPool<byte>.Shared.Return(buffer); throw; }
            });
    }

    /// <summary>同一绘制核心输出本机 GPU 租约；不分配最终 CPU 像素或执行最终回读。</summary>
    public GpuRenderFrame RenderGpu(RenderRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_device.SynchronizationRoot)
        {
            if (_device is not VulkanSkiaRenderDevice gpu || !gpu.SupportsGpuFrames)
                throw new GpuPreviewException("GpuPreviewUnavailable", "This pipeline did not enable shared Vulkan GPU output.");
            gpu.EnsureGpuTargetInitialized(request.ResolveTarget());
            return RenderToOutput(request, null, (surface, _, target, diagnostics) =>
                new GpuRenderFrame(target, request.TimeUs, gpu.CopyGpuFrame(surface, target))
                { MaterialDiagnostics = diagnostics.Materials, ImageEffectDiagnostics = diagnostics.Images, DiagnosticSnapshot = diagnostics.Scene });
        }
    }

    private sealed record DrawDiagnostics(ImmutableArray<RenderMaterialDiagnostic> Materials,
        ImmutableArray<RenderImageEffectDiagnostic> Images, RuntimeDiagnosticSnapshot Scene);

    private T RenderToOutput<T>(RenderRequest request, SceneFrame? measuredScene,
        Func<SKSurface, SKImageInfo, RenderTarget, DrawDiagnostics, T> output)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(_disposed, this);

        request.CancellationToken.ThrowIfCancellationRequested();
        PreparedRenderFrame.ValidateRequest(request);
        ValidateNativePreparedFrame(request.PreparedFrame);
        if (_imageEffects is null && request.PreparedFrame is { ImageEffects.IsEmpty: false } pending)
        {
            var owner = pending.ImageEffects[0];
            throw new EffectFailureException(owner.InstanceId, owner.ArtifactHash, "RenderImageEffects", request.TimeUs,
                "ImageEffectResourcesRequired", new PreparedFrameException("ImageEffectResourcesRequired", "生产图效果需要当前修订的固定程序集合，不能忽略图绑定。"), owner.PackageIdentity);
        }

        if (_imageEffects is null && request.PreparedFrame is { Materials.IsEmpty: false } materialFrame)
        {
            var owner = materialFrame.Materials[0];
            throw owner.Owner.Failure("RenderMaterials", request.TimeUs,
                "MaterialResourcesRequired", new PreparedFrameException("MaterialResourcesRequired", "材质须提供真实固定程序集合，不能绘制白色种子。"));
        }
        if (_imageEffects is null && request.PreparedFrame is { BaseMaterials.IsEmpty: false } baseMaterialFrame)
        {
            var binding = baseMaterialFrame.BaseMaterials[0];
            throw binding.Owner.Failure("RenderMaterials", request.TimeUs, "MaterialResourcesRequired",
                MaterialTargetError(new PreparedFrameException("MaterialResourcesRequired", "Base 材质须有固定程序集合。"),
                    new Scripting.V2.TextTarget.Base(binding.Target), "MaterialResourcesRequired"));
        }
        Composition composition = request.Project.Composition;
        RenderTarget target = request.ResolveTarget();

        if (target.PixelWidth <= 0 || target.PixelHeight <= 0)
        {
            throw new InvalidOperationException($"渲染目标尺寸非法：{target.PixelWidth}×{target.PixelHeight}。");
        }

        using var srgb = request.PreparedFrame is { } colorFrame && (!colorFrame.Materials.IsEmpty || !colorFrame.BaseMaterials.IsEmpty || !colorFrame.ImageEffects.IsEmpty || !colorFrame.GroupOpacities.IsEmpty || colorFrame.OverlayBatches.Any(batch => batch.ItemCount > 0 && (batch is Scripting.SpriteBatch or Scripting.PathBatch or Scripting.AtlasSpriteBatch))) ? SKColorSpace.CreateSrgb() : null;
        SKImageInfo info = new(target.PixelWidth, target.PixelHeight, SKColorType.Bgra8888, SKAlphaType.Premul, srgb);
        using var backgroundScope = BeginBackgroundFrameScope(request);
        ImmutableArray<RenderImageEffectDiagnostic> imageDiagnostics = [];
        ImmutableArray<RenderMaterialDiagnostic> materialDiagnostics = [];
        RuntimeDiagnosticSnapshot sceneDiagnostics = request.PreparedFrame?.DiagnosticSnapshot ?? RuntimeDiagnosticSnapshot.Empty;
        using (SKSurface surface = _device.CreateSurface(info))
        {
            SKCanvas canvas = surface.Canvas;
            canvas.ClipRect(new(0, 0, target.PixelWidth, target.PixelHeight));

            // 透明输出（EXPORT-07）：从全透明开始，并且下面不画背景层。
            canvas.Clear(
                request.TransparentBackground
                    ? SKColors.Transparent
                    : ToSkColor(composition.BackgroundColor));

            double scaleX = target.ScaleXOf(composition);
            double scaleY = target.ScaleYOf(composition);

            canvas.Save();
            canvas.Scale((float)scaleX, (float)scaleY);

            // 先背景后歌词：背景色已在 Clear 时铺满画布，背景图是它上面的一层。
            // 透明输出时两层都不画——底色与背景图都算"背景"。
            if (!request.TransparentBackground)
            {
                DrawBackground(canvas, request, composition);
            }

            using var frameResources = _surfaceBudget.BeginFrame();
            using var spriteImages = BorrowSpriteImages(request, frameResources);
            using var svgInputs = BorrowSvgInputs(request, frameResources);
            var svgWork = new SvgRenderWorkBudget(_svgEffects?.MaximumFrameOperations ?? 200_000,
                _svgEffects?.MaximumFrameFilterBytes ?? 128L * 1024 * 1024);
            using var backdropFrame = CaptureSceneBackdrop(surface, request, frameResources, svgWork);
            DrawOverlays(canvas, request, Scripting.OverlayLayer.BehindText, frameResources, svgWork);
            if (request.PreparedFrame is { } preparedScene && (!preparedScene.Materials.IsEmpty || !preparedScene.BaseMaterials.IsEmpty || !preparedScene.ImageEffects.IsEmpty || !preparedScene.GroupOpacities.IsEmpty))
            {
                imageDiagnostics = DrawImageEffectScene(surface, request, frameResources, backdropFrame, out var drawingDiagnostics, out materialDiagnostics);
                sceneDiagnostics = sceneDiagnostics.Append(drawingDiagnostics);
            }
            else DrawLyrics(canvas, request, composition, measuredScene, frameResources);
            DrawOverlays(canvas, request, Scripting.OverlayLayer.AboveText, frameResources, svgWork);

            canvas.Restore();
            return output(surface, info, target, new(materialDiagnostics, imageDiagnostics, sceneDiagnostics));
        }
    }

    /// <summary>画背景层（没有媒体或媒体不可用时什么都不画，只剩背景色）。</summary>
    /// <remarks>
    /// 落位计算在 <c>Core</c> 的 <see cref="BackgroundPlacement" /> 里（纯几何，可单测），
    /// 这里只把两个矩形交给后端。坐标是<b>画布坐标</b>，因此预览缩放自动跟随。
    /// </remarks>
    private void DrawBackground(SKCanvas canvas, RenderRequest request, Composition composition)
    {
        BackgroundSource background = request.Project.Background;

        // 图片与视频在这里是同一件事：都只是"这一刻要画的一张位图"。
        // ⚠️ 这里原本写的是 HasImage（那时还没有视频）：视频背景会因为这一句被整个跳过，
        // 而且不报错、画面只剩背景色——正是"漏了处理分支"最难看出来的那种表现。
        if (!background.HasMedia)
        {
            return;
        }

        BackgroundFrame? frame = ResolveBackgroundFrame(request);

        if (frame is null)
        {
            return;
        }

        DrawBackgroundFrame(canvas, frame, background, composition);
    }

    private sealed record BackgroundFrame(SKBitmap Bitmap, int SourceWidth, int SourceHeight);

    /// <summary>本次渲染内每个请求只向来源取一次背景帧（来源接口约定「每帧只问一次」）。</summary>
    /// <remarks>
    /// 可变位图（视频每帧复用的那张）每次 DrawBitmap 都会复制像素并以新身份上传纹理；
    /// 这里固定一份不可变副本，本帧所有背景绘制共享同一像素引用，GPU 纹理缓存可以命中。
    /// 图片来源解码后即不可变，直接共享，且跨帧命中。
    /// </remarks>
    private BackgroundFrame? ResolveBackgroundFrame(RenderRequest request)
    {
        if (!request.Project.Background.HasMedia) return null;
        if (_backgroundScope is not { } scope || !ReferenceEquals(scope.Request, request)) return ResolveBackgroundFrameCore(request);
        if (scope.Resolved) return scope.Frame;
        var frame = ResolveBackgroundFrameCore(request);
        if (frame is { Bitmap.IsImmutable: false })
        {
            var copy = frame.Bitmap.Copy() ?? throw new InvalidOperationException("无法固定本帧背景位图。");
            copy.SetImmutable();
            scope.Copy = copy;
            frame = frame with { Bitmap = copy };
        }
        scope.Frame = frame; scope.Resolved = true;
        return frame;
    }

    private BackgroundFrameScope? BeginBackgroundFrameScope(RenderRequest request)
    {
        if (_backgroundScope is not null) return null;
        return _backgroundScope = new(this, request);
    }

    private sealed class BackgroundFrameScope(SkiaRenderPipeline owner, RenderRequest request) : IDisposable
    {
        internal RenderRequest Request { get; } = request;
        internal bool Resolved { get; set; }
        internal BackgroundFrame? Frame { get; set; }
        internal SKBitmap? Copy { get; set; }
        public void Dispose()
        {
            if (ReferenceEquals(owner._backgroundScope, this)) owner._backgroundScope = null;
            Frame = null; Copy?.Dispose(); Copy = null;
        }
    }

    private BackgroundFrame? ResolveBackgroundFrameCore(RenderRequest request)
    {
        var source = _backgroundResolver.Resolve(request.Project.Background, request.CancellationToken);
        if (source is null) return null;
        // Raster nearest sampling has a stable integer phase. Vulkan texture sampling at
        // exact texel boundaries can choose either neighbour across the same draw, and a
        // local capture changes that distribution. Keep its original pixels for both paths;
        // one decoder size also prevents render/capture from repeatedly restarting FFmpeg.
        var bitmap = _device is CpuSkiaRenderDevice
            ? source.GetFrame(request.TimeUs, request.Project.Composition, request.ResolveTarget(),
                request.Project.Background.Fit, request.CancellationToken)
            : source.GetFrame(request.TimeUs, request.CancellationToken);
        return bitmap is null ? null : new BackgroundFrame(bitmap,
            source.SourceWidth > 0 ? source.SourceWidth : bitmap.Width,
            source.SourceHeight > 0 ? source.SourceHeight : bitmap.Height);
    }

    private static void DrawBackgroundFrame(SKCanvas canvas, BackgroundFrame frame, BackgroundSource background, Composition composition)
    {

        BackgroundPlacement placement = BackgroundPlacement.Compute(
            frame.SourceWidth,
            frame.SourceHeight,
            composition.CanvasWidth,
            composition.CanvasHeight,
            background.Fit);

        if (placement.IsEmpty)
        {
            return;
        }

        // 不用抗锯齿：位图是轴对齐的，抗锯齿只会在边缘引入半透明像素，
        // 让"图片有没有铺到位"变得难以断言，画面本身也不因此更准。
        using SKPaint paint = new();

        var source = placement.Source;
        float scaleX = (float)frame.Bitmap.Width / frame.SourceWidth;
        float scaleY = (float)frame.Bitmap.Height / frame.SourceHeight;
        canvas.DrawBitmap(
            frame.Bitmap,
            ToSkRect(new BackgroundRect(source.X * scaleX, source.Y * scaleY, source.Width * scaleX, source.Height * scaleY)),
            ToSkRect(placement.Destination),
            paint);
    }

    private static SKRect ToSkRect(BackgroundRect rect) =>
        new(rect.X, rect.Y, rect.X + rect.Width, rect.Y + rect.Height);

    /// <inheritdoc/>
    public void Dispose()
    {
        lock (_device.SynchronizationRoot) DisposeCore();
        GC.SuppressFinalize(this);
    }

    private void DisposeCore()
    {
        if (_disposed)
        {
            return;
        }

        if (_ownsLayouts) _layouts.Clear();
        _compositionProgram.Dispose();
        if (_isolationResources.IsValueCreated) _isolationResources.Value.Dispose();
        _spriteProgram?.Dispose();
        if (_ownsSurfaceBudget) _surfaceBudget.Dispose();
        _backgroundResolver.Dispose();

        if (_ownsLayoutEngine)
        {
            _layoutEngine.Dispose();
        }

        _deviceLifetime?.Dispose();

        _disposed = true;
    }

    /// <summary>
    /// 画一帧的歌词层。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 样式按"基线 → 行覆盖"解析后用于整行（字体、字号、纵向位置、未唱/已唱颜色）；
    /// 元素级覆盖只会让<b>有可绘制覆盖</b>的那些元素多画几遍。
    /// 预设不在这里参与：工程里存的就是预设落地后的样式本身（见 <see cref="ProjectSnapshot" />）。
    /// </para>
    /// <para>
    /// 绘制顺序不能反：
    /// <list type="number">
    ///   <item>整行按未唱色打底，确立稳定布局；</item>
    ///   <item>有颜色覆盖的元素各自重画自己的未唱色；</item>
    ///   <item>当前元素按自己的逐字进度在未唱色与已唱色之间自然过渡；</item>
    ///   <item>裁切到已唱宽度画已唱色；</item>
    ///   <item>已唱区域里，有颜色覆盖的元素再补一遍它们自己的已唱色。</item>
    /// </list>
    /// </para>
    /// </remarks>
    private void DrawLyrics(SKCanvas canvas, RenderRequest request, Composition composition, SceneFrame? measuredScene = null, ImageSurfaceBudgetFrame? resources = null)
    {
        var frame = measuredScene ?? request.PreparedFrame?.HierarchyScene ?? GetSceneFrame(request.Project, request.TimeUs, request.LayoutLineId);
        foreach (var item in DrawItems(frame))
        {
            if (item.Line is { } line) DrawSceneLine(canvas, request, composition, line, resources: resources);
            else DrawTextMark(canvas, request, composition, item.Mark!, resources: resources);
        }
    }

    private void DrawSceneLine(SKCanvas canvas, RenderRequest request, Composition composition, SceneLineSample scene, CapturePaint? capture = null, ImageSurfaceBudgetFrame? resources = null)
    {
        LyricFrameSample sample = scene.Frame;
        if (sample.Line is not { } line || string.IsNullOrEmpty(line.Text))
        {
            return;
        }

        LyricStyle lineStyle = LineBaseStyle(request.Project.LyricStyle, line, scene.Branch);

        lineStyle = StaticStyleProjection.DrawingSeed(lineStyle);
        TextLayoutOptions options = LayoutOptionsFor(lineStyle);
        var layout = GetLineLayout(line, lineStyle);
        ShapedLine shaped = layout.Shaped;

        if (shaped.Elements.Count == 0)
        {
            return;
        }

        ImmutableArray<RubyLayout> ruby = capture?.Recipe.IncludeRuby == false ? [] : layout.Ruby;
        TextInkBounds? groupInk = GroupInk(shaped.InkBounds, ruby);
        LineRenderGeometry geometry = scene.Geometry!;
        float originX = (float)geometry.OriginX;
        float baselineY = (float)geometry.BaselineY;
        float pivotX = (float)geometry.Pivot.X, pivotY = (float)geometry.Pivot.Y;

        // 进入动画（ADR-0042）：
        // 不透明度用<b>图层 alpha</b> 表达——它对渐变、描边、阴影一视同仁，
        // 不必逐个颜色去乘 alpha（那样必然会漏掉某条绘制路径）；
        // 位移与缩放走画布变换，且作用在"行自己的样式缩放"<b>之外</b>：
        // 入场效果不该改变这一行的排版。
        SceneTransform animation = scene.Transform;
        bool animationLayer = capture?.IsCoverage != true && capture?.SuppressCommonOpacity != true && animation.Opacity < 1;
        using var animationReservation = animationLayer ? capture?.Layer("CaptureEntranceLayer") : null;
        canvas.Save();
        if (animationLayer)
        {
            using SKPaint layerPaint = new() { Color = SKColors.White.WithAlpha(ToOpacityByte((float)animation.Opacity)) };
            canvas.SaveLayer(layerPaint);
        }
        var matrix = geometry.WorldMatrix!.Value;
        canvas.Concat(new SKMatrix((float)matrix.A, (float)matrix.C, (float)matrix.Tx,
            (float)matrix.B, (float)matrix.D, (float)matrix.Ty, 0, 0, 1));

        // 草稿档位关掉抗锯齿：只影响画质成本，不影响几何（roadmap RENDER-04 的底线）。
        bool antialias = request.Quality != RenderQuality.Draft;

        var familyTransforms = request.PreparedFrame?.TokenTransforms
            .Where(item => item.Target.BindingId.Value == scene.BindingId).ToImmutableArray() ?? [];
        var familyStyles = request.PreparedFrame?.TokenStyles
            .Where(item => item.Target.BindingId.Value == scene.BindingId).ToImmutableArray() ?? [];
        var warpedFamilies = request.PreparedFrame?.FinalGeometry
            .Where(item => item.WithRuby.BindingId.Value == scene.BindingId
                && item.Clusters.Any(static cluster => HasEffectiveWarp(cluster.Geometry.Mapping)))
            .ToDictionary(static item => item.WithRuby.TokenId.Value, StringComparer.Ordinal) ?? [];
        if (capture?.TokenId is not null || capture?.ExcludedTokens is { Count: > 0 } || !familyTransforms.IsEmpty || !familyStyles.IsEmpty
            || warpedFamilies.Count != 0 || HasTextPartitions(request.PreparedFrame, scene.BindingId!)
            || sample.TokensMatchLineText && line.Tokens.Any(static token => TokenStyleSupport.HasDrawableOverride(token.StyleOverride)))
        {
            DrawChangedFamilies(canvas, request, request.PreparedFrame, scene, shaped, options, ruby,
                lineStyle, composition, originX, baselineY, antialias, familyTransforms, familyStyles, capture, warpedFamilies, resources ?? capture?.Budget);
            RestoreAnimationLayer(canvas, animationLayer);
            canvas.Restore();
            return;
        }

        DrawLineGlow(canvas, shaped, options, originX, baselineY, lineStyle, antialias, capture: capture);

        // 每个区域只绘制一次最终样式。已唱区域由每个有时间的元素各自贡献，
        // 因此夹在两个已定时元素之间的未定时元素不会被连续填充误染成已唱色。
        using SKPath sungRegions = BuildSungRegions(sample, shaped, originX, canvas.LocalClipBounds);
        bool hasSungRegions = !sungRegions.IsEmpty;
        DrawStylePass(false);
        if (hasSungRegions) DrawStylePass(true);

        void DrawStylePass(bool sung)
        {
            canvas.Save();
            SKRect bounds = canvas.LocalClipBounds;
            if (hasSungRegions)
                ClipSungRegions(canvas, sungRegions, sung);

            canvas.Save();
            foreach (TokenSample token in sample.Tokens)
            {
                if (ElementOverlay(line, token) is not { } elementOverlay) continue;
                SKRect box = ElementBox(originX, shaped, token, bounds, CapturePadding(lineStyle, elementOverlay, capture));
                // 排除的是同一个矩形（含装饰外扩），因此这里与元素那一遍严格互补（ADR-0044）。
                if (box.Right > box.Left) canvas.ClipRect(box, SKClipOperation.Difference);
            }
            DrawStyledLine(canvas, shaped, options, originX, baselineY,
                sung ? lineStyle.SungFill.Color : lineStyle.UnsungFill.Color,
                sung ? lineStyle.SungFill.Gradient : lineStyle.UnsungFill.Gradient,
                lineStyle, composition, antialias, capture: capture, sungPass: sung);
            canvas.Restore();
            DrawTokenOverrides(canvas, shaped, options, sample, line, originX, baselineY,
                antialias, lineStyle, composition, sung, capture: capture);
            canvas.Restore();
        }

        DrawRuby(canvas, ruby, sample, line, lineStyle, composition, originX, baselineY, antialias, capture: capture);
        RestoreAnimationLayer(canvas, animationLayer);
        canvas.Restore();
    }

    /// <summary>
    /// 把有元素级覆盖的元素按它们自己的样式重画一遍（颜色族、描边、阴影、不透明度）。
    /// </summary>
    /// <param name="canvas">目标画布。</param>
    /// <param name="shaped">整行布局。</param>
    /// <param name="options">布局选项。</param>
    /// <param name="sample">时刻采样结果。</param>
    /// <param name="line">当前行。</param>
    /// <param name="originX">整行左端横坐标。</param>
    /// <param name="baselineY">基线纵坐标。</param>
    /// <param name="antialias">是否开抗锯齿。</param>
    /// <param name="lineStyle">行样式，作为元素没有覆盖时的回退。</param>
    /// <param name="composition">当前合成尺寸，用于画布坐标渐变。</param>
    /// <param name="isSungPass">是否为"已唱区域补色"这一遍。</param>
    /// <param name="drawUnitIndices">要绘制的固定单元；default 为整行，空数组不画。</param>
    /// <param name="excludedTokens">已经按独立固定单元绘制的源覆盖。</param>
    /// <param name="capture">显式捕获通道；null 保持常规颜色绘制。</param>
    private void DrawTokenOverrides(
        SKCanvas canvas,
        ShapedLine shaped,
        TextLayoutOptions options,
        LyricFrameSample sample,
        LyricLine line,
        float originX,
        float baselineY,
        bool antialias,
        LyricStyle lineStyle,
        Composition composition,
        bool isSungPass,
        ImmutableArray<int> drawUnitIndices = default, HashSet<int>? excludedTokens = null, CapturePaint? capture = null)
    {
        foreach (TokenSample token in sample.Tokens)
        {
            if (excludedTokens?.Contains(token.Index) == true) continue;
            if (ElementOverlay(line, token) is not { } overlay)
            {
                continue;
            }

            var bounds = canvas.LocalClipBounds;
            SKRect box = ElementBox(originX, shaped, token, bounds, CapturePadding(lineStyle, overlay, capture));

            // 外层已按每个元素的实际进度剪裁；这里只限定该覆盖自身的装饰区域。
            if (box.Right <= box.Left)
            {
                continue;
            }

            LyricStyle tokenStyle = ResolveElementStyle(lineStyle, overlay);

            RgbaColor color = isSungPass ? tokenStyle.SungColor : tokenStyle.UnsungColor;
            GradientSpec? gradient = isSungPass
                ? tokenStyle.SungFill.Gradient
                : tokenStyle.UnsungFill.Gradient;

            canvas.Save();
            canvas.ClipRect(box);
            DrawStyledLine(
                canvas,
                shaped,
                options,
                originX,
                baselineY,
                color,
                gradient,
                tokenStyle,
                composition,
                antialias,
                drawUnitIndices, capture, isSungPass);
            canvas.Restore();
        }
    }

    /// <summary>这个元素要脱离整行、按自己的样式重画的覆盖；不需要重画时为 <see langword="null"/>。</summary>
    private static LyricStyleOverride? ElementOverlay(LyricLine line, TokenSample token) =>
        token.Index < line.Tokens.Length && TokenStyleSupport.HasDrawableOverride(line.Tokens[token.Index].StyleOverride)
            ? line.Tokens[token.Index].StyleOverride
            : null;

    /// <summary>按元素覆盖解析出的样式。</summary>
    private static LyricStyle ResolveElementStyle(LyricStyle lineStyle, LyricStyleOverride overlay) =>
        StaticStyleProjection.DrawingSeed(LyricStyleResolver.Resolve(lineStyle, preset: null, lineOverride: null, tokenOverride: overlay).Style);

    /// <summary>元素区域要连同装饰一起占用的外扩量（描边外半宽、阴影偏移与模糊）。</summary>
    /// <param name="lineStyle">整行样式。</param>
    /// <param name="overlay">元素覆盖。</param>
    /// <returns>像素数。</returns>
    /// <remarks>
    /// 描边与阴影会画到字框之外：只按字框排除时，整行那一遍仍会把元素的描边外沿画出来，
    /// 元素即使明确关闭了描边也擦不掉它（实测表现为字框边界上一列基线颜色）。
    /// 这里取"整行样式"与"元素样式"两者外扩量的较大者，因为整行那一遍画的是整行样式，
    /// 而元素那一遍要能覆盖同一片区域。
    /// </remarks>
    private static float ElementPadding(LyricStyle lineStyle, LyricStyleOverride overlay) =>
        MathF.Max(DecorationExtent(lineStyle), DecorationExtent(ResolveElementStyle(lineStyle, overlay)));

    /// <summary>一个样式会画到字框之外的像素数。</summary>
    private static float DecorationExtent(LyricStyle style)
    {
        StrokeSpec? strokeSpec = LyricStyleEffects.GetStroke(style);
        ShadowSpec? shadowSpec = EffectiveShadow(style);
        float stroke = strokeSpec is { IsEnabled: true } ? MathF.Max(strokeSpec.WidthPx, 0f) : 0f;
        float shadow = shadowSpec is { IsEnabled: true }
            ? MathF.Max(MathF.Abs(shadowSpec.OffsetXPx), MathF.Abs(shadowSpec.OffsetYPx)) + MathF.Max(shadowSpec.BlurRadiusPx, 0f) : 0f;
        float legacy = MathF.Min(stroke + shadow + 2f, 64f);
        return style.Glow is { IsEnabled: true } glow ? Math.Max(legacy, (float)(ValidateGlow(glow) + 2)) : legacy;
    }

    /// <summary>元素的像素对齐字框；"排除基线"与"重画元素"两遍共用同一个矩形。</summary>
    /// <param name="originX">整行左端横坐标。</param>
    /// <param name="shaped">整行布局。</param>
    /// <param name="token">元素采样。</param>
    /// <param name="bounds">当前剪裁区的上下边界。</param>
    /// <param name="padding">装饰外扩量。</param>
    /// <returns>字框。</returns>
    /// <remarks>
    /// <para>
    /// 两遍的可见区域必须严格互补，否则接缝处会漏出被排除那一遍画的描边／阴影
    /// （实测表现为边界上一列半透明的基线颜色）。
    /// </para>
    /// <para>
    /// 两遍的矩形由同一段算式得出（同一个字框、同一个外扩量），因此严格互补。
    /// 卡拉 OK 进度那条边界不在此列：它是"已唱到哪"，仍按自己的方式画。
    /// </para>
    /// </remarks>
    private static SKRect ElementBox(float originX, ShapedLine shaped, TokenSample token, SKRect bounds, float padding)
    {
        float left = originX + shaped.XAt(token.Utf16Start) - padding;
        float right = originX + shaped.XAt(token.Utf16End) + padding;

        return new SKRect(left, bounds.Top, MathF.Max(right, left), bounds.Bottom);
    }

    /// <summary>
    /// 按每个已定时元素的实际进度建立已唱区域。
    /// </summary>
    /// <param name="sample">时刻采样结果。</param>
    /// <param name="shaped">整行布局结果。</param>
    /// <param name="originX">整行左端横坐标。</param>
    /// <param name="bounds">当前画布剪裁边界。</param>
    /// <returns>由调用方释放的已唱区域路径。</returns>
    /// <remarks>
    /// 未定时元素没有来源可靠的进度，即使后面的元素开始演唱，也不能用连续的
    /// 整行裁切区域把它填成已唱色。各元素仍共享同一次整行整形的坐标。
    /// 元素文本拼不回行文本时不推算逐字高亮。
    /// </remarks>
    private static SKPath BuildSungRegions(
        LyricFrameSample sample, ShapedLine shaped, float originX, SKRect bounds)
    {
        SKPath regions = new();
        if (!sample.HasTokens || !sample.TokensMatchLineText)
        {
            return regions;
        }

        foreach (TokenSample token in sample.Tokens)
        {
            if (token.Source != ProgressSource.FromToken || token.Progress <= 0d)
            {
                continue;
            }

            float left = originX + shaped.XAt(token.Utf16Start);
            float right = originX + shaped.XAt(token.Utf16End);
            float reached = token.IsSung
                ? right
                : MakoKaraGenerator.Scripting.TextProgressPath.XAt(left, right, token.Progress);
            if (reached > left)
                regions.AddRect(new SKRect(left, bounds.Top, reached, bounds.Bottom));
        }

        return regions;
    }

    private void ClipSungRegions(SKCanvas canvas, SKPath regions, bool sung)
    {
        var operation = sung ? SKClipOperation.Intersect : SKClipOperation.Difference;
        if (!_device.Info.Backend.Equals("vulkan", StringComparison.OrdinalIgnoreCase))
        {
            canvas.ClipPath(regions, operation, antialias: false);
            return;
        }
        // Ganesh can round analytic intersect and difference edges in opposite
        // directions. Both passes must use the same integer set of device pixels.
        using var devicePath = new SKPath(regions);
        devicePath.Transform(canvas.TotalMatrix);
        using var clipBounds = new SKRegion(canvas.DeviceClipBounds);
        using var pixels = new SKRegion();
        pixels.SetPath(devicePath, clipBounds);
        canvas.ClipRegion(pixels, operation);
    }

    /// <summary>
    /// 取得（并缓存）一行文本的布局结果。
    /// </summary>
    /// <remarks>
    /// 布局只与"文本 + 字体 + 字号"有关，与时刻无关——这正是"高亮时整行不跳动"的根据：
    /// 同一行在不同时刻复用同一份元素位置。
    /// </remarks>
    private ShapedLine GetLayout(string text, TextLayoutOptions options)
    {
        LayoutKey key = new(text, options.FamilyName, options.FontSizePx, options.FontWeight,
            options.IsBold, options.IsItalic, options.LetterSpacingPx, options.FallbackFamilyNames);

        return _layouts.GetOrAdd(key, _layoutEngine, text, options);
    }

    private static SKColor ToSkColor(RgbaColor color) => new(color.Red, color.Green, color.Blue, color.Alpha);

    /// <summary>关掉进入动画的不透明度图层（只有开过才关）。</summary>
    private static void RestoreAnimationLayer(SKCanvas canvas, bool hasLayer)
    {
        if (hasLayer)
        {
            canvas.Restore();
        }
    }

    /// <summary>把 0~1 的不透明度换算成字节；越界夹住而不是溢出。</summary>
    private static byte ToOpacityByte(float opacity) =>
        (byte)Math.Clamp((int)Math.Round(opacity * 255f), 0, 255);

    private static SKColor ToSkColorWithOpacity(RgbaColor color, float opacity)
    {
        byte alpha = (byte)Math.Clamp(Math.Round(color.Alpha * opacity), 0d, 255d);
        return new SKColor(color.Red, color.Green, color.Blue, alpha);
    }

    /// <summary>
    /// 把核心渐变描述采样成 Skia 着色器。
    /// </summary>
    /// <remarks>
    /// <para>
    /// Skia 的基础线性渐变不能表达 HSV 色相路径和线性 RGB 插值，因此先用核心的
    /// <see cref="GradientSampler"/> 采样，再把采样点交给 Skia。这保证预览与导出共享同一套
    /// 渐变取色规则；采样数足够高时，Skia 只在相邻采样点之间做很小的屏幕空间插值。
    /// </para>
    /// <para>0.5.0 首轮接入线性／径向和画布／行坐标空间；其他组合明确拒绝。</para>
    /// </remarks>
    private static SKShader? CreateGradientShader(
        GradientSpec? gradient,
        ShapedLine shaped,
        float originX,
        float baselineY,
        LyricStyle style,
        Composition composition)
    {
        if (gradient is null || !GradientSupport.IsSupported(gradient))
        {
            return null;
        }

        const int sampleCount = 65;
        SKColor[] colors = new SKColor[sampleCount];
        float[] positions = new float[sampleCount];

        try
        {
            for (int index = 0; index < sampleCount; index++)
            {
                float position = index / (float)(sampleCount - 1);
                RgbaColor color = gradient.Kind == GradientKind.Linear
                    ? GradientSampler.Sample(gradient, position)
                    : GradientSampler.SampleAt(
                        gradient,
                        gradient.CenterX + (position * gradient.RadiusX),
                        gradient.CenterY);
                colors[index] = ToSkColorWithOpacity(color, style.Opacity);
                positions[index] = position;
            }
        }
        catch (NotSupportedException)
        {
            return null;
        }

        float rectLeft = gradient.CoordinateSpace == GradientCoordinateSpace.Canvas ? 0f : originX;
        float rectTop = gradient.CoordinateSpace == GradientCoordinateSpace.Canvas
            ? 0f
            : baselineY - (style.FontSizePx * 0.85f);
        float width = gradient.CoordinateSpace == GradientCoordinateSpace.Canvas
            ? Math.Max(1f, composition.CanvasWidth)
            : Math.Max(1f, shaped.Width);
        float height = gradient.CoordinateSpace == GradientCoordinateSpace.Canvas
            ? Math.Max(1f, composition.CanvasHeight)
            : Math.Max(1f, style.FontSizePx);
        float centerX = rectLeft + (width * 0.5f);
        float centerY = rectTop + (height * 0.5f);
        if (gradient.Kind == GradientKind.Radial)
        {
            centerX = rectLeft + ((float)gradient.CenterX * width);
            centerY = rectTop + ((float)gradient.CenterY * height);
        }

        if (gradient.Kind == GradientKind.Radial)
        {
            SKMatrix matrix = SKMatrix.CreateScale(
                1f / Math.Max(1e-4f, (float)(gradient.RadiusX * width)),
                1f / Math.Max(1e-4f, (float)(gradient.RadiusY * height)),
                centerX,
                centerY);
            return SKShader.CreateRadialGradient(
                new SKPoint(centerX, centerY),
                1f,
                colors,
                positions,
                SKShaderTileMode.Clamp,
                matrix);
        }

        float radians = (float)(gradient.AngleDegrees * (Math.PI / 180d));
        float directionX = MathF.Cos(radians);
        float directionY = MathF.Sin(radians);
        // 取矩形在该方向上的投影半长，使 0°/90° 时停止点分别贴住行的左右/上下边界，
        // 斜向时也刚好覆盖整行，而不是因使用对角线长度而把端点推到可见区域之外。
        float halfLength = (MathF.Abs(directionX) * width + MathF.Abs(directionY) * height) * 0.5f + 1f;

        SKPoint direction = new(directionX * halfLength, directionY * halfLength);

        return SKShader.CreateLinearGradient(
            new SKPoint(centerX - direction.X, centerY - direction.Y),
            new SKPoint(centerX + direction.X, centerY + direction.Y),
            colors,
            positions,
            SKShaderTileMode.Clamp);
    }

    /// <summary>布局缓存的键：文本 + 字体 + 字号（与时刻无关）。</summary>
    internal readonly record struct LayoutKey(string Text, string? FamilyName, float FontSizePx,
        int FontWeight, bool IsBold, bool IsItalic, float LetterSpacingPx, ImmutableArray<string> FallbackFamilyNames);

    // The owner shares this only among renderers created for the same fixed font set.
    internal sealed class LayoutCache
    {
        private readonly ConcurrentDictionary<LayoutKey, ShapedLine> _entries = new();
        private readonly ConditionalWeakTable<SkiaTextLayoutEngine, ConcurrentDictionary<LayoutKey, ShapedLine>> _engineEntries = new();
        private readonly object _gate = new();
        private readonly object _rubyGate = new();
        private readonly Dictionary<(LyricLine, LyricStyle), LineLayout> _ruby = [];
        private readonly ConditionalWeakTable<SkiaTextLayoutEngine, Dictionary<(LyricLine, LyricStyle), LineLayout>> _engineRuby = new();
        internal int Count => _entries.Count;
        internal int RubyBuildCount { get; private set; }
        internal void Clear()
        {
            _entries.Clear(); _engineEntries.Clear();
            lock (_rubyGate) { _ruby.Clear(); _engineRuby.Clear(); }
        }

        internal LineLayout GetRuby(LyricLine line, LyricStyle style, SkiaTextLayoutEngine engine, Func<LineLayout> build)
        {
            lock (_rubyGate)
            {
                var key = (line, style);
                if (_ruby.TryGetValue(key, out var result)) return result;
                var local = _engineRuby.GetOrCreateValue(engine);
                if (local.TryGetValue(key, out result)) return result;
                result = build(); RubyBuildCount++;
                // 动态回退字体的字形身份属于原引擎；只有全部布局可共享时才跨管线复用解算。
                bool share = engine.CanShareLayout(result.Shaped, LayoutOptionsFor(style))
                    && result.Ruby.All(ruby => engine.CanShareLayout(ruby.Shaped, ruby.Options));
                var entries = share ? _ruby : local;
                if (entries.Count >= 256) entries.Clear();
                entries.Add(key, result);
                return result;
            }
        }

        internal ShapedLine GetOrAdd(LayoutKey key, SkiaTextLayoutEngine engine, string text, TextLayoutOptions options)
        {
            if (_entries.TryGetValue(key, out var existing)) return existing;
            var local = _engineEntries.GetOrCreateValue(engine);
            if (local.TryGetValue(key, out existing)) return existing;
            var shaped = engine.Shape(text, options);
            if (!engine.CanShareLayout(shaped, options))
            {
                lock (local)
                {
                    if (local.TryGetValue(key, out existing)) return existing;
                    if (local.Count >= 2048) return shaped;
                    return local.GetOrAdd(key, shaped);
                }
            }
            lock (_gate)
            {
                if (_entries.TryGetValue(key, out existing)) return existing;
                if (_entries.Count >= 2048) return shaped;
                return _entries.GetOrAdd(key, shaped);
            }
        }
    }
}
