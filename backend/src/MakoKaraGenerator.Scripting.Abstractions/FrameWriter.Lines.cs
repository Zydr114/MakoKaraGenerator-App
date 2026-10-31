namespace MakoKaraGenerator.Scripting;

/// <summary>当前真实整行的局部视觉仿射，包含关联注音，不改排版。</summary>
/// <param name="Target">真实行和装载。</param><param name="LocalMatrix">LineGeometry 的逻辑坐标矩阵。</param>
public sealed record LineTransform(LineTarget Target, Affine2 LocalMatrix);

public sealed partial class FrameWriter
{
    private readonly HashSet<LineTarget> _allowedLines;
    private readonly Dictionary<LineTarget, Affine2> _lineTransforms = [];
    private void TransformLine(LineTarget target, Affine2 matrix)
    {
        RequireOpen(); ArgumentNullException.ThrowIfNull(target);
        if (!_capabilities.HasFlag(EffectCapabilities.Transforms)) throw new EffectOutputException("UndeclaredCapability", "效果未声明整行变换能力。");
        if (!_allowedLines.Contains(target)) throw new EffectOutputException("TargetOutsideBinding", "整行不属于本帧合法输出范围。");
        if (!matrix.TryInverse(out _)) throw new EffectOutputException("InvalidTransform", "整行仿射必须有限且可逆。");
        if (matrix == Affine2.Identity) return;
        try
        {
            var local = _lineTransforms.GetValueOrDefault(target, Affine2.Identity).Compose(matrix);
            if (!local.TryInverse(out _)) throw new EffectOutputException("InvalidTransform", "整行组合矩阵不可逆。");
            if (local == Affine2.Identity) _lineTransforms.Remove(target); else _lineTransforms[target] = local;
        }
        catch (ArgumentException error) { throw new EffectOutputException("InvalidTransform", "整行组合矩阵溢出：" + error.Message); }
    }
}
