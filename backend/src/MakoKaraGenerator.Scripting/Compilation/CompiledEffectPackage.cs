using System.Collections.Immutable;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Scripting.Execution;
using MakoKaraGenerator.Scripting.Loading;

namespace MakoKaraGenerator.Scripting.Compilation;

/// <summary>全部包资源和导出的正式门禁及固定 SDK 投影；工程持久化／主画布协调器另行接入。</summary>
public sealed partial class CompiledEffectPackage
{
    private readonly ScriptCompilationResult? _artifact;
    private CompiledEffectPackage(EffectPackageDefinition definition, ScriptCompilationResult? artifact, ResourceCatalog resources)
    { Definition = definition; _artifact = artifact; Resources = resources; }
    /// <summary>已经解析的唯一参数／导出元数据。</summary>
    public EffectPackageDefinition Definition { get; }
    /// <summary>实际存在的 C# 编译结果；纯材质／图包读取此属性明确失败，不制造空 DLL。</summary>
    public ScriptCompilationResult Artifact => _artifact ?? throw new EffectPackageException("PackageCSharpArtifactUnavailable", "files", "此包没有 C# 源码或 DLL。");
    /// <summary>实际 C# 结果；没有 C# 文件时为 null。</summary>
    public ScriptCompilationResult? ControllerArtifact => _artifact;
    /// <summary>全部资源门禁和实际 C# 编译是否成功；没有 C# 不等于失败。</summary>
    public bool Succeeded => _artifact?.Succeeded ?? true;
    /// <summary>完整验证后的材质／图导出及图片；源码失败时为空，不留下可启用部分包。</summary>
    public ResourceCatalog Resources { get; }

    /// <summary>完整验证图片、离线编译并反射全部入口；反射不运行作者构造或初始化。</summary>
    public static Task<CompiledEffectPackage> CompileAsync(EffectPackageDefinition definition, IScriptCompilationService compiler,
        IPackageImageValidator? imageValidator = null, CancellationToken cancellationToken = default)
        => CompileAsync(definition, compiler, imageValidator, null, cancellationToken);

    /// <summary>由实际后端检查所有图片／shader，再编译全部控制器；缺少所需验证器拒绝启用。</summary>
    public static Task<CompiledEffectPackage> CompileAsync(EffectPackageDefinition definition, IScriptCompilationService compiler,
        IPackageImageValidator? imageValidator, IPackageShaderValidator? shaderValidator, CancellationToken cancellationToken = default)
        => CompileAsync(definition, compiler, imageValidator, shaderValidator, null, cancellationToken);

    /// <summary>由正式后端验证全部图片、程序及图，再编译控制器；不会给未启用导出伪造句柄。</summary>
    public static Task<CompiledEffectPackage> CompileAsync(EffectPackageDefinition definition, IScriptCompilationService compiler,
        IPackageImageValidator? imageValidator, IPackageShaderValidator? shaderValidator, IPackageImageGraphValidator? graphValidator,
        CancellationToken cancellationToken = default)
        => CompileAsync(definition, compiler, imageValidator, shaderValidator, graphValidator, null, cancellationToken);

    /// <summary>验证全部程序、图、图片、材质回退和实际源码；纯资源包不需要假控制器。</summary>
    public static async Task<CompiledEffectPackage> CompileAsync(EffectPackageDefinition definition, IScriptCompilationService compiler,
        IPackageImageValidator? imageValidator, IPackageShaderValidator? shaderValidator, IPackageImageGraphValidator? graphValidator,
        IPackageMaterialValidator? materialValidator, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition); ArgumentNullException.ThrowIfNull(compiler);
        cancellationToken.ThrowIfCancellationRequested();
        if (!definition.ImagePaths.IsEmpty && imageValidator is null)
            throw new EffectPackageException("PackageImageValidatorMissing", "files", "图片须由实际后端完整解码，不能只按文件声明建立句柄。");
        if (!definition.ShaderPrograms.IsEmpty && shaderValidator is null)
            throw new EffectPackageException("PackageShaderValidatorMissing", "files", "包内全部 shader 须实际编译和反射，不能只接受声明或部分启用控制器。");
        if (!definition.ImageGraphs.IsEmpty && graphValidator is null)
            throw new EffectPackageException("PackageImageGraphValidatorMissing", "files", "包内全部图须由正式后端验证，不能忽略图资源而部分启用。");
        if (!definition.Materials.IsEmpty && materialValidator is null)
            throw new EffectPackageException("PackageMaterialValidatorMissing", "exports", "全部材质回退须由生产后端检查，不能只运行控制器部分。");
        await Task.Run(() =>
        {
            if (!definition.ShaderPrograms.IsEmpty) shaderValidator!.Validate(definition.ShaderPrograms, cancellationToken);
            foreach (var path in definition.ImagePaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                imageValidator!.Validate(definition.Contents.Files.Single(file => file.Path == path), cancellationToken);
            }
            if (!definition.ImageGraphs.IsEmpty) graphValidator!.Validate(definition.ImageGraphs, cancellationToken);
            if (!definition.Materials.IsEmpty) materialValidator!.Validate(definition.Materials, cancellationToken);
        }, cancellationToken).ConfigureAwait(false);
        ScriptCompilationResult? artifact = null;
        if (!definition.Controllers.IsEmpty || definition.Contents.Files.Any(file => file.Role == "csharp"))
            artifact = await compiler.CompileAsync(EffectPackageSources.Read(definition.Contents),
                definition.Controllers.Select(controller => controller.EntryType), cancellationToken).ConfigureAwait(false);
        if (artifact is { Succeeded: true })
        {
            using ScriptModule module = new(artifact.PeImage.AsMemory(), artifact.PdbImage.AsMemory());
            foreach (var controller in definition.Controllers)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (EffectPackageProfiles.IsV2(definition.Contents.Profile))
                        module.ValidateEntry<V2.IPureKaraokeEffect>(controller.EntryType);
                    else module.ValidateEntry<IKaraokeEffect>(controller.EntryType);
                }
                catch (Exception error) when (error is not OperationCanceledException)
                { throw new EffectPackageException("PackageEntryInvalid", "exports." + controller.Id + ".entryType", "真实 DLL 入口缺失或不符合公共 SDK 构造合同。", error); }
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(definition, artifact, artifact is { Succeeded: false } ? ResourceCatalog.Empty
            : EffectPackageResourceCatalog.Create(definition));
    }

    /// <summary>按导出建立生产帧描述；只接受当前真实 writer，未实施能力和跨输出旁路明确失败。</summary>
    public CompiledFrameEffect CreateEffect(string exportId, EffectInstanceId instanceId, EffectBinding binding, ulong seed,
        ReadOnlyMemory<byte> parameterOverrides = default, SourceSubscription? subscription = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(binding); cancellationToken.ThrowIfCancellationRequested();
        EnsureSucceeded();
        if (Definition.GetExport(exportId) is EffectGraphDefinition graph)
        {
            if (EffectPackageProfiles.IsV2(Definition.Contents.Profile))
                throw new EffectPackageException("PackageWriterPending", "exports." + exportId, "SDK v2 直接图输出尚未接通。");
            DirectGraphEffectValidation.Validate(Definition, graph, binding);
            var graphParameters = ResolveParameters(exportId, parameterOverrides, cancellationToken);
            return new(instanceId, null, null, graphParameters, binding, EffectCapabilities.ImageEffects)
            { Resources = Resources, Seed = seed, Subscription = subscription ?? new SourceSubscription.BoundSources(),
                PackageDeclaration = graph, PackageDefinition = Definition };
        }
        var controller = Definition.GetController(exportId);
        string bindingKind = binding switch
        {
            EffectBinding.Project => "project", EffectBinding.Branch => "branch", EffectBinding.Lines => "lines",
            EffectBinding.Tokens => "tokens", EffectBinding.Clips => "clips", EffectBinding.Node => "node", _ => throw new ArgumentException("未知绑定。", nameof(binding)),
        };
        if (!controller.BindingKinds.Contains(bindingKind, StringComparer.Ordinal))
            throw new EffectPackageException("PackageBindingNotAllowed", "exports." + exportId + ".bindingKinds", "导出未允许此绑定类别。");
        EffectCapabilities capabilities = EffectCapabilities.None;
        bool requiresAudio = false;
        foreach (var output in controller.Outputs)
        {
            string path = "exports." + exportId + ".outputs." + output.Id;
            if (EffectPackageProfiles.IsV2(Definition.Contents.Profile))
            {
                if (output.Kind is not ("VisualTransform" or "StyleModulation" or "Overlay"))
                    throw new EffectPackageException("PackageWriterPending", path, "SDK v2 尚未接通此输出阶段：" + output.Kind);
                // v2 的有限能力已由完整包解析器与包级 requires 共同校验。
            }
            else
            {
                if (output.Requires.Any(requirement => requirement != "AudioFeatures")) throw new EffectPackageException("PackageRequirementUnsupported", path + ".requires", "当前控制器适配尚不能解析这些能力需求：" + string.Join(",", output.Requires));
                requiresAudio |= output.Requires.Contains("AudioFeatures", StringComparer.Ordinal);
            }
            if (!output.DependsOn.IsEmpty) throw new EffectPackageException("PackageOutputPlanPending", path + ".dependsOn", "依赖已校验，但跨输出执行／旁路计划尚未接入。");
            if (output.Bypass is not null && output.Kind != "ImageGraph")
                throw new EffectPackageException("PackageOutputPlanPending", path + ".bypass", "此旁路尚不能通过生产输出计划执行。");
            capabilities |= (output.Kind, output.Target) switch
            {
                ("VisualTransform", "tokenFamily" or "line" or "group" or "textMark") => EffectCapabilities.Transforms,
                ("VisualTransform", "baseToken" or "cluster") when EffectPackageProfiles.IsV2(Definition.Contents.Profile) => EffectCapabilities.Transforms,
                ("StyleModulation", "tokenFamily" or "textMark" or "group") => EffectCapabilities.Style,
                ("StyleModulation", "baseToken") when EffectPackageProfiles.IsV2(Definition.Contents.Profile) => EffectCapabilities.Style,
                ("Overlay", "tokenFamily" or "line" or "textMark") => EffectCapabilities.Overlay,
                ("Overlay", "canvas") when EffectPackageProfiles.IsV2(Definition.Contents.Profile) => EffectCapabilities.Overlay,
                ("ImageGraph", "tokenFamily" or "line" or "group" or "textMark") => EffectCapabilities.ImageEffects,
                _ => throw new EffectPackageException("PackageWriterPending", path, "当前生产运行时尚未提供此输出和目标组合。"),
            };
        }
        var parameters = ResolveParameters(exportId, parameterOverrides, cancellationToken);
        return new(instanceId, Artifact, controller.EntryType, parameters, binding, capabilities)
        { Resources = Resources, Seed = seed, Subscription = subscription ?? new SourceSubscription.BoundSources(), RequiresAudio = requiresAudio, PackageDeclaration = controller, PackageDefinition = Definition };
    }
    /// <summary>解析任一种导出的完整参数，保留资源修订和九类真实值。</summary>
    public ParameterValues ResolveParameters(string exportId, ReadOnlyMemory<byte> overrides = default, CancellationToken cancellationToken = default)
    {
        EnsureSucceeded();
        var resolved = Definition.ResolveParameters(exportId, overrides, cancellationToken);
        ParameterValues values = new(resolved.Select(pair => KeyValuePair.Create(pair.Key, ToSdk(pair.Value))));
        values.ValidateResources(Resources); return values;
    }
    private void EnsureSucceeded()
    { if (!Succeeded) throw new EffectPackageException("PackageCompilationFailed", "files", "当前包源码未成功编译，不能启用部分资源或旧 DLL。"); }
    private ParameterValue ToSdk(EffectParameterValue value) => value switch
    {
        EffectParameterValue.Number number => new ParameterValue.Number(number.Value),
        EffectParameterValue.Whole integer => new ParameterValue.Whole(integer.Value),
        EffectParameterValue.Toggle boolean => new ParameterValue.Toggle(boolean.Value),
        EffectParameterValue.Duration duration => new ParameterValue.Duration(duration.Value),
        EffectParameterValue.Choice choice => new ParameterValue.Choice(choice.Value),
        EffectParameterValue.Text text => new ParameterValue.Text(text.Value),
        EffectParameterValue.Color color => new ParameterValue.Color(new(color.R, color.G, color.B, color.A)),
        EffectParameterValue.Asset asset => new ParameterValue.Asset(Resources.GetImage(asset.Path)),
        EffectParameterValue.Curve curve => new ParameterValue.Curve(curve.Value.Kind switch
        {
            "easing" => new CurveValue.Easing(curve.Value.Easing switch
            { "linear" => EasingKind.Linear, "easeOut" => EasingKind.EaseOut, "easeInOut" => EasingKind.EaseInOut, _ => throw new InvalidOperationException() }),
            "cubicBezier" => new CurveValue.CubicBezier(new(curve.Value.Points[0].X, curve.Value.Points[0].Y), new(curve.Value.Points[1].X, curve.Value.Points[1].Y)),
            "controlPoints" => new CurveValue.ControlPoints([.. curve.Value.Points.Select(point => new Point2(point.X, point.Y))]),
            _ => throw new InvalidOperationException(),
        }),
        _ => throw new InvalidOperationException("未知参数投影。"),
    };
}
