using System.Collections.Immutable;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering.Skia;

internal static class ImageGraphBindings
{
    internal static void ValidateParameters(CompiledImageGraphDefinition plan, ParameterValues values)
        => ValidateParameters(plan.Parameters.Values, plan.Definition.Id, values);
    internal static void ValidateParameters(IEnumerable<EffectParameterConstraint> constraints, string graphId, ParameterValues values)
    {
        try
        {
            foreach (var parameter in constraints)
            {
                bool valid = parameter switch
                {
                    EffectParameterConstraint.Number number => InRange(values.GetNumber(number.Id), number.Minimum, number.Maximum),
                    EffectParameterConstraint.Whole integer => values.GetInteger(integer.Id) >= integer.Minimum && values.GetInteger(integer.Id) <= integer.Maximum,
                    EffectParameterConstraint.Toggle toggle => Read(values.GetBoolean(toggle.Id)),
                    EffectParameterConstraint.Color color => Read(values.GetColor(color.Id)),
                    EffectParameterConstraint.Choice choice => choice.Values.Contains(values.GetEnum(choice.Id), StringComparer.Ordinal),
                    EffectParameterConstraint.Duration duration => values.GetDuration(duration.Id) >= duration.Minimum && values.GetDuration(duration.Id) <= duration.Maximum,
                    EffectParameterConstraint.Text text => Read(values.GetText(text.Id)),
                    EffectParameterConstraint.Curve curve => Read(values.GetCurve(curve.Id)),
                    EffectParameterConstraint.Asset asset => Read(values.GetAsset(asset.Id)),
                    _ => false,
                };
                if (!valid) throw new ImageGraphException("GraphParameterOutOfRange", graphId, "parameters." + parameter.Id, "参数超出已接受约束。");
            }
        }
        catch (Exception exception) when (exception is KeyNotFoundException or InvalidOperationException && exception is not ImageGraphException)
        { throw new ImageGraphException("GraphParameterTypeMismatch", graphId, "parameters", exception.Message, exception); }
    }
    private static bool Read<T>(T value) => value is not null;
    private static bool InRange(double value, double minimum, double maximum) => double.IsFinite(value) && value >= minimum && value <= maximum;
    internal static double Number(GraphNumberBinding binding, ParameterValues values) => binding switch
    { GraphNumberBinding.Literal literal => literal.Value, GraphNumberBinding.Parameter parameter => values.GetNumber(parameter.Id), _ => throw new InvalidOperationException("未知数值绑定。") };
    internal static double Expansion(GraphExpansion binding, ParameterValues values) => binding switch
    { GraphExpansion.Literal literal => literal.Value, GraphExpansion.CeilingMultiple multiple => Math.Ceiling(multiple.Factor * values.GetNumber(multiple.ParameterId)), _ => throw new InvalidOperationException("未知外扩绑定。") };
    internal static UniformValues Uniforms(ImageGraphNode.RuntimeShader node, CompiledImageGraphDefinition plan, ImageGraphFrameContext frame)
        => new(node.Uniforms.Select(pair => KeyValuePair.Create(pair.Key,
            frame.Uniforms.Values.TryGetValue((node.Id, pair.Key), out var value) ? value : Resolve(pair.Value, plan, frame))));
    private static UniformValue Resolve(GraphUniformBinding binding, CompiledImageGraphDefinition plan, ImageGraphFrameContext frame) => binding switch
    {
        GraphUniformBinding.Host { Value: GraphHostValue.TimeSeconds } => UniformValue.Float(frame.TimeUs / 1_000_000d),
        GraphUniformBinding.Host { Value: GraphHostValue.CanvasSize } => UniformValue.Float2(frame.CanvasWidth, frame.CanvasHeight),
        GraphUniformBinding.Parameter parameter => plan.Parameters[parameter.Id] switch
        {
            EffectParameterConstraint.Number => UniformValue.Float(frame.Parameters.GetNumber(parameter.Id)),
            EffectParameterConstraint.Whole => UniformValue.Int(checked((int)frame.Parameters.GetInteger(parameter.Id))),
            EffectParameterConstraint.Toggle => UniformValue.Int(frame.Parameters.GetBoolean(parameter.Id) ? 1 : 0),
            EffectParameterConstraint.Color => UniformValue.Color(frame.Parameters.GetColor(parameter.Id)),
            _ => throw new InvalidOperationException("参数不支持 uniform 类型。"),
        },
        GraphUniformBinding.Literal literal => Literal(literal.Value),
        _ => throw new InvalidOperationException("未知 uniform 绑定。"),
    };
    private static UniformValue Literal(ShaderLiteral literal)
    {
        int count = literal.ArrayLength ?? 1;
        int stride = new ShaderUniformDeclaration("value", literal.Kind).ComponentCount;
        UniformValue Value(int index)
        {
            var n = literal.Numbers; int offset = index * stride;
            return literal.Kind switch
            {
                ShaderUniformKind.Float => UniformValue.Float(n[offset]),
                ShaderUniformKind.Float2 => UniformValue.Float2(n[offset], n[offset + 1]),
                ShaderUniformKind.Float3 => UniformValue.Float3(n[offset], n[offset + 1], n[offset + 2]),
                ShaderUniformKind.Float4 => UniformValue.Float4(n[offset], n[offset + 1], n[offset + 2], n[offset + 3]),
                ShaderUniformKind.Color => UniformValue.Color(new(n[offset], n[offset + 1], n[offset + 2], n[offset + 3])),
                ShaderUniformKind.Int => UniformValue.Int(literal.Integers[index]),
                ShaderUniformKind.Matrix3x3 => UniformValue.Matrix3x3(n[offset], n[offset + 1], n[offset + 2], n[offset + 3], n[offset + 4], n[offset + 5], n[offset + 6], n[offset + 7], n[offset + 8]),
                _ => throw new InvalidOperationException("未知字面量类型。"),
            };
        }
        return literal.ArrayLength.HasValue ? UniformValue.Array(Enumerable.Range(0, count).Select(Value)) : Value(0);
    }
}
