using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Media;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Rendering.Abstractions;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Scripting;
using MakoKaraGenerator.Scripting.Compilation;

namespace MakoKaraGenerator.Runtime.Hosting;

/// <summary>组合根实际装配：固定包、分析和native资源到独立异步像素会话。</summary>
public sealed class ProjectRenderSessionFactory(IScriptCompilationService compiler, IVideoFrameProvider videos, ReplayReason reason,
    Func<IBackgroundFrameSourceResolver>? backgroundResolverFactory = null,
    bool layoutReference = false, string? editingLineId = null, string? editingCueId = null,
    string? renderBackend = null, bool enableGpuPreview = false, bool enableGpuPreviewBenchmark = false, RuntimeGpuPreparation? gpuPreparation = null)
    : IRenderFrameSessionFactory
{
    private readonly SkiaRenderBackendOptions? _renderBackendOptions = renderBackend is null ? null : SkiaRenderBackendOptions.Parse(renderBackend);

    /// <inheritdoc/>
    public ValueTask<IRenderFrameSession> CreateAsync(ProjectSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new(Task.Run(() => CreateCoreAsync(snapshot, cancellationToken), cancellationToken));
    }
    private async Task<IRenderFrameSession> CreateCoreAsync(ProjectSnapshot snapshot, CancellationToken token)
    {
        List<CompiledPackageGraphExports> natives = []; List<SkiaGraphSurface> inputs = [];
        SceneImageEffectResources? resources = null; IEvaluatedFrameSource? source = null;
        IBackgroundFrameSourceResolver? resolver = null; SkiaRenderPipeline? renderer = null;
        ImageSurfaceBudgetSession? budget = null; IDisposable? fixedImages = null;
        ISkiaRenderDevice? device = null;
        try
        {
            token.ThrowIfCancellationRequested();
            if (gpuPreparation is not null && (!enableGpuPreview ||
                (_renderBackendOptions ?? SkiaRenderBackendOptions.FromEnvironment()).Backend != SkiaRenderBackend.Vulkan))
                throw new InvalidOperationException("GPU 预热只能转移给显式 Vulkan shared preview 会话。");
            device = gpuPreparation?.TakeDevice() ?? SkiaRenderDeviceFactory.Create(enableGpuPreview
                ? (_renderBackendOptions ?? SkiaRenderBackendOptions.FromEnvironment()) with { EnableGpuPreview = true }
                : _renderBackendOptions);
            if (enableGpuPreview && device is not VulkanSkiaRenderDevice)
                throw new GpuPreviewException("GpuPreviewGpuBackendRequired", "Shared GPU preview requires an explicitly selected Vulkan backend.");
            ScriptBackendRequirement.RequireSupported(snapshot, device is VulkanSkiaRenderDevice);
            ReadOnlyMemory<byte>[] fonts = [.. snapshot.Effects.Packages.SelectMany(package => package.Files)
                .Where(file => file.Role == "font").Select(file => file.Content.AsMemory())];
            SkiaRenderPipeline.LayoutCache layouts = new();
            SkiaRenderPipeline Renderer(IBackgroundFrameSourceResolver? background = null, ImageSurfaceBudgetSession? surfaces = null,
                SceneImageEffectResources? images = null) => new(fonts.Length == 0 ? null : new SkiaTextLayoutEngine(fonts),
                    background, surfaces, images, ownsLayoutEngine: fonts.Length != 0, device, ownsDevice: false, layouts);
            resolver = backgroundResolverFactory is null ? new BackgroundFrameSourceResolver(videos)
                : backgroundResolverFactory() ?? throw new InvalidOperationException("背景解析器工厂不能返回空值。");
            token.ThrowIfCancellationRequested();
            if (snapshot.EffectInputsFingerprint is null)
            {
                renderer = Renderer(resolver);
                token.ThrowIfCancellationRequested();
                return new ProjectRenderSession(snapshot, renderer, null, null, [], [], null, null, device, enableGpuPreviewBenchmark);
            }
            var compiled = await CompiledProjectEffects.CompileAsync(snapshot, compiler, new PackageImageValidator(), new PackageShaderValidator(),
                new PackageImageGraphValidator(), new PackageMaterialValidator(), token).ConfigureAwait(false);
            if (!compiled.Succeeded)
                throw new ProjectEffectCompilationException(compiled);
            budget = new(compiled.GraphBudget);
            List<SceneImageGraphProgram> graphs = []; List<SceneMaterialProgram> materials = [];
            List<KeyValuePair<ImageHandle, SkiaGraphSurface>> spriteImages = [];
            List<KeyValuePair<GraphHandle, IReadOnlyDictionary<string, SkiaGraphInput>>> images = [];
            List<KeyValuePair<MaterialHandle, IReadOnlyDictionary<string, SkiaGraphInput>>> materialImages = [];
            var bounds = new GraphRect(0, 0, snapshot.Composition.CanvasWidth, snapshot.Composition.CanvasHeight);
            long imageBytes = 0; int imageCount = 0;
            foreach (var package in compiled.Packages)
            {
                token.ThrowIfCancellationRequested();
                if (package.Definition.ImagePaths.Length > compiled.GraphBudget.MaximumSurfaces - imageCount)
                    throw new EffectPackageException("PackageImageBudgetExceeded", "files", "工程固定图片超过总表面预算。");
                var native = CompiledPackageGraphExports.CompileWithDevice(package.Definition,
                    compiled.GraphBudget with { MaximumBytes = compiled.GraphBudget.MaximumBytes - imageBytes }, device, token);
                natives.Add(native);
                foreach (var path in package.Definition.ImagePaths)
                {
                    var size = native.GetImagePixelSize(path);
                    imageBytes = checked(imageBytes + (long)size.Width * size.Height * 4); imageCount++;
                    var input = new GraphInputDefinition("fixed", GraphInputSource.ResourceImage, ShaderSurfaceKind.Color, true,
                        GraphBackgroundRequirement.None, GraphCoordinateSpace.Canvas, resourceId: path);
                    var surface = native.GetImageInput(input, new(new(0, 0, size.Width, size.Height), input.Space, SceneMatrix.Identity, 1, 1)).Surface;
                    inputs.Add(surface); spriteImages.Add(KeyValuePair.Create(native.Resources.GetImage(path), surface));
                }
                foreach (var graph in package.Definition.Graphs)
                {
                    var handle = native.Resources.GetGraph(graph.Id); graphs.Add(native.CreateProgram(handle, bounds));
                    images.Add(KeyValuePair.Create(handle, Images(package.Definition.ImageGraphs[graph.GraphPath])));
                }
                foreach (var material in package.Definition.Materials)
                {
                    var handle = native.Resources.GetMaterial(material.Id); materials.Add(native.CreateMaterialProgram(handle, bounds));
                    materialImages.Add(KeyValuePair.Create(handle, Images(package.Definition.ImageGraphs[material.GraphPath])));
                }
                IReadOnlyDictionary<string, SkiaGraphInput> Images(EffectPackageImageGraph graph)
                {
                    Dictionary<string, SkiaGraphInput> result = [];
                    foreach (var input in graph.Plan.Definition.Inputs.Where(input => input.Source == GraphInputSource.ResourceImage || input.Choices.ContainsValue(GraphInputSource.ResourceImage)))
                    {
                        token.ThrowIfCancellationRequested(); var size = native.GetImagePixelSize(input.ResourceId!);
                        var value = native.GetImageInput(input, new(new(0, 0, size.Width, size.Height), input.Space, SceneMatrix.Identity, 1, 1));
                        inputs.Add(value.Surface); result.Add(input.Id, value);
                    }
                    return result;
                }
            }
            resources = new(graphs, images, materials, materialImages, spriteImages);
            fixedImages = budget.HoldFixedImages(inputs);
            source = await compiled.CreateFactory(() => layoutReference || editingLineId is not null
                ? new MeasurementGeometryProvider(Renderer(), layoutReference, editingLineId, editingCueId)
                : Renderer(), reason).CreateAsync(snapshot, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            renderer = Renderer(resolver, budget, resources);
            token.ThrowIfCancellationRequested();
            return new ProjectRenderSession(snapshot, renderer, source, resources, natives, inputs, budget, fixedImages, device, enableGpuPreviewBenchmark);
        }
        catch (Exception error)
        {
            List<Exception> cleanupFailures = [];
            try { await ProjectRenderSession.ReleaseAsync(source, renderer, resources, fixedImages, inputs, natives, budget, device).ConfigureAwait(false); }
            catch (Exception cleanup) { cleanupFailures.Add(cleanup); }
            // A successfully created pipeline owns its resolver. Before that transfer,
            // including compiler failure and cancellation, this method owns it.
            if (renderer is null)
            {
                try { resolver?.Dispose(); }
                catch (Exception cleanup) { cleanupFailures.Add(cleanup); }
            }
            if (cleanupFailures.Count > 0) error.Data["RuntimeCleanupFailures"] = cleanupFailures.ToArray();
            throw;
        }
    }

    private sealed class MeasurementGeometryProvider(SkiaRenderPipeline renderer, bool layout,
        string? lineId, string? cueId) : ILineGeometryProvider, IDisposable
    {
        public SceneFrame GetSceneFrame(ProjectSnapshot snapshot, long timeUs, string? editingLineId = null)
            => renderer.GetMeasurementSceneFrame(snapshot, timeUs, layout, lineId ?? editingLineId, cueId);
        public LineRenderGeometry? GetLineGeometry(ProjectSnapshot snapshot, string id)
            => GetSceneFrame(snapshot, 0, id).Lines.FirstOrDefault(line => line.Frame.Line?.Id == id)?.Geometry;
        public void Dispose() => renderer.Dispose();
    }
}
