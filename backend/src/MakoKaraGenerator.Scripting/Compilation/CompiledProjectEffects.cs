using System.Collections.Immutable;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Scripting.Execution;
using MakoKaraGenerator.Scripting.Projection;

namespace MakoKaraGenerator.Scripting.Compilation;

/// <summary>持久工程闭包到真实控制器会话的投影；编译失败不改源数据。</summary>
public sealed class CompiledProjectEffects
{
    private CompiledProjectEffects(ProjectEffects source, ImmutableArray<CompiledEffectPackage> packages, ImmutableArray<CompiledFrameEffect> effects)
    { Source = source; Packages = packages; Effects = effects; }
    /// <summary>精确源数据，包括顺序、绑定、种子与预算。</summary>
    public ProjectEffects Source { get; }
    /// <summary>真实编译结果，源码失败保留原始行列诊断。</summary>
    public ImmutableArray<CompiledEffectPackage> Packages { get; }
    /// <summary>全部启用实例的真实生产描述，保持工程顺序。</summary>
    public ImmutableArray<CompiledFrameEffect> Effects { get; }
    /// <summary>全部启用包编译成功；不允许部分实例静默缺席。</summary>
    public bool Succeeded => Packages.All(p => p.Succeeded);
    private AudioFeatures? _audio;
    private string? _fingerprint;
    private ProjectSnapshot? _snapshot;
    /// <summary>会话前解析的全部源材质，纯资源工程也保留真实固定投影。</summary>
    public PreparedSourceMaterials? SourceMaterials { get; private set; }

    /// <summary>固定整个工程的已接受分析及源实例，再由正式资源门禁编译。</summary>
    public static Task<CompiledProjectEffects> CompileAsync(Project project, IScriptCompilationService compiler,
        IPackageImageValidator? images = null, IPackageShaderValidator? shaders = null, IPackageImageGraphValidator? graphs = null,
        IPackageMaterialValidator? materials = null, CancellationToken cancellationToken = default)
        => CompileAsync(project, compiler, images, shaders, graphs, materials, null, cancellationToken);

    /// <summary>固定工程并将完整 SVG 门禁传递给实际包编译，不允许项目入口绕过资源验证。</summary>
    public static async Task<CompiledProjectEffects> CompileAsync(Project project, IScriptCompilationService compiler,
        IPackageImageValidator? images, IPackageShaderValidator? shaders, IPackageImageGraphValidator? graphs,
        IPackageMaterialValidator? materials, IPackageSvgValidator? svgValidator, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        return await CompileAsync(ProjectSnapshot.FromProject(project), compiler, images, shaders, graphs, materials, svgValidator, cancellationToken).ConfigureAwait(false);
    }
    /// <summary>直接固定实际渲染快照及分析，不读取当前工作区或重建缺少字段的源工程。</summary>
    public static Task<CompiledProjectEffects> CompileAsync(ProjectSnapshot snapshot, IScriptCompilationService compiler,
        IPackageImageValidator? images = null, IPackageShaderValidator? shaders = null, IPackageImageGraphValidator? graphs = null,
        IPackageMaterialValidator? materials = null, CancellationToken cancellationToken = default)
        => CompileAsync(snapshot, compiler, images, shaders, graphs, materials, null, cancellationToken);

    /// <summary>直接固定渲染快照，并对启用包与源材质闭包执行完整 SVG 验证。</summary>
    public static async Task<CompiledProjectEffects> CompileAsync(ProjectSnapshot snapshot, IScriptCompilationService compiler,
        IPackageImageValidator? images, IPackageShaderValidator? shaders, IPackageImageGraphValidator? graphs,
        IPackageMaterialValidator? materials, IPackageSvgValidator? svgValidator, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var audio = ProjectAudioProjection.Create(snapshot);
        var compiled = await CompileCoreAsync(snapshot.Effects, compiler, images, shaders, graphs, materials, svgValidator,
            SourceMaterialCatalog.CollectForRuntime(snapshot.LyricStyle, snapshot.Lyrics, snapshot.LyricPresentation, snapshot.PresetApplications), snapshot, cancellationToken).ConfigureAwait(false);
        compiled._audio = audio; compiled._snapshot = snapshot; compiled._fingerprint = ProjectRuntimeEffectInputs.Fingerprint(snapshot); return compiled;
    }
    /// <summary>根据固定工程字节离线编译，正式资源门禁由组合根提供。</summary>
    public static Task<CompiledProjectEffects> CompileAsync(ProjectEffects source, IScriptCompilationService compiler,
        IPackageImageValidator? images = null, IPackageShaderValidator? shaders = null, IPackageImageGraphValidator? graphs = null,
        IPackageMaterialValidator? materials = null, CancellationToken cancellationToken = default)
        => CompileAsync(source, compiler, images, shaders, graphs, materials, null, cancellationToken);

    /// <summary>根据固定工程效果闭包编译，并为每个实际包提供 SVG 门禁。</summary>
    public static async Task<CompiledProjectEffects> CompileAsync(ProjectEffects source, IScriptCompilationService compiler,
        IPackageImageValidator? images, IPackageShaderValidator? shaders, IPackageImageGraphValidator? graphs,
        IPackageMaterialValidator? materials, IPackageSvgValidator? svgValidator, CancellationToken cancellationToken = default)
        => await CompileCoreAsync(source, compiler, images, shaders, graphs, materials, svgValidator, [], null, cancellationToken).ConfigureAwait(false);

    private static async Task<CompiledProjectEffects> CompileCoreAsync(ProjectEffects source, IScriptCompilationService compiler,
        IPackageImageValidator? images, IPackageShaderValidator? shaders, IPackageImageGraphValidator? graphs,
        IPackageMaterialValidator? materials, IPackageSvgValidator? svgValidator, ImmutableArray<SourceMaterialReference> references, ProjectSnapshot? snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(compiler);
        Dictionary<(string, string), CompiledEffectPackage> compiled = [];
        foreach (var key in source.Instances.Where(i => i.Enabled).Select(i => (i.PackageId, i.Revision))
            .Concat(references.Select(reference => (reference.Material.PackageId, reference.Material.Revision))).Distinct())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                compiled.Add(key, await CompiledEffectPackage.CompileAsync(EffectPackageDefinition.Read(source.GetPackage(key.PackageId, key.Revision), cancellationToken),
                    compiler, images, shaders, graphs, materials, svgValidator, cancellationToken).ConfigureAwait(false));
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                if (references.FirstOrDefault(reference => (reference.Material.PackageId, reference.Material.Revision) == key) is not { } reference) throw;
                throw SourceOwner(reference).Failure("CompileSourceMaterials", 0,
                    error is EffectPackageException package ? package.Code : "SourceMaterialCompilationFailed", error);
            }
        }
        if (compiled.Values.Any(p => !p.Succeeded)) return new(source, [.. compiled.Values], []);
        var effects = source.Instances.Where(i => i.Enabled).Select(i => Create(i)).ToImmutableArray();
        var result = new CompiledProjectEffects(source, [.. compiled.Values], effects);
        if (!references.IsEmpty)
        {
            List<KeyValuePair<SourceMaterialReference, MaterialValue.PackageMaterial>> values = [];
            Dictionary<MaterialSpec.PackageMaterial, MaterialValue.PackageMaterial> resolvedMaterials = [];
            foreach (var reference in references)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var package = compiled[(reference.Material.PackageId, reference.Material.Revision)];
                    SourceMaterialCatalog.Validate(reference, package.Definition);
                    var export = package.Definition.GetMaterial(reference.Material.ExportId);
                    if (export.Outputs.Any(output => !output.Requires.IsEmpty || !output.DependsOn.IsEmpty))
                        throw new EffectPackageException("PackageOutputPlanPending", reference.Path, "源材质跨输出执行计划尚未接通。");
                    if (package.Definition.ImageGraphs[export.GraphPath].Plan.Definition.AnchorMode != GraphAnchorMode.ColorOnly)
                        throw new EffectPackageException("PackageGraphMappingPending", reference.Path, "源材质实际映射尚未接通。");
                    if (!resolvedMaterials.TryGetValue(reference.Material, out var value))
                    {
                        value = (MaterialValue.PackageMaterial)MaterialValue.PackageExport(package.Resources.GetMaterial(reference.Material.ExportId),
                            package.ResolveParameters(reference.Material.ExportId, reference.Material.Parameters.Encode(), cancellationToken));
                        resolvedMaterials.Add(reference.Material, value);
                    }
                    values.Add(KeyValuePair.Create(reference, value));
                }
                catch (Exception error) when (error is not OperationCanceledException)
                { throw SourceOwner(reference).Failure("ResolveSourceMaterials", 0, error is EffectPackageException package ? package.Code : "SourceMaterialResolutionFailed", error); }
            }
            result.SourceMaterials = new(snapshot!, values);
        }
        return result;
        CompiledFrameEffect Create(ProjectEffectInstance instance)
        {
            var package = compiled[(instance.PackageId, instance.Revision)]; var export = package.Definition.GetExport(instance.ExportId);
            if (instance.OverlayLayer != EffectOverlayLayer.Package && !export.Outputs.Any(o => o.Kind == "Overlay"))
                throw new EffectPackageException("EffectOverlayLayerUnsupported", "effects.instances", "层覆盖要求真实Overlay输出。");
            return package.CreateEffect(instance.ExportId, new(instance.Id), ProjectEffectBindingProjection.ToBinding(instance.Binding), instance.Seed, instance.Parameters.Encode(), ToSubscription(instance.Subscription), cancellationToken)
                with { OverlayLayerOverride = instance.OverlayLayer switch { EffectOverlayLayer.Package => null, EffectOverlayLayer.BehindText => OverlayLayer.BehindText,
                    EffectOverlayLayer.AboveText => OverlayLayer.AboveText, _ => throw new ArgumentException("图元层无效。") } };
        }
    }
    private static PreparedMaterialOwner.Source SourceOwner(SourceMaterialReference reference) => new(reference, PreparedMaterialSlots.Of(reference));
    /// <summary>为固定闭包和分析创建独立重放会话，使用工程真实视觉／粒子预算。</summary>
    public TimelineFrameSourceFactory CreateFactory(Func<ILineGeometryProvider> geometryFactory, ReplayReason reason,
        MakoKaraGenerator.Rendering.Abstractions.ProjectMediaFeatureBinding? mediaBinding = null)
    {
        if (!Succeeded) throw new EffectPackageException("ProjectEffectsCompilationFailed", "effects.instances", "工程效果编译失败；不能使用旧 DLL 导出。");
        if (Effects.IsEmpty && SourceMaterials is null) throw new EffectPackageException("ProjectEffectsNotEnabled", "effects.instances", "没有启用实例或源材质，无需创建准备会话。");
        if (_snapshot is { } snapshot) ProjectMediaFeatureAdmission.Validate(snapshot, Effects, SourceMaterials, mediaBinding, default);
        else if (mediaBinding is not null) throw new EffectOutputException("MediaFeatureBindingMismatch", "独立效果源编译未固定完整媒体快照。");
        return new(Effects, _fingerprint ?? Source.InputsFingerprint!, geometryFactory, reason, audio: _audio, visualBudget: new(Source.Budgets.DrawCommands),
            particleBudget: new(Source.Budgets.LiveParticles, Source.Budgets.TrailSamples, Source.Budgets.EmissionRecords), sourceMaterials: SourceMaterials, mediaBinding: mediaBinding);
    }
    /// <summary>中间表面执行使用工程持久限额；其余图限制沿用已接受固定默认值。</summary>
    public GraphExecutionBudget GraphBudget => new() { MaximumBytes = Source.Budgets.SurfaceMemoryBytes };
    private static SourceSubscription ToSubscription(ProjectEffectSubscription value) => value.Kind switch
    { "project" => new SourceSubscription.Project(), "branch" => new SourceSubscription.Branch(new(value.BranchId!)), _ => new SourceSubscription.BoundSources() };
}
