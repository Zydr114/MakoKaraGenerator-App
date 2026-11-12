namespace MakoKaraGenerator.Scripting;

public sealed partial class FrameWriter
{
    private readonly Dictionary<TokenFamilyTarget, StyleUpdate> _styles = [];
    private readonly BaselineStyles _baseline;
    private readonly ResourceCatalog _resources;
    private void WriteStyle(TokenFamilyTarget target, StyleUpdate update)
    {
        RequireOpen(); ArgumentNullException.ThrowIfNull(target);
        if (!_capabilities.HasFlag(EffectCapabilities.Style)) throw new EffectOutputException("UndeclaredCapability", "效果未声明 Style 能力。");
        if (!_allowed.TryGetValue(target, out var geometry)) throw new EffectOutputException("TargetOutsideBinding", "外观目标不属于本帧实例输出范围。");
        var basis = _baseline.Get(target);
        foreach (var material in update.Materials()) ValidateMaterial(material);
        bool changed = update.Unsung.Changes(basis.Unsung) || update.Sung.Changes(basis.Sung)
            || update.OpacityFactor != 1 && basis.Opacity != 0 || update.MaterialEdits.Any(edit => edit is MaterialEdit.Uniform);
        if (changed && !geometry.CanTransformIndependently)
            throw new EffectOutputException(geometry.DiagnosticCode ?? "UnsupportedGlyphPartition", "源元素共享字簇，不能独立改变外观。");
        _styles[target] = _styles.TryGetValue(target, out var previous) ? previous.Then(update) : update;
    }
    private static StyleUpdate StrokeBoth(StrokeValue? stroke) => StyleUpdate.Both(new(strokes: new(stroke is null ? [] : [stroke])));
    private void ValidateMaterial(MaterialValue? material)
    {
        if (material is not MaterialValue.PackageMaterial package) return;
        if (!_resources.Contains(package.Handle)) throw new EffectOutputException("InvalidMaterialResource", "材质不属于本实例固定资源修订。");
        package.Parameters.ValidateResources(_resources);
    }
    /// <summary>Evaluate 内的限时外观 writer，基础字与其注音共用。</summary>
    public sealed partial class StyleWriter
    {
        private readonly FrameWriter _owner;
        internal StyleWriter(FrameWriter owner) { _owner = owner; }
        /// <summary>明确替换未唱材质；null 无效，禁用请用透明 Solid。</summary>
        public void SetUnsungMaterial(TokenFamilyTarget target, MaterialValue material)
        { ArgumentNullException.ThrowIfNull(material); _owner.WriteStyle(target, new(unsung: new(fill: material))); }
        /// <summary>明确替换已唱材质；Set 按顺序覆盖。</summary>
        public void SetSungMaterial(TokenFamilyTarget target, MaterialValue material)
        { ArgumentNullException.ThrowIfNull(material); _owner.WriteStyle(target, new(sung: new(fill: material))); }
        /// <summary>两态一起替换为单层描边；null 明确关闭。</summary>
        public void SetStroke(TokenFamilyTarget target, StrokeValue? stroke) => _owner.WriteStyle(target, StrokeBoth(stroke));
        /// <summary>两态一起替换阴影；null 明确关闭。</summary>
        public void SetShadow(TokenFamilyTarget target, ShadowValue? shadow) => _owner.WriteStyle(target, StyleUpdate.Both(new(shadow: new(shadow))));
        /// <summary>乘样式 alpha，最终值由宿主校验；不包含源入场 alpha。</summary>
        public void MultiplyOpacity(TokenFamilyTarget target, double factor) => _owner.WriteStyle(target, new(opacityFactor: factor));
    }
}
