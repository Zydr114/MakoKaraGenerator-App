using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using static MakoKaraGenerator.Core.Effects.EffectPackageJson;

namespace MakoKaraGenerator.Core.Effects;

public static partial class EffectPackageImageGraphs
{
    private static readonly string[] ColorComponents = ["r", "g", "b", "a"];
    private static ImageGraphNode ReadNode(JsonElement item, string path, ImmutableDictionary<string, ShaderProgramDefinition> programs)
    {
        string id = RequiredText(item, "id", path), op = RequiredText(item, "op", path);
        string[] fields = op switch
        {
            "RuntimeShader" => ["program", "children", "uniforms", "outputType"], "Blur" => ["input", "sigmaPx"],
            "Mask" => ["color", "coverage"], "Transform" => ["input", "matrix", "outputBounds"],
            "Blend" => ["foreground", "background", "mode"],
            _ => throw Error("UnsupportedGraphOperation", path + ".op", "操作不属于图 v1。"),
        };
        Keys(item, path, ["id", "op", "bounds", "edge", .. fields], []);
        var bound = item.GetProperty("bounds"); Keys(bound, path + ".bounds", ["expandPx", "maxExpandPx", "maxSampleOffsetPx"], []);
        var edge = RequiredText(item, "edge", path) switch
        {
            "transparent" => GraphEdgeMode.Transparent, "clamp" => GraphEdgeMode.Clamp, "repeat" => GraphEdgeMode.Repeat,
            _ => throw Error("PackageGraphEdgeUnsupported", path + ".edge", "未知域外采样规则。"),
        };
        var bounds = new GraphNodeBounds(Expansion(bound.GetProperty("expandPx"), path + ".bounds.expandPx"),
            Number(bound.GetProperty("maxExpandPx"), path + ".bounds.maxExpandPx"), Number(bound.GetProperty("maxSampleOffsetPx"), path + ".bounds.maxSampleOffsetPx"), edge);
        GraphReference Ref(string key) => Reference(item.GetProperty(key), path + "." + key);
        switch (op)
        {
            case "RuntimeShader": return Shader(item, path, id, bounds, programs);
            case "Blur": return new ImageGraphNode.Blur(id, Ref("input"), NumberBinding(item.GetProperty("sigmaPx"), path + ".sigmaPx"), bounds);
            case "Mask": return new ImageGraphNode.Mask(id, Ref("color"), Ref("coverage"), bounds);
            case "Transform":
                var matrix = Components(item.GetProperty("matrix"), path + ".matrix", 6);
                var rect = Components(item.GetProperty("outputBounds"), path + ".outputBounds", 4);
                return new ImageGraphNode.Transform(id, Ref("input"), new(matrix[0], matrix[1], matrix[2], matrix[3], matrix[4], matrix[5]),
                    new(rect[0], rect[1], rect[2], rect[3]), bounds);
            default:
                var mode = RequiredText(item, "mode", path) switch
                {
                    "sourceOver" => GraphBlendMode.SourceOver, "plus" => GraphBlendMode.Plus, "multiply" => GraphBlendMode.Multiply,
                    _ => throw Error("InvalidGraphBlend", path + ".mode", "未知混合方式。"),
                };
                return new ImageGraphNode.Blend(id, Ref("foreground"), Ref("background"), mode, bounds);
        }
    }

    private static ImageGraphNode.RuntimeShader Shader(JsonElement item, string path, string id, GraphNodeBounds bounds,
        ImmutableDictionary<string, ShaderProgramDefinition> programs)
    {
        string program = RequiredText(item, "program", path); EffectPackageContents.ValidatePath(program);
        if (!programs.TryGetValue(program, out var definition)) throw Error("GraphShaderMissing", path + ".program", "须引用同闭包独立 shader 程序文件。");
        var values = item.GetProperty("uniforms"); Keys(values, path + ".uniforms", definition.Uniforms.Select(value => value.Name).ToArray(), []);
        var uniforms = definition.Uniforms.Select(uniform => KeyValuePair.Create(uniform.Name,
            Uniform(values.GetProperty(uniform.Name), path + ".uniforms." + uniform.Name, uniform))).ToImmutableArray();
        var ports = item.GetProperty("children"); Keys(ports, path + ".children", definition.Children.Select(value => value.Name).ToArray(), []);
        return new(id, program, Surface(RequiredText(item, "outputType", path), path + ".outputType"), uniforms,
            definition.Children.Select(child => KeyValuePair.Create(child.Name, Reference(ports.GetProperty(child.Name), path + ".children." + child.Name))), bounds);
    }
    private static GraphUniformBinding Uniform(JsonElement value, string path, ShaderUniformDeclaration declaration)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            string binding = String(value, path);
            if (binding.StartsWith("parameter:", StringComparison.Ordinal) && binding.Length > 10) return new GraphUniformBinding.Parameter(binding[10..]);
            return binding switch
            {
                "host:timeSeconds" => new GraphUniformBinding.Host(GraphHostValue.TimeSeconds), "host:canvasSize" => new GraphUniformBinding.Host(GraphHostValue.CanvasSize),
                _ => throw Error("PackageGraphUniformBindingInvalid", path, "只接受明确参数／封闭宿主绑定。"),
            };
        }
        var elements = declaration.ArrayLength is not null || declaration.ScalarCount > 1 && declaration.Kind != ShaderUniformKind.Color
            ? Array(value, path).ToArray() : [value];
        try
        {
            if (declaration.Kind == ShaderUniformKind.Int)
            {
                var integers = elements.Select(element => Whole(element, path)).ToArray();
                if (integers.Any(number => number is < int.MinValue or > int.MaxValue)) throw Range(path);
                return new GraphUniformBinding.Literal(ShaderLiteral.Whole(integers.Select(number => (int)number), declaration.ArrayLength));
            }
            double[] numbers;
            if (declaration.Kind == ShaderUniformKind.Color)
            {
                numbers = elements.SelectMany(element =>
                {
                    Keys(element, path, ["r", "g", "b", "a"], []);
                    return ColorComponents.Select(component => Number(element.GetProperty(component), path + "." + component));
                }).ToArray();
            }
            else numbers = elements.Select(element => Number(element, path)).ToArray();
            return new GraphUniformBinding.Literal(ShaderLiteral.Numeric(declaration.Kind, numbers, declaration.ArrayLength));
        }
        catch (ArgumentException error) { throw Error("PackageGraphUniformLiteralInvalid", path, "完整字面量的类型、分量数量或颜色范围不符。", error); }
    }
    private static double[] Components(JsonElement value, string path, int count)
    {
        var result = Array(value, path).Select(component => Number(component, path)).ToArray();
        if (result.Length != count) throw Error("PackageGraphComponentCountInvalid", path, "向量／矩阵分量数量不符。");
        return result;
    }
    private static GraphNumberBinding NumberBinding(JsonElement value, string path)
    {
        if (value.ValueKind == JsonValueKind.Number) return new GraphNumberBinding.Literal(Number(value, path));
        string binding = String(value, path);
        if (binding.StartsWith("parameter:", StringComparison.Ordinal) && binding.Length > 10) return new GraphNumberBinding.Parameter(binding[10..]);
        throw Error("PackageGraphNumberBindingInvalid", path, "只接受数值或明确 number 参数。");
    }
    private static GraphExpansion Expansion(JsonElement value, string path)
    {
        if (value.ValueKind == JsonValueKind.Number) return new GraphExpansion.Literal(Number(value, path));
        string expression = String(value, path); const string separator = " * parameter:";
        int at = expression.IndexOf(separator, StringComparison.Ordinal);
        if (expression.StartsWith("ceil(", StringComparison.Ordinal) && expression.EndsWith(')') && at > 5
            && double.TryParse(expression.AsSpan(5, at - 5), NumberStyles.Float, CultureInfo.InvariantCulture, out double factor)
            && double.IsFinite(factor) && factor >= 0 && expression.Length > at + separator.Length + 1)
            return new GraphExpansion.CeilingMultiple(expression[(at + separator.Length)..^1], factor);
        throw Error("PackageGraphExpansionInvalid", path, "外扩只接受常量或 ceil(k * parameter:id)。");
    }
}
