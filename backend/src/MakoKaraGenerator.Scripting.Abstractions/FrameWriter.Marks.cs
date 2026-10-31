namespace MakoKaraGenerator.Scripting;

public sealed partial class FrameWriter
{
    private readonly Dictionary<TextMarkTarget, GeometrySample> _allowedMarks;
    private readonly Dictionary<TextMarkTarget, Affine2> _markTransforms = [];
    private readonly Dictionary<TextMarkTarget, StyleUpdate> _markStyles = [];
    private GeometrySample RequireMark(TextMarkTarget target, EffectCapabilities capability)
    {
        RequireOpen(); ArgumentNullException.ThrowIfNull(target);
        if (!_capabilities.HasFlag(capability)) throw new EffectOutputException("UndeclaredCapability", "效果未声明此标记输出能力。");
        return _allowedMarks.TryGetValue(target, out var geometry) ? geometry
            : throw new EffectOutputException("TargetOutsideBinding", "文字标记不属于本帧合法提示装载。");
    }
    private void TransformMark(TextMarkTarget target, Affine2 matrix)
    {
        _ = RequireMark(target, EffectCapabilities.Transforms);
        if (!matrix.TryInverse(out _)) throw new EffectOutputException("InvalidTransform", "标记仿射必须有限且可逆。");
        if (matrix == Affine2.Identity) return;
        try
        {
            var combined = _markTransforms.GetValueOrDefault(target, Affine2.Identity).Compose(matrix);
            if (!combined.TryInverse(out _)) throw new EffectOutputException("InvalidTransform", "标记仿射组合不可逆。");
            if (combined == Affine2.Identity) _markTransforms.Remove(target); else _markTransforms[target] = combined;
        }
        catch (ArgumentException exception) { throw new EffectOutputException("InvalidTransform", "标记仿射组合溢出：" + exception.Message); }
    }
    private void WriteMarkStyle(TextMarkTarget target, StyleUpdate update)
    {
        _ = RequireMark(target, EffectCapabilities.Style); _ = _baseline.Get(target);
        ValidateMaterial(update.UnsungMaterial); ValidateMaterial(update.SungMaterial); ValidateMaterial(update.Stroke?.Value?.Material);
        _markStyles[target] = _markStyles.TryGetValue(target, out var previous) ? previous.Then(update) : update;
    }
    public sealed partial class StyleWriter
    {
        /// <summary>设置标记 Pending 材质，不产生未唱元素或演唱事件。</summary>
        public void SetUnsungMaterial(TextMarkTarget target, MaterialValue material)
        { ArgumentNullException.ThrowIfNull(material); _owner.WriteMarkStyle(target, new(unsung: material)); }
        /// <summary>设置标记 Elapsed 材质，不制造逐字进度。</summary>
        public void SetSungMaterial(TextMarkTarget target, MaterialValue material)
        { ArgumentNullException.ThrowIfNull(material); _owner.WriteMarkStyle(target, new(sung: material)); }
        /// <summary>设置标记外部描边；null 明确关闭。</summary>
        public void SetStroke(TextMarkTarget target, StrokeValue? stroke) => _owner.WriteMarkStyle(target, new(stroke: new(stroke)));
        /// <summary>设置标记阴影；null 明确关闭。</summary>
        public void SetShadow(TextMarkTarget target, ShadowValue? shadow) => _owner.WriteMarkStyle(target, new(shadow: new(shadow)));
        /// <summary>乘标记静态 alpha，动态附着透明度单独且只应用一次。</summary>
        public void MultiplyOpacity(TextMarkTarget target, double factor) => _owner.WriteMarkStyle(target, new(opacityFactor: factor));
    }
}
