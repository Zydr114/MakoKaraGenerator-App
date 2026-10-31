using System.Collections.Immutable;
using System.Text.Json;
using static MakoKaraGenerator.Core.Effects.EffectPackageJson;

namespace MakoKaraGenerator.Core.Effects;

/// <summary>包捕获区域；材质区域须由真实材质绑定解析，不默认当作填充。</summary>
public enum EffectCaptureRegion
{
    /// <summary>填充。</summary>
    Fill,
    /// <summary>外描边。</summary>
    Stroke,
    /// <summary>填充及外描边。</summary>
    FillAndStroke,
    /// <summary>正在绘制的材质区域。</summary>
    MaterialRegion,
}

/// <summary>单个输入的完整固定捕获配方，不携带运行目标。</summary>
/// <param name="IncludeRuby">注音。</param><param name="IncludeStroke">描边。</param>
/// <param name="IncludeShadow">阴影。</param><param name="IncludeDescendantGroups">子组。</param>
/// <param name="IncludeOwnedOverlay">该实例拥有的覆盖层。</param><param name="Region">显式区域。</param>
public sealed record EffectGraphCapture(bool IncludeRuby, bool IncludeStroke, bool IncludeShadow,
    bool IncludeDescendantGroups, bool IncludeOwnedOverlay, EffectCaptureRegion Region);

/// <summary>包文件的固定图计划及逐输入配方；不是可启用的导出或 SDK 句柄。</summary>
public sealed class EffectPackageImageGraph
{
    internal EffectPackageImageGraph(CompiledImageGraphDefinition plan, ImmutableDictionary<string, EffectGraphCapture> captures)
    { Plan = plan; Captures = captures; }
    /// <summary>由唯一 Core 编译器验证的图。</summary>
    public CompiledImageGraphDefinition Plan { get; }
    /// <summary>按输入身份索引的完整配方。</summary>
    public ImmutableDictionary<string, EffectGraphCapture> Captures { get; }
}

/// <summary>读取闭包中全部图文件，复用参数／程序目录与唯一拓扑验证器。</summary>
public static partial class EffectPackageImageGraphs
{
    /// <summary>独立读取所有 role:graph 文件，包括未引用的图；不解析仍待确认的导出编码。</summary>
    public static ImmutableDictionary<string, EffectPackageImageGraph> Read(EffectPackageContents contents,
        GraphExecutionBudget? budget = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contents); cancellationToken.ThrowIfCancellationRequested();
        using var manifest = ReadJson(contents.Manifest.AsMemory(), "manifest.json");
        var files = contents.Files.ToImmutableDictionary(file => file.Path, StringComparer.Ordinal);
        var parameters = EffectPackageParameterParser.Read(manifest.RootElement.GetProperty("parameters"), files, cancellationToken);
        return Read(contents, parameters, EffectPackageShaderPrograms.Read(contents, cancellationToken), budget, cancellationToken);
    }

    internal static ImmutableDictionary<string, EffectPackageImageGraph> Read(EffectPackageContents contents,
        ImmutableArray<EffectParameterDefinition> parameters, ImmutableDictionary<string, ShaderProgramDefinition> programs,
        GraphExecutionBudget? budget, CancellationToken cancellationToken)
    {
        var files = contents.Files.ToImmutableDictionary(file => file.Path, StringComparer.Ordinal);
        var result = ImmutableDictionary.CreateBuilder<string, EffectPackageImageGraph>(StringComparer.Ordinal);
        foreach (var file in contents.Files.Where(file => file.Role == "graph"))
        {
            cancellationToken.ThrowIfCancellationRequested(); using var json = ReadJson(file.Content.AsMemory(), file.Path);
            var root = json.RootElement; string path = file.Path;
            Keys(root, path, ["graphVersion", "id", "inputs", "parameters", "nodes", "output", "anchorMode", "bounds", "composite"], ["bypassInput"]);
            long version = Whole(root.GetProperty("graphVersion"), path + ".graphVersion");
            if (version != ImageGraphDefinition.CurrentVersion) throw Error("PackageGraphVersionMismatch", path + ".graphVersion", "图版本不受支持。");
            var bounds = root.GetProperty("bounds"); Keys(bounds, path + ".bounds", ["space", "source", "maxExpandPx"], []);
            if (RequiredText(bounds, "source", path + ".bounds") != "targetRegion")
                throw Error("PackageGraphBoundsSourceUnsupported", path + ".bounds.source", "图框必须明确来自目标区域。");
            var space = Space(RequiredText(bounds, "space", path + ".bounds"), path + ".bounds.space");
            var captures = ImmutableDictionary.CreateBuilder<string, EffectGraphCapture>(StringComparer.Ordinal);
            var inputs = ImmutableArray.CreateBuilder<GraphInputDefinition>(); int index = 0;
            foreach (var input in Array(root.GetProperty("inputs"), path + ".inputs"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                inputs.Add(ReadInput(input, path + ".inputs[" + index++ + "]", files, captures));
            }
            var nodes = ImmutableArray.CreateBuilder<ImageGraphNode>(); index = 0;
            foreach (var node in Array(root.GetProperty("nodes"), path + ".nodes"))
            {
                cancellationToken.ThrowIfCancellationRequested(); nodes.Add(ReadNode(node, path + ".nodes[" + index++ + "]", programs));
            }
            var composite = root.GetProperty("composite");
            string mode = RequiredText(composite, "mode", path + ".composite");
            Keys(composite, path + ".composite", mode == "replaceCoveredBackdrop" ? ["mode", "coverage"] : ["mode"], []);
            GraphCompositeMode operation = mode switch
            {
                "sourceOver" => GraphCompositeMode.SourceOver, "replaceCoveredBackdrop" => GraphCompositeMode.ReplaceCoveredBackdrop,
                _ => throw Error("PackageGraphCompositeUnsupported", path + ".composite.mode", "未知合成规则。"),
            };
            var anchor = RequiredText(root, "anchorMode", path) switch
            {
                "ColorOnly" => GraphAnchorMode.ColorOnly, "MappedWarp" => GraphAnchorMode.MappedWarp, "ImageOnly" => GraphAnchorMode.ImageOnly,
                _ => throw Error("PackageGraphAnchorUnsupported", path + ".anchorMode", "未知锚点协议。"),
            };
            var definition = new ImageGraphDefinition(RequiredText(root, "id", path), (int)version, inputs,
                Strings(root.GetProperty("parameters"), path + ".parameters"), nodes, Reference(root.GetProperty("output"), path + ".output"),
                space, Number(bounds.GetProperty("maxExpandPx"), path + ".bounds.maxExpandPx"), anchor, operation,
                operation == GraphCompositeMode.ReplaceCoveredBackdrop ? Reference(composite.GetProperty("coverage"), path + ".composite.coverage") : null,
                root.TryGetProperty("bypassInput", out var bypass) ? Reference(bypass, path + ".bypassInput") : null);
            try { result.Add(path, new(ImageGraphCompiler.Compile(definition, parameters.Select(parameter => parameter.Constraint), programs, budget), captures.ToImmutable())); }
            catch (ImageGraphException error) { throw Error(error.Code, path + "." + error.Field, error.Message, error); }
        }
        cancellationToken.ThrowIfCancellationRequested(); return result.ToImmutable();
    }

    private static GraphReference Reference(JsonElement value, string path)
    {
        string text = String(value, path);
        if (text.StartsWith("input:", StringComparison.Ordinal) && text.Length > 6) return GraphReference.Input(text[6..]);
        if (text.StartsWith("node:", StringComparison.Ordinal) && text.Length > 5) return GraphReference.Node(text[5..]);
        throw Error("PackageGraphReferenceInvalid", path, "边须明确引用 input: 或 node: 的非空身份。");
    }
    private static GraphCoordinateSpace Space(string value, string path) => value switch
    {
        "token" => GraphCoordinateSpace.Token, "line" => GraphCoordinateSpace.Line, "node" => GraphCoordinateSpace.Node, "canvas" => GraphCoordinateSpace.Canvas,
        _ => throw Error("PackageGraphSpaceUnsupported", path, "未知空间。"),
    };
    private static ShaderSurfaceKind Surface(string value, string path) => value switch
    {
        "color" => ShaderSurfaceKind.Color, "coverage" => ShaderSurfaceKind.Coverage,
        _ => throw Error("PackageGraphSurfaceUnsupported", path, "未知表面类型。"),
    };
}
