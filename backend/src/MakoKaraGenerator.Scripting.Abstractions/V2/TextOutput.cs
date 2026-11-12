using System.Collections.Immutable;

namespace MakoKaraGenerator.Scripting.V2;

/// <summary>文字阶段的类型安全临时输出句柄；只写本实例输出，不读取最终全局值。</summary>
public sealed partial class TextOutput
{
    private readonly ImmutableHashSet<string> _capabilities;
    private readonly ImmutableHashSet<TextTarget> _allowed;
    private readonly ImmutableDictionary<TextTarget, GeometrySample> _tokenGeometry;
    private readonly ResourceCatalog _resources;
    private readonly Dictionary<TextTarget, Affine2> _transforms = [];
    private readonly List<TextWrite> _writes = [];
    private bool _closed;

    /// <summary>宿主建立本帧输出范围；全局组几何不授予写权，writableGroups 必须由运行父树绑定检查取得。</summary>
    public TextOutput(in TextFrame frame, ImmutableArray<string> capabilities, ImmutableArray<GroupTarget> writableGroups = default)
    {
        if (capabilities.IsDefault || capabilities.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("能力必须是明确的非空身份集合。", nameof(capabilities));
        if (frame.Source is null || frame.Presentations is null) throw new ArgumentException("文字帧不能为默认值。", nameof(frame));
        _capabilities = capabilities.ToImmutableHashSet(StringComparer.Ordinal); _resources = frame.Resources;
        var allowed = ImmutableHashSet.CreateBuilder<TextTarget>();
        var geometry = ImmutableDictionary.CreateBuilder<TextTarget, GeometrySample>();
        foreach (var token in frame.Geometry)
        {
            TextTarget family = new TextTarget.Family(token.WithRuby), basic = new TextTarget.Base(token.BaseOnly);
            if (!frame.Presentations.TokensInBinding.Any(item => item.WithRuby == token.WithRuby && item.BaseOnly == token.BaseOnly))
                throw new ArgumentException("文字几何不属于当前帧目标范围。", nameof(frame));
            allowed.Add(family); allowed.Add(basic); geometry.Add(family, token.BaseGeometry); geometry.Add(basic, token.BaseGeometry);
        }
        foreach (var cluster in frame.Clusters)
        {
            TextTarget target = new TextTarget.Cluster(cluster.Target);
            if (!frame.Presentations.TokensInBinding.Any(item => item.TokenId == cluster.Target.TokenId && item.BindingId == cluster.Target.BindingId)) continue;
            allowed.Add(target); geometry.Add(target, cluster.Geometry);
        }
        if (frame.Binding is not EffectBinding.Tokens)
            foreach (var line in frame.LineGeometry)
                if (frame.LineLoads.Any(load => load.Target == line.Target)) allowed.Add(new TextTarget.Line(line.Target));
        foreach (var mark in frame.MarkGeometry)
            if (frame.Presentations.MarksInBinding.Any(item => item.Target == mark.Target))
            {
                TextTarget target = new TextTarget.Mark(mark.Target);
                allowed.Add(target); geometry.Add(target, mark.Geometry);
            }
        foreach (var group in writableGroups.IsDefault ? [] : writableGroups)
        {
            if (group is null || !frame.GroupGeometry.Any(item => item.Target == group))
                throw new ArgumentException("组写范围必须是当前真实组几何的子集。", nameof(writableGroups));
            allowed.Add(new TextTarget.Group(group));
        }
        _allowed = allowed.ToImmutable(); _tokenGeometry = geometry.ToImmutable();
    }

    /// <summary>在目标局部逻辑空间追加完整仿射；调用 B 时累计为 B*A，不修改源布局。</summary>
    public void AppendTransform(TextTarget target, Affine2 localMatrix)
    {
        RequireOpen(); ArgumentNullException.ThrowIfNull(target);
        Require(target, TransformCapability(target));
        ValidateMatrix(localMatrix);
        if (localMatrix != Affine2.Identity && _tokenGeometry.TryGetValue(target, out var geometry) && !geometry.CanTransformIndependently)
            throw new EffectOutputException(geometry.DiagnosticCode ?? "UnsupportedGlyphPartition", "源元素与其他元素共享整形单元，不能独立变换。");
        Affine2 combined;
        try { combined = localMatrix.Compose(_transforms.GetValueOrDefault(target, Affine2.Identity)); }
        catch (ArgumentException error) { throw new EffectOutputException("InvalidTransform", "变换组合溢出：" + error.Message); }
        ValidateMatrix(combined); _transforms[target] = combined;
        _writes.Add(new TextWrite.Transform(target, localMatrix));
    }
    /// <summary>乘有限 [0,1] 不透明度因子；未写继承基线，组使用独立隔离合成。</summary>
    public void MultiplyOpacity(TextTarget target, double factor)
    {
        RequireOpen(); ArgumentNullException.ThrowIfNull(target);
        string capability = target switch
        {
            TextTarget.Family or TextTarget.Base => "token.opacity",
            TextTarget.Group => "group.opacity",
            _ => throw Unsupported(),
        };
        Require(target, capability);
        if (!double.IsFinite(factor) || factor is < 0 or > 1)
            throw new EffectOutputException("InvalidStyleOpacity", "不透明度因子必须有限且位于 [0,1]。");
        RequireIndependentStyle(target);
        _writes.Add(new TextWrite.Opacity(target, factor));
    }
    /// <summary>替换一态的填充；「两态」等于两态各写一次。包材质必须属于本实例固定资源修订。</summary>
    public void SetFill(TextTarget target, TextState state, MaterialValue material)
    {
        WriteMaterialReplacement(() =>
        {
            RequireStyle(target, "style.fill"); RequireState(state);
            ArgumentNullException.ThrowIfNull(material); ValidateMaterial(material);
            foreach (var each in States(state))
            {
                _graphBudget.ChargeMaterial(material, GraphCancellation);
                _writes.Add(new TextWrite.Fill(target, each, material));
            }
        });
    }
    /// <summary>整表替换一态的描边，由内向外 0～4 层；不传即无描边。包材质只能用在第 0 层（最内层）。</summary>
    public void SetStrokes(TextTarget target, TextState state, params StrokeValue[] strokes)
    {
        WriteMaterialReplacement(() =>
        {
            RequireStyle(target, "style.stroke"); RequireState(state); ArgumentNullException.ThrowIfNull(strokes);
            var layers = AppearanceView.ValidateStrokes([.. strokes]);
            foreach (var layer in layers) ValidateMaterial(layer.Material);
            foreach (var each in States(state))
            {
                if (layers.IsEmpty) _graphBudget.ChargeMaterial(null, GraphCancellation);
                foreach (var layer in layers) _graphBudget.ChargeMaterial(layer.Material, GraphCancellation);
                _writes.Add(new TextWrite.Strokes(target, each, layers));
            }
        });
    }
    /// <summary>整体替换一态的阴影；null 明确关闭，与未写继承不同。</summary>
    public void SetShadow(TextTarget target, TextState state, ShadowValue? value)
    {
        RequireStyle(target, "style.shadow"); RequireState(state);
        foreach (var each in States(state)) _writes.Add(new TextWrite.Shadow(target, each, value));
    }
    /// <summary>整体替换一态的外发光；null 明确关闭，与未写继承不同。</summary>
    public void SetGlow(TextTarget target, TextState state, GlowValue? value)
    {
        RequireStyle(target, "style.glow"); RequireState(state);
        foreach (var each in States(state)) _writes.Add(new TextWrite.Glow(target, each, value));
    }
    /// <summary>整体替换扫字样式（羽化与高亮，两态共享）；只用于元素族与底字，文字标记没有扫字样式。</summary>
    public void SetWipe(TextTarget target, WipeValue wipe)
    {
        RequireStyle(target, "style.wipe"); ArgumentNullException.ThrowIfNull(wipe);
        if (target is TextTarget.Mark) throw Unsupported();
        _writes.Add(new TextWrite.Wipe(target, wipe));
    }

    /// <summary>宿主在成功返回后关闭并取得整个不可变结果；只允许完成一次，作者不得自行调用。</summary>
    public TextOutputSnapshot Complete()
    {
        RequireOpen(); _closed = true;
        return _writes.Count == 0 ? TextOutputSnapshot.Empty : new([.. _writes]);
    }
    /// <summary>宿主在退出阶段或异常时废弃未发布结果；幂等，之后所有写入及完成均拒绝。</summary>
    public void Invalidate() { _closed = true; _writes.Clear(); _transforms.Clear(); }

    private void RequireOpen()
    {
        if (_closed) throw new EffectOutputException("InvalidOutputStage", "文字输出句柄已离开本次求值阶段。");
        RequireGraphTransaction();
    }
    private void Require(TextTarget target, string capability)
    {
        RequireOpen(); ArgumentNullException.ThrowIfNull(target);
        if (!_allowed.Contains(target)) throw new EffectOutputException("TargetOutsideBinding", "目标不属于本帧实例输出范围。");
        if (!_capabilities.Contains(capability)) throw new EffectOutputException("UndeclaredCapability", "包未声明能力：" + capability);
    }
    private void RequireStyle(TextTarget target, string capability)
    {
        Require(target, capability);
        if (target is not (TextTarget.Family or TextTarget.Base or TextTarget.Mark)) throw Unsupported();
        RequireIndependentStyle(target);
    }
    private void RequireIndependentStyle(TextTarget target)
    {
        if (_tokenGeometry.TryGetValue(target, out var geometry) && !geometry.CanTransformIndependently)
            throw new EffectOutputException(geometry.DiagnosticCode ?? "UnsupportedGlyphPartition", "源元素与其他元素共享整形单元，不能独立改变外观。");
    }
    private static void RequireState(TextState state)
    {
        if (!Enum.IsDefined(state)) throw new ArgumentOutOfRangeException(nameof(state));
    }
    private static readonly TextState[] UnsungOnly = [TextState.Unsung], SungOnly = [TextState.Sung], BothStates = [TextState.Unsung, TextState.Sung];
    private static TextState[] States(TextState state) => state switch
    {
        TextState.Unsung => UnsungOnly,
        TextState.Sung => SungOnly,
        _ => BothStates,
    };
    private static string TransformCapability(TextTarget target) => target switch
    {
        TextTarget.Family or TextTarget.Base => "token.affine",
        TextTarget.Line => "line.affine",
        TextTarget.Group => "group.affine",
        TextTarget.Mark => "mark.affine",
        TextTarget.Cluster => "cluster.affine",
        _ => throw Unsupported(),
    };
    private static EffectOutputException Unsupported() => new("PropertyTargetUnsupported", "本属性不支持该目标粒度。");
    private void ValidateMaterial(MaterialValue value)
    {
        if (value is MaterialValue.PackageMaterial package)
        {
            if (!_resources.Contains(package.Handle)) throw new EffectOutputException("InvalidMaterialResource", "材质不属于本实例固定包修订。");
            package.Parameters.ValidateResources(_resources);
        }
    }
    private static void ValidateMatrix(Affine2 value)
    {
        if (!value.TryInverse(out _)) throw new EffectOutputException("InvalidTransform", "变换必须有限且可逆。");
        float a = (float)value.A, b = (float)value.B, c = (float)value.C, d = (float)value.D, tx = (float)value.Tx, ty = (float)value.Ty;
        double determinant = (double)a * d - (double)b * c;
        if (!float.IsFinite(a) || !float.IsFinite(b) || !float.IsFinite(c) || !float.IsFinite(d) || !float.IsFinite(tx) || !float.IsFinite(ty)
            || !double.IsFinite(determinant) || determinant == 0
            || !float.IsFinite((float)(d / determinant)) || !float.IsFinite((float)(b / determinant))
            || !float.IsFinite((float)(c / determinant)) || !float.IsFinite((float)(a / determinant))
            || !float.IsFinite((float)(((double)c * ty - (double)d * tx) / determinant))
            || !float.IsFinite((float)(((double)b * tx - (double)a * ty) / determinant)))
            throw new EffectOutputException("InvalidTransform", "变换在后端 float 精度下必须有限且可逆。");
    }
}
