namespace MakoKaraGenerator.Scripting;

// Shared by the per-call SDK transaction and the all-instance frame admission.
// Counts every attempted complete write, including values replaced later.
internal sealed class GraphBindingBudget
{
    internal const int MaximumWrites = 256;
    internal const int MaximumScalars = 65_536;
    internal const long MaximumBytes = 8 * 1024 * 1024;
    private int _writes, _scalars;
    private long _bytes;

    internal void Charge(GraphBindingValue binding, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(binding);
        Charge(binding.Graph.Identity, binding.Parameters, binding.Uniforms, binding.Transforms, 0, 0, token);
    }

    internal void ChargeMaterial(MaterialValue? material, CancellationToken token = default)
    {
        switch (material)
        {
            case MaterialValue.PackageMaterial package:
                Charge(package.Handle.Identity, package.Parameters, package.Uniforms, GraphTransformValues.Empty, 0, 0, token); break;
            case MaterialValue.GradientMaterial gradient:
                int count = gradient.Value.Stops.Length;
                if (count > MaximumScalars / 5) throw Exceeded();
                Charge(null, ParameterValues.Empty, GraphUniformValues.Empty, GraphTransformValues.Empty, 9 + 5 * count, 128L + 40L * count, token); break;
            default:
                Charge(null, ParameterValues.Empty, GraphUniformValues.Empty, GraphTransformValues.Empty, material is null ? 0 : 4, 32, token); break;
        }
    }
    internal void ChargeMaterialUniform(MaterialHandle handle, string nodeId, string name, UniformValue value, CancellationToken token = default)
        => Charge(handle.Identity, ParameterValues.Empty, new([new((nodeId, name), value)]), GraphTransformValues.Empty, 0, 0, token);

    private void Charge(ResourceIdentity? identity, ParameterValues parameters, GraphUniformValues uniforms, GraphTransformValues transforms,
        int extraScalars, long extraBytes, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_writes == MaximumWrites) throw Exceeded();
        int scalars = 0; long bytes = 256;
        void Add(int count, long size)
        {
            if (count < 0 || size < 0 || count > MaximumScalars - _scalars - scalars || size > MaximumBytes - _bytes - bytes)
                throw Exceeded();
            scalars += count; bytes += size;
        }
        void Text(string value) { Add(0, checked(24L + 2L * value.Length)); }
        Add(extraScalars, extraBytes);
        if (identity is not null) { Text(identity.PackageId); Text(identity.Revision); Text(identity.ExportId); }
        foreach (var pair in parameters.Entries)
        {
            token.ThrowIfCancellationRequested(); Text(pair.Key); Add(0, 64);
            switch (pair.Value)
            {
                case ParameterValue.Number number: Finite(number.Value); Add(1, 8); break;
                case ParameterValue.Whole: case ParameterValue.Toggle: case ParameterValue.Duration: Add(1, 8); break;
                case ParameterValue.Color color:
                    Finite(color.Value.Red); Finite(color.Value.Green); Finite(color.Value.Blue); Finite(color.Value.Alpha); Add(4, 32); break;
                case ParameterValue.Choice choice: Text(choice.Value); break;
                case ParameterValue.Text text: Text(text.Value); break;
                case ParameterValue.Asset asset:
                    Text(asset.Value.Identity.PackageId); Text(asset.Value.Identity.Revision); Text(asset.Value.Identity.ExportId); break;
                case ParameterValue.Curve curve:
                    switch (curve.Value)
                    {
                        case CurveValue.Easing: Add(1, 8); break;
                        case CurveValue.CubicBezier bezier:
                            Finite(bezier.First.X); Finite(bezier.First.Y); Finite(bezier.Second.X); Finite(bezier.Second.Y); Add(4, 32); break;
                        case CurveValue.ControlPoints control:
                            Add(checked(control.Points.Length * 2), checked(16L * control.Points.Length));
                            foreach (var point in control.Points) { token.ThrowIfCancellationRequested(); Finite(point.X); Finite(point.Y); }
                            break;
                        default: throw Invalid("未知曲线值。");
                    }
                    break;
                default: throw Invalid("未知参数值。");
            }
        }
        foreach (var pair in uniforms.Values)
        {
            token.ThrowIfCancellationRequested(); Text(pair.Key.NodeId); Text(pair.Key.Name); Add(0, 96);
            var value = pair.Value;
            int components = value.Kind switch
            {
                UniformValueKind.Float or UniformValueKind.Int => 1, UniformValueKind.Float2 => 2,
                UniformValueKind.Float3 => 3, UniformValueKind.Float4 or UniformValueKind.Color => 4,
                UniformValueKind.Matrix3x3 => 9, _ => throw Invalid("未知 uniform 类型。"),
            };
            int length = value.ArrayLength ?? 1;
            if (length <= 0 || length > MaximumScalars / components) throw Exceeded();
            int count = checked(length * components);
            bool integer = value.Kind == UniformValueKind.Int;
            if (integer ? value.Integers.Length != count || !value.Numbers.IsEmpty : value.Numbers.Length != count || !value.Integers.IsEmpty)
                throw Invalid("uniform 值自身的类型或数组形状不一致。");
            Add(count, checked(8L * count));
            foreach (double number in value.Numbers)
            {
                token.ThrowIfCancellationRequested(); float native = (float)number;
                if (!double.IsFinite(number) || !float.IsFinite(native) || number != 0 && native == 0)
                    throw new EffectOutputException("ShaderUniformNativeRange", "uniform 值超出固定 float32 范围或非零下溢。");
            }
        }
        foreach (var pair in transforms.Values)
        {
            token.ThrowIfCancellationRequested(); Text(pair.Key); Add(10, 160);
            // GraphTransformValue already owns intrinsic finite/native/inverse and
            // nonempty bounds validation. Real node identity is final-binding work.
            if (pair.Value is null) throw Invalid("Transform 覆盖不能为空。");
        }
        _writes++; _scalars += scalars; _bytes += bytes;
    }
    private static void Finite(double value)
    { if (!double.IsFinite(value)) throw Invalid("图参数值必须有限。"); }
    private static EffectOutputException Invalid(string message) => new("InvalidGraphBindingValue", message);
    private static EffectOutputException Exceeded() => new("GraphOutputBudgetExceeded", "全部图／材质写入、标量、数组或验证字节超过固定帧限值。");
}
