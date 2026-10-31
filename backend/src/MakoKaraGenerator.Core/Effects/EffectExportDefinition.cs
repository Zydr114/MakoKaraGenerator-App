using System.Collections.Immutable;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Core.Effects;

/// <summary>控制器、材质和图共用的固定导出元数据；仅包读取器能建立实例。</summary>
public abstract class EffectExportDefinition
{
    private protected EffectExportDefinition(string id, string name, ImmutableArray<string> parameters,
        ImmutableArray<EffectOutputDefinition> outputs, ImmutableArray<EffectOutputDefinition> ordered)
    { Id = id; Name = name; ParameterIds = parameters; Outputs = outputs; TopologicalOutputs = ordered; }
    /// <summary>在包内跨种类唯一的导出身份。</summary>
    public string Id { get; }
    /// <summary>用户可读名称。</summary>
    public string Name { get; }
    /// <summary>本导出引用的唯一参数，保持清单顺序。</summary>
    public ImmutableArray<string> ParameterIds { get; }
    /// <summary>清单原顺序的具名输出。</summary>
    public ImmutableArray<EffectOutputDefinition> Outputs { get; }
    /// <summary>稳定拓扑顺序；同层沿用清单顺序。</summary>
    public ImmutableArray<EffectOutputDefinition> TopologicalOutputs { get; }
}

/// <summary>明确的透明输出替代，当前映射仅允许 identity。</summary>
public abstract record EffectOutputBypass
{
    private EffectOutputBypass() { }
    /// <summary>使用该材质导出的已验证基础外观。</summary>
    public sealed record MaterialFallback : EffectOutputBypass;
    /// <summary>使用同一图显式指定的原输入。</summary><param name="InputId">实际输入身份，不含 input: 前缀。</param>
    public sealed record GraphInput(string InputId) : EffectOutputBypass;
}

/// <summary>材质的唯一基础回退；不能引用其他包材质。</summary>
public abstract record EffectMaterialFallback
{
    private EffectMaterialFallback() { }
    /// <summary>规范八位纯色。</summary><param name="Color">完整 RGBA。</param>
    public sealed record Solid(RgbaColor Color) : EffectMaterialFallback;
    /// <summary>完整规范渐变；支持情况由正式后端门禁检查。</summary><param name="Value">所有字段显式保存的渐变。</param>
    public sealed record Gradient(GradientSpec Value) : EffectMaterialFallback;
}

/// <summary>材质输出的显式字形覆盖协议。</summary>
public enum EffectMaterialCoverageMode
{
    /// <summary>图输出尚未乘区域覆盖，由宿主对颜色和替换覆盖各应用一次。</summary>
    Unmasked,
    /// <summary>图输出已经覆盖；宿主验证区域约束，不再次乘覆盖。</summary>
    Masked,
}

/// <summary>独立材质导出，不制造源行或控制器对象。</summary>
public sealed class EffectMaterialDefinition : EffectExportDefinition
{
    internal EffectMaterialDefinition(string id, string name, string graphPath, ImmutableArray<string> parameters,
        ImmutableArray<string> regions, EffectMaterialFallback fallback, EffectMaterialCoverageMode coverageMode, ImmutableArray<EffectOutputDefinition> outputs,
        ImmutableArray<EffectOutputDefinition> ordered) : base(id, name, parameters, outputs, ordered)
    { GraphPath = graphPath; Regions = regions; Fallback = fallback; CoverageMode = coverageMode; }
    /// <summary>同闭包 role:graph 路径。</summary>
    public string GraphPath { get; }
    /// <summary>允许的 fill／stroke 区域。</summary>
    public ImmutableArray<string> Regions { get; }
    /// <summary>透明输出跳过背景依赖材质时的唯一基础外观。</summary>
    public EffectMaterialFallback Fallback { get; }
    /// <summary>明确的区域覆盖协议，不能由图是否读取 TargetColor 推测。</summary>
    public EffectMaterialCoverageMode CoverageMode { get; }
}

/// <summary>独立图导出，图文件与导出身份分开保存。</summary>
public sealed class EffectGraphDefinition : EffectExportDefinition
{
    internal EffectGraphDefinition(string id, string name, string graphPath, ImmutableArray<string> parameters,
        ImmutableArray<string> bindings, ImmutableArray<EffectOutputDefinition> outputs, ImmutableArray<EffectOutputDefinition> ordered)
        : base(id, name, parameters, outputs, ordered) { GraphPath = graphPath; BindingKinds = bindings; }
    /// <summary>同闭包 role:graph 路径。</summary>
    public string GraphPath { get; }
    /// <summary>允许的六类绑定。</summary>
    public ImmutableArray<string> BindingKinds { get; }
}
