using System.Collections.Immutable;
using static MakoKaraGenerator.Core.Effects.EffectPackageJson;

namespace MakoKaraGenerator.Core.Effects;

/// <summary>包清单的完整参数值；不引用 SDK、native 资源或机器路径。</summary>
public abstract record EffectParameterValue
{
    private EffectParameterValue() { }
    /// <summary>有限浮点值。</summary><param name="Value">实际数值。</param>
    public sealed record Number(double Value) : EffectParameterValue;
    /// <summary>完整整数。</summary><param name="Value">实际整数。</param>
    public sealed record Whole(long Value) : EffectParameterValue;
    /// <summary>布尔值。</summary><param name="Value">实际值。</param>
    public sealed record Toggle(bool Value) : EffectParameterValue;
    /// <summary>枚举身份。</summary><param name="Value">已声明选项。</param>
    public sealed record Choice(string Value) : EffectParameterValue;
    /// <summary>非负整数微秒。</summary><param name="Value">时长。</param>
    public sealed record Duration(long Value) : EffectParameterValue;
    /// <summary>普通文本。</summary><param name="Value">文本，可为空串。</param>
    public sealed record Text(string Value) : EffectParameterValue;
    /// <summary>直乘 sRGB 分量，不量化为八位颜色。</summary><param name="R">红。</param><param name="G">绿。</param><param name="B">蓝。</param><param name="A">透明度。</param>
    public sealed record Color(double R, double G, double B, double A) : EffectParameterValue;
    /// <summary>封闭进度曲线。</summary><param name="Value">已验证的固定定义。</param>
    public sealed record Curve(EffectCurveDefinition Value) : EffectParameterValue;
    /// <summary>本包内明确声明的图片；实际解码由后端验证。</summary><param name="Path">相对逻辑路径。</param>
    public sealed record Asset(string Path) : EffectParameterValue;
}

/// <summary>封闭曲线描述的一个点。</summary><param name="X">进度。</param><param name="Y">输出，允许有限过冲。</param>
public readonly record struct EffectCurvePoint(double X, double Y);

/// <summary>包内已校验的曲线编码；不执行表达式或持有用户函数。</summary>
public sealed class EffectCurveDefinition
{
    internal EffectCurveDefinition(string kind, string? easing, ImmutableArray<EffectCurvePoint> points)
    { Kind = kind; Easing = easing; Points = points; }
    /// <summary>easing／cubicBezier／controlPoints。</summary>
    public string Kind { get; }
    /// <summary>缓动身份，其他类型为 null。</summary>
    public string? Easing { get; }
    /// <summary>贝塞尔两个控制点或完整折线点；缓动为空。</summary>
    public ImmutableArray<EffectCurvePoint> Points { get; }
}

/// <summary>选项身份及显示标签。</summary><param name="Value">唯一身份。</param><param name="Label">用户可读标签。</param>
public sealed record EffectParameterOption(string Value, string Label);

/// <summary>参数唯一元数据；图约束与运行默认值均来自本定义。</summary>
public sealed class EffectParameterDefinition
{
    internal EffectParameterDefinition(string type, string label, string? unit, EffectParameterConstraint constraint,
        EffectParameterValue defaultValue, ImmutableArray<EffectParameterOption> options)
    { Type = type; Label = label; Unit = unit; Constraint = constraint; DefaultValue = defaultValue; Options = options; }
    /// <summary>清单参数身份。</summary>
    public string Id => Constraint.Id;
    /// <summary>九类清单类型名。</summary>
    public string Type { get; }
    /// <summary>显示标签。</summary>
    public string Label { get; }
    /// <summary>可选单位说明，不改变值的类型。</summary>
    public string? Unit { get; }
    /// <summary>图编译器沿用的唯一约束。</summary>
    public EffectParameterConstraint Constraint { get; }
    /// <summary>已检查类型和范围的默认值。</summary>
    public EffectParameterValue DefaultValue { get; }
    /// <summary>按清单顺序的枚举选项，其他类型为空。</summary>
    public ImmutableArray<EffectParameterOption> Options { get; }
}

/// <summary>控制器的具名输出及同导出依赖；声明不自动授予尚未实现的写权限。</summary>
public sealed class EffectOutputDefinition
{
    internal EffectOutputDefinition(string id, string kind, string target, ImmutableArray<string> requires, ImmutableArray<string> dependsOn,
        EffectOutputBypass? bypass = null)
    { Id = id; Kind = kind; Target = target; Requires = requires; DependsOn = dependsOn; Bypass = bypass; }
    /// <summary>本导出内唯一身份。</summary>
    public string Id { get; }
    /// <summary>R0 声明的输出种类。</summary>
    public string Kind { get; }
    /// <summary>真实目标类别。</summary>
    public string Target { get; }
    /// <summary>由宿主明确支持的能力需求。</summary>
    public ImmutableArray<string> Requires { get; }
    /// <summary>本导出内依赖身份；可以前向引用。</summary>
    public ImmutableArray<string> DependsOn { get; }
    /// <summary>显式透明替代协议；null 表示没有声明旁路，不补默认值。</summary>
    public EffectOutputBypass? Bypass { get; }
}

/// <summary>固定控制器导出；不持有实例、运行状态或编译器。</summary>
public sealed class EffectControllerDefinition : EffectExportDefinition
{
    internal EffectControllerDefinition(string id, string name, string entry, ImmutableArray<string> parameters,
        ImmutableArray<string> bindings, ImmutableArray<EffectOutputDefinition> outputs, ImmutableArray<EffectOutputDefinition> ordered)
        : base(id, name, parameters, outputs, ordered)
    { EntryType = entry; BindingKinds = bindings; }
    /// <summary>完整作者类型名；实际类型检查属于 DLL 加载层。</summary>
    public string EntryType { get; }
    /// <summary>允许的六类源／场景绑定。</summary>
    public ImmutableArray<string> BindingKinds { get; }
}

/// <summary>控制器、材质和图的唯一完整包语义；任何导出或资源错误都拒绝整个包。</summary>
public sealed partial class EffectPackageDefinition
{
    private readonly ImmutableDictionary<string, EffectParameterDefinition> _parameters;
    private readonly ImmutableDictionary<string, EffectExportDefinition> _exports;
    private readonly ImmutableDictionary<string, EffectPackageFile> _files;
    private EffectPackageDefinition(EffectPackageContents contents, ImmutableArray<EffectParameterDefinition> parameters,
        ImmutableArray<EffectExportDefinition> exports, ImmutableDictionary<string, ShaderProgramDefinition> shaderPrograms,
        ImmutableDictionary<string, EffectPackageImageGraph> imageGraphs)
    {
        Contents = contents; Parameters = parameters; Exports = exports; ShaderPrograms = shaderPrograms; ImageGraphs = imageGraphs;
        Controllers = [.. exports.OfType<EffectControllerDefinition>()]; Materials = [.. exports.OfType<EffectMaterialDefinition>()];
        Graphs = [.. exports.OfType<EffectGraphDefinition>()];
        _parameters = parameters.ToImmutableDictionary(parameter => parameter.Id, StringComparer.Ordinal);
        _exports = exports.ToImmutableDictionary(export => export.Id, StringComparer.Ordinal);
        _files = contents.Files.ToImmutableDictionary(file => file.Path, StringComparer.Ordinal);
        ImagePaths = [.. contents.Files.Where(file => file.Role == "image").Select(file => file.Path)];
    }
    /// <summary>原固定闭包；没有另一份机器路径。</summary>
    public EffectPackageContents Contents { get; }
    /// <summary>按清单顺序的九类参数定义。</summary>
    public ImmutableArray<EffectParameterDefinition> Parameters { get; }
    /// <summary>跨种类唯一且按清单顺序的全部导出。</summary>
    public ImmutableArray<EffectExportDefinition> Exports { get; }
    /// <summary>全部控制器导出。</summary>
    public ImmutableArray<EffectControllerDefinition> Controllers { get; }
    /// <summary>全部材质导出；每项都有明确的纯色／渐变回退。</summary>
    public ImmutableArray<EffectMaterialDefinition> Materials { get; }
    /// <summary>全部图导出；不是未导出的资源文件路径。</summary>
    public ImmutableArray<EffectGraphDefinition> Graphs { get; }
    /// <summary>须实际解码后才能公开到 SDK 的图片。</summary>
    public ImmutableArray<string> ImagePaths { get; }
    /// <summary>按独立程序文件路径索引的全部固定 shader 声明；启用前须由正式后端编译反射。</summary>
    public ImmutableDictionary<string, ShaderProgramDefinition> ShaderPrograms { get; }
    /// <summary>按图文件路径索引的全部固定计划及逐输入捕获；正式启用前还须 CPU 编译验证。</summary>
    public ImmutableDictionary<string, EffectPackageImageGraph> ImageGraphs { get; }
    /// <summary>读取真实控制器导出，缺失不选第一个代替。</summary>
    public EffectControllerDefinition GetController(string exportId)
        => Get<EffectControllerDefinition>(exportId);
    /// <summary>读取完整材质导出，不将同名图或控制器当作材质。</summary>
    public EffectMaterialDefinition GetMaterial(string exportId) => Get<EffectMaterialDefinition>(exportId);
    /// <summary>读取完整图导出，不把资源文件或另一种导出当作图。</summary>
    public EffectGraphDefinition GetGraph(string exportId) => Get<EffectGraphDefinition>(exportId);
    /// <summary>读取任一种真实导出；缺失明确报错。</summary>
    public EffectExportDefinition GetExport(string exportId)
        => _exports.TryGetValue(exportId, out var value) ? value : throw Error("PackageExportMissing", "exports." + exportId, "导出不存在。");
    private T Get<T>(string id) where T : EffectExportDefinition => GetExport(id) as T
        ?? throw Error("PackageExportKindMismatch", "exports." + id + ".kind", "导出种类与请求不一致。");

    /// <summary>由清单默认值和明确的 JSON 参数覆盖建立完整值；不补未知键、不转换类型。</summary>
    public ImmutableDictionary<string, EffectParameterValue> ResolveParameters(string exportId, ReadOnlyMemory<byte> overrides = default,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); var export = GetExport(exportId);
        var values = export.ParameterIds.ToImmutableDictionary(id => id, id => _parameters[id].DefaultValue, StringComparer.Ordinal).ToBuilder();
        if (overrides.IsEmpty) return values.ToImmutable();
        using var json = ReadJson(overrides, "parameters"); var root = json.RootElement;
        Keys(root, "parameters", [], export.ParameterIds.ToArray());
        foreach (var property in root.EnumerateObject())
        {
            cancellationToken.ThrowIfCancellationRequested(); var parameter = _parameters[property.Name];
            values[property.Name] = EffectPackageParameterParser.ReadValue(property.Value, parameter.Constraint, _files, "parameters." + property.Name);
        }
        return values.ToImmutable();
    }
}
