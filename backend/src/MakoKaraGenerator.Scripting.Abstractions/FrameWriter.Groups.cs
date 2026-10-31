namespace MakoKaraGenerator.Scripting;

/// <summary>真实容器组的局部视觉仿射，不改源布局或显示调度。</summary>
/// <param name="Target">真实组节点。</param><param name="LocalMatrix">组参考框逻辑空间的矩阵。</param>
public sealed record GroupTransform(GroupTarget Target, Affine2 LocalMatrix);

/// <summary>组的共同透明度乘数；最终范围由宿主在全部实例组合后校验。</summary>
/// <param name="Target">真实组节点。</param><param name="Factor">有限非负乘数，不包含孩子自身透明度。</param>
public sealed record GroupOpacity(GroupTarget Target, double Factor);

public sealed partial class FrameWriter
{
    private readonly HashSet<GroupTarget> _allowedGroups;
    private readonly Dictionary<GroupTarget, Affine2> _groupTransforms = [];
    private readonly Dictionary<GroupTarget, double> _groupOpacities = [];
    private void MultiplyGroupOpacity(GroupTarget target, double factor)
    {
        RequireOpen(); ArgumentNullException.ThrowIfNull(target);
        if (!_capabilities.HasFlag(EffectCapabilities.Style)) throw new EffectOutputException("UndeclaredCapability", "效果未声明组透明度能力。");
        if (!_allowedGroups.Contains(target)) throw new EffectOutputException("TargetOutsideBinding", "容器组不属于本帧合法输出范围。");
        double combined = _groupOpacities.GetValueOrDefault(target, 1) * factor;
        if (!double.IsFinite(factor) || factor < 0 || !double.IsFinite(combined))
            throw new EffectOutputException("InvalidStyleOpacity", "组透明度乘数及组合必须有限且非负。");
        if (combined == 1) _groupOpacities.Remove(target); else _groupOpacities[target] = combined;
    }
    public sealed partial class StyleWriter
    {
        /// <summary>统一乘真实组的合成透明度，孩子先合成；不改变各孩子外观。</summary>
        public void MultiplyOpacity(GroupTarget target, double factor) => _owner.MultiplyGroupOpacity(target, factor);
    }
    private void TransformGroup(GroupTarget target, Affine2 matrix)
    {
        RequireOpen(); ArgumentNullException.ThrowIfNull(target);
        if (!_capabilities.HasFlag(EffectCapabilities.Transforms)) throw new EffectOutputException("UndeclaredCapability", "效果未声明组变换能力。");
        if (!_allowedGroups.Contains(target)) throw new EffectOutputException("TargetOutsideBinding", "容器组不属于本帧合法输出范围。");
        if (!matrix.TryInverse(out _)) throw new EffectOutputException("InvalidTransform", "组仿射必须有限且可逆。");
        if (matrix == Affine2.Identity) return;
        try
        {
            var local = _groupTransforms.GetValueOrDefault(target, Affine2.Identity).Compose(matrix);
            if (!local.TryInverse(out _)) throw new EffectOutputException("InvalidTransform", "组组合矩阵不可逆。");
            if (local == Affine2.Identity) _groupTransforms.Remove(target); else _groupTransforms[target] = local;
        }
        catch (ArgumentException error) { throw new EffectOutputException("InvalidTransform", "组组合矩阵溢出：" + error.Message); }
    }
}
