using System.Collections.Immutable;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Core.Effects;

/// <summary>共享配置的用途；全局属于应用范围而不是配置种类。</summary>
public enum ScriptConfigurationKind
{
    /// <summary>源填充或描边材质。</summary>
    Material,
    /// <summary>控制器或直接图效果。</summary>
    Effect,
}

/// <summary>与具体工程身份无关的相对配方目标。</summary>
public enum ScriptRelativeTarget
{
    /// <summary>保留所选应用范围的单个实例。</summary>
    Application,
    /// <summary>应用范围中的各歌词行。</summary>
    Line,
    /// <summary>应用范围中的各元素族。</summary>
    TokenFamily,
}

/// <summary>相对事件读取范围。</summary>
public enum ScriptReadScope
{
    /// <summary>绑定来源。</summary>
    BoundSources,
    /// <summary>完整工程来源。</summary>
    Project,
    /// <summary>应用时映射的具名歌词分支输入。</summary>
    BranchInput,
}

/// <summary>独立于歌曲的固定源码／资源和参数配置；保存不执行作者。</summary>
public sealed record ScriptConfiguration
{
    private ScriptConfiguration(string id, string name, EffectPackageContents package, string exportId,
        EffectParameterOverrides parameters, ScriptRelativeTarget target, ScriptReadScope readScope,
        string? branchInput, ulong? fixedSeed, EffectOverlayLayer overlayLayer)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(package);
        ArgumentException.ThrowIfNullOrWhiteSpace(exportId);
        ArgumentNullException.ThrowIfNull(parameters);
        if (package.Profile != ProjectEffectsJson.PackageProfile)
            throw new ArgumentException("脚本配置包profile与当前宿主不符。", nameof(package));
        if (!Enum.IsDefined(target) || !Enum.IsDefined(readScope) || !Enum.IsDefined(overlayLayer))
            throw new ArgumentException("脚本配置的目标、读取范围或图元层无效。");
        if (readScope == ScriptReadScope.BranchInput ? string.IsNullOrWhiteSpace(branchInput) : branchInput is not null)
            throw new ArgumentException("分支订阅必须提供相对输入名称；其他范围不接受分支身份。", nameof(branchInput));
        var definition = EffectPackageDefinition.Read(package);
        var export = definition.GetExport(exportId);
        _ = definition.ResolveParameters(exportId, parameters.Encode());
        Kind = export is EffectMaterialDefinition ? ScriptConfigurationKind.Material : ScriptConfigurationKind.Effect;
        BindingKinds = export switch
        {
            EffectControllerDefinition controller => controller.BindingKinds,
            EffectGraphDefinition graph => graph.BindingKinds,
            EffectMaterialDefinition => [],
            _ => throw new ArgumentException("导出不支持作为材质或效果配置。", nameof(exportId)),
        };
        // 声明允许某个绑定并不放宽生产组写权；组输出只支持工程或显式节点。
        if (export.Outputs.Any(output => output.Target == "group"))
            BindingKinds = [.. BindingKinds.Where(kind => kind is "project" or "node")];
        if (Kind == ScriptConfigurationKind.Material && (target != ScriptRelativeTarget.Application
            || readScope != ScriptReadScope.BoundSources || fixedSeed is not null || overlayLayer != EffectOverlayLayer.Package))
            throw new ArgumentException("源材质不创建控制器实例，也不保存事件订阅、种子或图元层。");
        if (Kind == ScriptConfigurationKind.Effect)
        {
            bool supported = target switch
            {
                ScriptRelativeTarget.Line => BindingKinds.Any(kind => kind is "lines" or "project" or "branch" or "node"),
                ScriptRelativeTarget.TokenFamily => BindingKinds.Any(kind => kind is "tokens" or "project" or "branch" or "node"),
                _ => true,
            };
            if (!supported)
                throw new ArgumentException("导出不支持所选相对目标。", nameof(target));
            if (target != ScriptRelativeTarget.Application && export.Outputs.Any(output => output.Target == "group")
                || target == ScriptRelativeTarget.TokenFamily && export.Outputs.Any(output => output.Kind != "Overlay" && output.Target != "tokenFamily"))
                throw new ArgumentException("输出写入范围不支持所选相对目标。", nameof(target));
            if (overlayLayer != EffectOverlayLayer.Package && !export.Outputs.Any(output => output.Kind == "Overlay"))
                throw new ArgumentException("非图元效果不能覆盖图元层。", nameof(overlayLayer));
        }
        Id = id; Name = name.Trim(); Package = package; ExportId = exportId; Parameters = parameters;
        Target = target; ReadScope = readScope; BranchInput = branchInput; FixedSeed = fixedSeed; OverlayLayer = overlayLayer;
    }

    /// <summary>应用级稳定配置身份。</summary>
    public string Id { get; }
    /// <summary>显示名称。</summary>
    public string Name { get; }
    /// <summary>由实际导出推导的种类。</summary>
    public ScriptConfigurationKind Kind { get; }
    /// <summary>完整不可变源码与资源闭包。</summary>
    public EffectPackageContents Package { get; }
    /// <summary>固定导出身份。</summary>
    public string ExportId { get; }
    /// <summary>保留精度的参数覆盖。</summary>
    public EffectParameterOverrides Parameters { get; }
    /// <summary>相对目标，不保存具体歌词或场景ID。</summary>
    public ScriptRelativeTarget Target { get; }
    /// <summary>相对读取范围。</summary>
    public ScriptReadScope ReadScope { get; }
    /// <summary>分支读取输入名；实际分支在应用时映射。</summary>
    public string? BranchInput { get; }
    /// <summary>显式固定种子；空值表示新应用建立自己的种子。</summary>
    public ulong? FixedSeed { get; }
    /// <summary>图元层。</summary>
    public EffectOverlayLayer OverlayLayer { get; }
    /// <summary>导出实际允许的绑定种类；源材质为空。</summary>
    public ImmutableArray<string> BindingKinds { get; }

    /// <summary>验证结构、闭包和参数，允许保留尚未编译或编译失败的源码。</summary>
    public static ScriptConfiguration Create(string id, string name, EffectPackageContents package, string exportId,
        EffectParameterOverrides? parameters = null, ScriptRelativeTarget target = ScriptRelativeTarget.Application,
        ScriptReadScope readScope = ScriptReadScope.BoundSources, string? branchInput = null,
        ulong? fixedSeed = null, EffectOverlayLayer overlayLayer = EffectOverlayLayer.Package)
        => new(id, name, package, exportId, parameters ?? EffectParameterOverrides.Empty, target, readScope, branchInput, fixedSeed, overlayLayer);

    /// <summary>复制为模板的材质值，不读取配置库或编译缓存。</summary>
    public MaterialSpec.PackageMaterial ToMaterial() => Kind == ScriptConfigurationKind.Material
        ? new(Package.PackageId, Package.Revision, ExportId, Parameters)
        : throw new InvalidOperationException("效果配置不能作为源材质使用。");

    /// <summary>固定配置投影为真实实例；工程级绑定保持一个实例。</summary>
    public ProjectEffectInstance CreateInstance(string instanceId, ProjectEffectBinding binding, ulong applicationSeed,
        IReadOnlyDictionary<string, string>? branchInputs = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId); ArgumentNullException.ThrowIfNull(binding);
        if (Kind != ScriptConfigurationKind.Effect || !BindingKinds.Contains(ProjectEffectsJson.KindName(binding.Kind), StringComparer.Ordinal)
            || Target == ScriptRelativeTarget.Line && binding.Kind is not (ProjectEffectBindingKind.Lines or ProjectEffectBindingKind.Project or ProjectEffectBindingKind.Branch or ProjectEffectBindingKind.Node)
            || Target == ScriptRelativeTarget.TokenFamily && binding.Kind is not (ProjectEffectBindingKind.Tokens or ProjectEffectBindingKind.Project or ProjectEffectBindingKind.Branch or ProjectEffectBindingKind.Node))
            throw new ArgumentException("配置与应用绑定不匹配。", nameof(binding));
        var subscription = ReadScope switch
        {
            ScriptReadScope.BoundSources => new ProjectEffectSubscription(),
            ScriptReadScope.Project => new ProjectEffectSubscription("project"),
            ScriptReadScope.BranchInput when branchInputs is not null && branchInputs.TryGetValue(BranchInput!, out string? branchId)
                => new ProjectEffectSubscription("branch", branchId),
            _ => throw new ArgumentException("配置所需的分支输入尚未映射。", nameof(branchInputs)),
        };
        return new() { Id = instanceId, Name = Name, PackageId = Package.PackageId, Revision = Package.Revision,
            ExportId = ExportId, Binding = binding, Subscription = subscription, Seed = FixedSeed ?? applicationSeed,
            Parameters = Parameters, OverlayLayer = OverlayLayer };
    }

    /// <summary>按固定内容比较，重读配置不会因包对象或数组引用不同制造脏草稿。</summary>
    public bool Equals(ScriptConfiguration? other) => other is not null && Id == other.Id && Name == other.Name
        && Package.PackageId == other.Package.PackageId && Package.Revision == other.Package.Revision
        && ExportId == other.ExportId && Parameters.Equals(other.Parameters) && Target == other.Target
        && ReadScope == other.ReadScope && BranchInput == other.BranchInput && FixedSeed == other.FixedSeed
        && OverlayLayer == other.OverlayLayer;

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        HashCode hash = new(); hash.Add(Id); hash.Add(Name); hash.Add(Package.PackageId); hash.Add(Package.Revision);
        hash.Add(ExportId); hash.Add(Parameters); hash.Add(Target); hash.Add(ReadScope); hash.Add(BranchInput);
        hash.Add(FixedSeed); hash.Add(OverlayLayer); return hash.ToHashCode();
    }
}
