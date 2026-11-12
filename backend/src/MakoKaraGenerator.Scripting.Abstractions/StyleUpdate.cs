using System.Collections.Immutable;

namespace MakoKaraGenerator.Scripting;

/// <summary>一次显式描边写入（整表替换）；空表表示无描边，不等于没有写入。</summary>
public sealed record StrokesUpdate
{
    /// <summary>由内向外 0～4 层；包材质只能在第 0 层。</summary>
    public StrokesUpdate(ImmutableArray<StrokeValue> layers) { Layers = AppearanceView.ValidateStrokes(layers); }
    /// <summary>新的描边，由内向外。</summary>
    public ImmutableArray<StrokeValue> Layers { get; }
    /// <summary>逐层比较。</summary>
    public bool Equals(StrokesUpdate? other) => ReferenceEquals(this, other) || other is not null && Layers.SequenceEqual(other.Layers);
    /// <inheritdoc/>
    public override int GetHashCode()
    {
        HashCode hash = new();
        foreach (var layer in Layers) hash.Add(layer);
        return hash.ToHashCode();
    }
}
/// <summary>一次显式阴影写入；Value=null 表示关闭。</summary>
/// <param name="Value">新阴影或明确关闭。</param>
public sealed record ShadowUpdate(ShadowValue? Value);
/// <summary>一次显式辉光写入；Value=null 表示关闭，不等于没有写入。</summary>
/// <param name="Value">新辉光或明确关闭。</param>
public sealed record GlowUpdate(GlowValue? Value);

/// <summary>一态外观的写入；为空的成员表示未写，继承基线。</summary>
public sealed record AppearanceUpdate
{
    /// <summary>空字段为未写入；填充禁用使用透明 Solid，装饰用显式 update(null) 或空描边表。</summary>
    public AppearanceUpdate(MaterialValue? fill = null, StrokesUpdate? strokes = null, ShadowUpdate? shadow = null, GlowUpdate? glow = null)
    {
        Fill = fill; Strokes = strokes; Shadow = shadow; Glow = glow;
    }
    /// <summary>什么都不写。</summary>
    public static AppearanceUpdate None { get; } = new();
    /// <summary>填充写入，空为不写。</summary>
    public MaterialValue? Fill { get; }
    /// <summary>描边写入，空为不写。</summary>
    public StrokesUpdate? Strokes { get; }
    /// <summary>阴影写入，空为不写。</summary>
    public ShadowUpdate? Shadow { get; }
    /// <summary>辉光写入，空为不写。</summary>
    public GlowUpdate? Glow { get; }
    /// <summary>按成员组合，后项覆盖前项。</summary>
    public AppearanceUpdate Then(AppearanceUpdate next)
    {
        ArgumentNullException.ThrowIfNull(next);
        if (ReferenceEquals(next, None)) return this;
        return ReferenceEquals(this, None) ? next : new(next.Fill ?? Fill, next.Strokes ?? Strokes, next.Shadow ?? Shadow, next.Glow ?? Glow);
    }
    /// <summary>写入是否改变给定一态的外观。</summary>
    internal bool Changes(AppearanceView basis) => Fill is { } fill && fill != basis.Fill
        || Strokes is { } strokes && !strokes.Layers.SequenceEqual(basis.Strokes)
        || Shadow is { } shadow && shadow.Value != basis.Shadow || Glow is { } glow && glow.Value != basis.Glow;
    /// <summary>写入里出现的全部材质（填充与各层描边）。</summary>
    internal IEnumerable<MaterialValue> Materials()
    {
        if (Fill is { } fill) yield return fill;
        if (Strokes is { } strokes) foreach (var layer in strokes.Layers) yield return layer.Material;
    }
}

/// <summary>同一目标的帧外观描述：两态各自的外观写入、扫字样式与不透明度系数；Set 后项覆盖，alpha 系数按顺序相乘。</summary>
/// <remarks>两态互不影响：只写未唱时已唱不跟着变（P3 设计 §9）。</remarks>
public sealed record StyleUpdate
{
    /// <summary>空的一态为不写；扫字样式为空为不写。</summary>
    public StyleUpdate(AppearanceUpdate? unsung = null, AppearanceUpdate? sung = null, double opacityFactor = 1, WipeValue? wipe = null)
    {
        Point2.Finite(opacityFactor); ArgumentOutOfRangeException.ThrowIfNegative(opacityFactor);
        Unsung = unsung ?? AppearanceUpdate.None; Sung = sung ?? AppearanceUpdate.None; OpacityFactor = opacityFactor; Wipe = wipe;
        var edits = ImmutableArray.CreateBuilder<MaterialEdit>();
        if (Unsung.Fill is { } unsungFill) edits.Add(new MaterialEdit.ReplaceMaterial(MaterialSlot.UnsungFill, unsungFill));
        if (Sung.Fill is { } sungFill) edits.Add(new MaterialEdit.ReplaceMaterial(MaterialSlot.SungFill, sungFill));
        if (Unsung.Strokes is { } unsungStrokes) edits.Add(new MaterialEdit.ReplaceMaterial(MaterialSlot.UnsungStroke, Innermost(unsungStrokes)));
        if (Sung.Strokes is { } sungStrokes) edits.Add(new MaterialEdit.ReplaceMaterial(MaterialSlot.SungStroke, Innermost(sungStrokes)));
        MaterialEdits = edits.ToImmutable();
        static MaterialValue? Innermost(StrokesUpdate strokes) => strokes.Layers.IsEmpty ? null : strokes.Layers[0].Material;
    }
    /// <summary>两态写同一份外观（两态各写一次）。</summary>
    public static StyleUpdate Both(AppearanceUpdate appearance)
    {
        ArgumentNullException.ThrowIfNull(appearance);
        return new(appearance, appearance);
    }
    /// <summary>未唱一态的写入。</summary>
    public AppearanceUpdate Unsung { get; }
    /// <summary>已唱一态的写入。</summary>
    public AppearanceUpdate Sung { get; }
    /// <summary>扫字样式写入，空为不写。</summary>
    public WipeValue? Wipe { get; }
    /// <summary>非负有限乘数，最终样式 alpha 仍必须落在 [0,1]。</summary>
    public double OpacityFactor { get; }
    /// <summary>按调用顺序保存的材质 Set／uniform；跨实例合并保留真实顺序。</summary>
    public ImmutableArray<MaterialEdit> MaterialEdits { get; private init; }
    internal static StyleUpdate Uniform(MaterialEdit.Uniform edit) => new() { MaterialEdits = [edit] };
    /// <summary>写入里出现的全部材质（两态的填充与各层描边）。</summary>
    internal IEnumerable<MaterialValue> Materials() => Unsung.Materials().Concat(Sung.Materials());
    /// <summary>按原有外观和完整操作内容比较，重放不因数组引用不同变成不同帧。</summary>
    public bool Equals(StyleUpdate? other) => ReferenceEquals(this, other) || other is not null
        && Unsung == other.Unsung && Sung == other.Sung && Wipe == other.Wipe && OpacityFactor == other.OpacityFactor
        && MaterialEdits.SequenceEqual(other.MaterialEdits);
    /// <inheritdoc/>
    public override int GetHashCode()
    {
        HashCode hash = new(); hash.Add(Unsung); hash.Add(Sung); hash.Add(Wipe); hash.Add(OpacityFactor);
        foreach (var edit in MaterialEdits) hash.Add(edit);
        return hash.ToHashCode();
    }
    /// <summary>按调用／实例顺序组合；不把后项基线读取替换成前项结果。</summary>
    public StyleUpdate Then(StyleUpdate next)
    {
        ArgumentNullException.ThrowIfNull(next); double factor = OpacityFactor * next.OpacityFactor;
        if (!double.IsFinite(factor)) throw new EffectOutputException("InvalidStyleOpacity", "样式透明度系数组合溢出。");
        return new(Unsung.Then(next.Unsung), Sung.Then(next.Sung), factor, next.Wipe ?? Wipe) { MaterialEdits = MaterialEdits.AddRange(next.MaterialEdits) };
    }
    /// <summary>对静态基线求最终外观；最终 alpha 越界报错，不夹值。</summary>
    public StyleView Apply(StyleView baseline)
    {
        ArgumentNullException.ThrowIfNull(baseline); double opacity = baseline.Opacity * OpacityFactor;
        if (!double.IsFinite(opacity) || opacity is < 0 or > 1) throw new EffectOutputException("InvalidStyleOpacity", "最终样式不透明度必须在 [0,1]。");
        var material = ApplyMaterials(baseline);
        return new(Decorate(material.Unsung, Unsung), Decorate(material.Sung, Sung), opacity, Wipe ?? baseline.Wipe);
        static AppearanceView Decorate(AppearanceView view, AppearanceUpdate update) => update.Shadow is null && update.Glow is null ? view
            : new(view.Fill, view.Strokes, update.Shadow is { } shadow ? shadow.Value : view.Shadow, update.Glow is { } glow ? glow.Value : view.Glow);
    }
    /// <summary>按顺序解析材质身份与覆盖；保留基线 alpha／阴影／辉光／扫字样式，最终 alpha 在全部实例合并后校验。</summary>
    public StyleView ApplyMaterials(StyleView baseline)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        MaterialValue?[] values = [baseline.Material(MaterialSlot.UnsungFill), baseline.Material(MaterialSlot.SungFill),
            baseline.Material(MaterialSlot.UnsungStroke), baseline.Material(MaterialSlot.SungStroke)];
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
        return new(State(baseline.Unsung, Unsung, values[(int)MaterialSlot.UnsungFill]!, values[(int)MaterialSlot.UnsungStroke]),
            State(baseline.Sung, Sung, values[(int)MaterialSlot.SungFill]!, values[(int)MaterialSlot.SungStroke]), baseline.Opacity, baseline.Wipe);

        static AppearanceView State(AppearanceView basis, AppearanceUpdate update, MaterialValue fill, MaterialValue? innermost)
        {
            var strokes = update.Strokes is { } written ? written.Layers : basis.Strokes;
            if (!strokes.IsEmpty && innermost is not null && innermost != strokes[0].Material)
                strokes = strokes.SetItem(0, new(strokes[0].WidthPx, innermost, strokes[0].Join));
            return fill == basis.Fill && strokes == basis.Strokes ? basis : new(fill, strokes, basis.Shadow, basis.Glow);
        }
    }
}
/// <summary>真实装载元素族的外观输出；基础字和全部注音共用。</summary>
/// <param name="Target">真实目标。</param><param name="Update">不可变外观描述。</param>
public sealed record TokenFamilyStyle(TokenFamilyTarget Target, StyleUpdate Update);
