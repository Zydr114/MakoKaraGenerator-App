using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering;

/// <summary>固定材质图的逐节点 uniform 合同校验；不编译源码或持有 native 对象。</summary>
public static class MaterialUniformValidation
{
    /// <summary>校验全部显式覆盖，包括未执行节点和透明跳过图，不能借旁路掩盖坏协议。</summary>
    public static void Validate(CompiledImageGraphDefinition plan, GraphUniformValues values)
    {
        ArgumentNullException.ThrowIfNull(plan); ArgumentNullException.ThrowIfNull(values);
        foreach (var pair in values.Values) Validate(plan, pair.Key.NodeId, pair.Key.Name, pair.Value);
    }
    /// <summary>校验真实 shader 节点、声明名、七类类型、显式数组长度及固定 float32 范围。</summary>
    public static void Validate(CompiledImageGraphDefinition plan, string nodeId, string name, UniformValue value)
    {
        ArgumentNullException.ThrowIfNull(plan); ArgumentNullException.ThrowIfNull(value);
        if (plan.Nodes.FirstOrDefault(node => node.Id == nodeId) is not ImageGraphNode.RuntimeShader shader)
            throw new EffectOutputException("MaterialUniformNodeInvalid", "uniform 必须指向真实 RuntimeShader 节点：" + nodeId);
        var declaration = plan.Programs[shader.ProgramId].Uniforms.FirstOrDefault(item => item.Name == name);
        if (declaration is null)
            throw new EffectOutputException("MaterialUniformUnknown", "节点未声明此 uniform：" + nodeId + "." + name);
        bool matches = declaration.Kind switch
        {
            ShaderUniformKind.Float => value.Kind == UniformValueKind.Float,
            ShaderUniformKind.Float2 => value.Kind == UniformValueKind.Float2,
            ShaderUniformKind.Float3 => value.Kind == UniformValueKind.Float3,
            ShaderUniformKind.Float4 => value.Kind == UniformValueKind.Float4,
            ShaderUniformKind.Int => value.Kind == UniformValueKind.Int,
            ShaderUniformKind.Color => value.Kind == UniformValueKind.Color,
            ShaderUniformKind.Matrix3x3 => value.Kind == UniformValueKind.Matrix3x3,
            _ => false,
        };
        if (!matches || declaration.ArrayLength != value.ArrayLength)
            throw new EffectOutputException("ShaderUniformValueMismatch", "uniform 类型／数组长度与节点声明不同：" + nodeId + "." + name);
        foreach (double number in value.Numbers)
            if (!float.IsFinite((float)number) || number != 0 && (float)number == 0)
                throw new EffectOutputException("ShaderUniformNativeRange", "uniform 超出固定 float32 范围或非零下溢：" + nodeId + "." + name);
    }
}
