using MakoKaraGenerator.Core.Effects;

namespace MakoKaraGenerator.Core.Projects;

/// <summary>配方的相对输出目标，不含工程ID。</summary>
public enum TemplateEffectTarget
{
    /// <summary>套用的歌词行。</summary>
    Line,
    /// <summary>套用元素及其注音；行模板遍历本行元素。</summary>
    TokenFamily,
    /// <summary>直接使用应用对象的范围，工程和分支保留单个实例。</summary>
    Application,
}

/// <summary>可分享的固定效果配方，不含实例或运行状态。</summary>
public sealed record TemplateEffectRecipe
{
    /// <summary>建立明确配方，参数仍由唯一包语义解析器验证。</summary>
    public TemplateEffectRecipe(string recipeId, string name, TemplateEffectTarget target, string packageId, string revision, string exportId,
        EffectParameterOverrides? parameters = null, EffectOverlayLayer overlayLayer = EffectOverlayLayer.Package,
        bool enabled = true, ScriptReadScope readScope = ScriptReadScope.BoundSources, string? branchInput = null,
        ulong? fixedSeed = null, string? configurationId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recipeId); ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId); ArgumentException.ThrowIfNullOrWhiteSpace(exportId); _ = ProjectEffectsJson.PackagePath(revision);
        if (!Enum.IsDefined(target)) throw new ArgumentOutOfRangeException(nameof(target));
        if (!Enum.IsDefined(overlayLayer)) throw new ArgumentOutOfRangeException(nameof(overlayLayer));
        if (!Enum.IsDefined(readScope) || (readScope == ScriptReadScope.BranchInput ? string.IsNullOrWhiteSpace(branchInput) : branchInput is not null))
            throw new ArgumentException("配方订阅必须使用有效相对范围和具名分支输入。");
        if (configurationId is not null && string.IsNullOrWhiteSpace(configurationId)) throw new ArgumentException("配置来源身份无效。", nameof(configurationId));
        RecipeId = recipeId; Name = name; Target = target; PackageId = packageId; Revision = revision; ExportId = exportId;
        Parameters = parameters ?? EffectParameterOverrides.Empty; OverlayLayer = overlayLayer;
        Enabled = enabled; ReadScope = readScope; BranchInput = branchInput; FixedSeed = fixedSeed; ConfigurationId = configurationId;
    }
    /// <summary>模板内稳定身份。</summary>
    public string RecipeId { get; }
    /// <summary>用户名称。</summary>
    public string Name { get; }
    /// <summary>相对目标。</summary>
    public TemplateEffectTarget Target { get; }
    /// <summary>包身份。</summary>
    public string PackageId { get; }
    /// <summary>固定资源修订。</summary>
    public string Revision { get; }
    /// <summary>包导出。</summary>
    public string ExportId { get; }
    /// <summary>完整精度的规范覆盖参数。</summary>
    public EffectParameterOverrides Parameters { get; }
    /// <summary>临时图元层覆盖。</summary>
    public EffectOverlayLayer OverlayLayer { get; }
    /// <summary>配方默认是否执行。</summary>
    public bool Enabled { get; }
    /// <summary>独立于写入范围的事件读取范围。</summary>
    public ScriptReadScope ReadScope { get; }
    /// <summary>相对分支输入名；不保存歌曲的分支身份。</summary>
    public string? BranchInput { get; }
    /// <summary>固定作者种子；为空时由应用建立种子。</summary>
    public ulong? FixedSeed { get; }
    /// <summary>共享配置来源，仅供明确更新定位；运行不读取配置库。</summary>
    public string? ConfigurationId { get; }

    /// <summary>按值复制配置元数据；包字节由模板自己的闭包保留。</summary>
    public static TemplateEffectRecipe FromConfiguration(string recipeId, ScriptConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (configuration.Kind != ScriptConfigurationKind.Effect) throw new ArgumentException("材质通过外观槽位应用。", nameof(configuration));
        return new(recipeId, configuration.Name, configuration.Target switch
        { ScriptRelativeTarget.Application => TemplateEffectTarget.Application, ScriptRelativeTarget.Line => TemplateEffectTarget.Line,
            ScriptRelativeTarget.TokenFamily => TemplateEffectTarget.TokenFamily, _ => throw new ArgumentOutOfRangeException(nameof(configuration)) },
            configuration.Package.PackageId, configuration.Package.Revision, configuration.ExportId, configuration.Parameters,
            configuration.OverlayLayer, readScope: configuration.ReadScope, branchInput: configuration.BranchInput,
            fixedSeed: configuration.FixedSeed, configurationId: configuration.Id);
    }

    /// <summary>验证并建立歌曲无关的固定配置，供应用投影使用。</summary>
    public ScriptConfiguration ToConfiguration(EffectPackageContents package)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (package.PackageId != PackageId || package.Revision != Revision) throw new ArgumentException("配方固定包身份不匹配。", nameof(package));
        return ScriptConfiguration.Create(ConfigurationId ?? RecipeId, Name, package, ExportId, Parameters, Target switch
        { TemplateEffectTarget.Application => ScriptRelativeTarget.Application, TemplateEffectTarget.Line => ScriptRelativeTarget.Line,
            TemplateEffectTarget.TokenFamily => ScriptRelativeTarget.TokenFamily, _ => throw new InvalidOperationException("配方相对目标无效。") },
            ReadScope, BranchInput, FixedSeed, OverlayLayer);
    }
}
