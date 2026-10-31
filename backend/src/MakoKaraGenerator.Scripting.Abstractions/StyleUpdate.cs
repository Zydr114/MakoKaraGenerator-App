using System.Collections.Immutable;

namespace MakoKaraGenerator.Scripting;

/// <summary>一次显式描边写入；Value=null 表示关闭，不等于没有写入。</summary>
/// <param name="Value">新描边或明确关闭。</param>
public sealed record StrokeUpdate(StrokeValue? Value);
/// <summary>一次显式阴影写入；Value=null 表示关闭。</summary>
/// <param name="Value">新阴影或明确关闭。</param>
public sealed record ShadowUpdate(ShadowValue? Value);

/// <summary>同一目标的帧外观描述；Set 后项覆盖，alpha 系数按顺序相乘。</summary>
public sealed record StyleUpdate
{
    /// <summary>空材质字段为未写入；填充禁用使用透明 Solid，装饰用显式 update(null)。</summary>
    public StyleUpdate(MaterialValue? unsung = null, MaterialValue? sung = null, StrokeUpdate? stroke = null,
        ShadowUpdate? shadow = null, double opacityFactor = 1)
    {
        Point2.Finite(opacityFactor); ArgumentOutOfRangeException.ThrowIfNegative(opacityFactor);
        UnsungMaterial = unsung; SungMaterial = sung; Stroke = stroke; Shadow = shadow; OpacityFactor = opacityFactor;
        var edits = ImmutableArray.CreateBuilder<MaterialEdit>();
        if (unsung is not null) edits.Add(new MaterialEdit.ReplaceMaterial(MaterialSlot.UnsungFill, unsung));
        if (sung is not null) edits.Add(new MaterialEdit.ReplaceMaterial(MaterialSlot.SungFill, sung));
        if (stroke is not null) edits.Add(new MaterialEdit.ReplaceMaterial(MaterialSlot.Stroke, stroke.Value?.Material));
        MaterialEdits = edits.ToImmutable();
    }
    /// <summary>未唱写入，空为不写。</summary>
    public MaterialValue? UnsungMaterial { get; }
    /// <summary>已唱写入，空为不写。</summary>
    public MaterialValue? SungMaterial { get; }
    /// <summary>描边显式写入，空为不写。</summary>
    public StrokeUpdate? Stroke { get; }
    /// <summary>阴影显式写入，空为不写。</summary>
    public ShadowUpdate? Shadow { get; }
    /// <summary>非负有限乘数，最终样式 alpha 仍必须落在 [0,1]。</summary>
    public double OpacityFactor { get; }
    /// <summary>按调用顺序保存的材质 Set／uniform；跨实例合并保留真实顺序。</summary>
    public ImmutableArray<MaterialEdit> MaterialEdits { get; private init; }
    internal static StyleUpdate Uniform(MaterialEdit.Uniform edit) => new() { MaterialEdits = [edit] };
    /// <summary>按原有外观和完整操作内容比较，重放不因数组引用不同变成不同帧。</summary>
    public bool Equals(StyleUpdate? other) => ReferenceEquals(this, other) || other is not null
        && UnsungMaterial == other.UnsungMaterial && SungMaterial == other.SungMaterial && Stroke == other.Stroke
        && Shadow == other.Shadow && OpacityFactor == other.OpacityFactor && MaterialEdits.SequenceEqual(other.MaterialEdits);
    /// <inheritdoc/>
    public override int GetHashCode()
    {
        HashCode hash = new(); hash.Add(UnsungMaterial); hash.Add(SungMaterial); hash.Add(Stroke); hash.Add(Shadow); hash.Add(OpacityFactor);
        foreach (var edit in MaterialEdits) hash.Add(edit);
        return hash.ToHashCode();
    }
    /// <summary>按调用／实例顺序组合；不把后项基线读取替换成前项结果。</summary>
    public StyleUpdate Then(StyleUpdate next)
    {
        ArgumentNullException.ThrowIfNull(next); double factor = OpacityFactor * next.OpacityFactor;
        if (!double.IsFinite(factor)) throw new EffectOutputException("InvalidStyleOpacity", "样式透明度系数组合溢出。");
        return new(next.UnsungMaterial ?? UnsungMaterial, next.SungMaterial ?? SungMaterial,
            next.Stroke ?? Stroke, next.Shadow ?? Shadow, factor) { MaterialEdits = MaterialEdits.AddRange(next.MaterialEdits) };
    }
    /// <summary>对静态基线求最终外观；最终 alpha 越界报错，不夹值。</summary>
    public StyleView Apply(StyleView baseline)
    {
        ArgumentNullException.ThrowIfNull(baseline); double opacity = baseline.Opacity * OpacityFactor;
        if (!double.IsFinite(opacity) || opacity is < 0 or > 1) throw new EffectOutputException("InvalidStyleOpacity", "最终样式不透明度必须在 [0,1]。");
        var material = ApplyMaterials(baseline);
        return new(material.UnsungMaterial, material.SungMaterial, material.Stroke,
            Shadow is { } shadow ? shadow.Value : baseline.Shadow, opacity);
    }
    /// <summary>按顺序解析材质身份与覆盖；保留基线 alpha／阴影，最终 alpha 在全部实例合并后校验。</summary>
    public StyleView ApplyMaterials(StyleView baseline)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        MaterialValue?[] values = [baseline.UnsungMaterial, baseline.SungMaterial, baseline.Stroke?.Material];
        foreach (var edit in MaterialEdits)
            switch (edit)
            {
                case MaterialEdit.ReplaceMaterial set:
                    values[(int)set.Slot] = set.Material is MaterialValue.PackageMaterial package ? package.WithoutUniforms() : set.Material;
                    break;
                case MaterialEdit.Uniform uniform:
                    if (values[(int)uniform.Slot] is not MaterialValue.PackageMaterial current || current.Handle != uniform.Handle)
                        throw new EffectOutputException("MaterialUniformIdentityMismatch", "uniform 写入时该部位没有匹配的固定包材质。");
                    values[(int)uniform.Slot] = current.WithUniform(uniform.NodeId, uniform.Name, uniform.Value);
                    break;
            }
        var stroke = Stroke is { } update ? update.Value : baseline.Stroke;
        if (stroke is not null && values[(int)MaterialSlot.Stroke] is { } strokeMaterial)
            stroke = new(stroke.WidthPx, strokeMaterial, stroke.Join, stroke.Position);
        return new(values[0]!, values[1]!, stroke, baseline.Shadow, baseline.Opacity);
    }
}
/// <summary>真实装载元素族的外观输出；基础字和全部注音共用。</summary>
/// <param name="Target">真实目标。</param><param name="Update">不可变外观描述。</param>
public sealed record TokenFamilyStyle(TokenFamilyTarget Target, StyleUpdate Update);
