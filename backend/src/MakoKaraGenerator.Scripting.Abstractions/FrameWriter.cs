using System.Collections.Immutable;

namespace MakoKaraGenerator.Scripting;

/// <summary>视觉缩放枢轴；不以含注音的联合框改变基础枢轴。</summary>
public enum Pivot
{
    /// <summary>固定基础字 Advance 框中心。</summary>
    BaseAdvanceCenter,
}

/// <summary>类型化的帧输出错误，宿主会追加实例／阶段／时刻。</summary>
public sealed class EffectOutputException : Exception
{
    /// <summary>创建输出错误。</summary>
    public EffectOutputException(string code, string message) : base(message) { Code = code; }
    /// <summary>稳定错误代码。</summary>
    public string Code { get; }
}

/// <summary>宿主指定的输出能力；未声明变换不能写变换。</summary>
[Flags]
public enum EffectCapabilities
{
    /// <summary>没有写能力。</summary>
    None = 0,
    /// <summary>非排版的视觉变换。</summary>
    Transforms = 1,
    /// <summary>不改变源排版的区域材质、装饰和不透明度。</summary>
    Style = 2,
    /// <summary>最终几何后的临时画布图元。</summary>
    Overlay = 4,
    /// <summary>固定包的图效果帧绑定；不授予额外目标范围。</summary>
    ImageEffects = 8,
}

/// <summary>某个真实元素族的逻辑局部变换。</summary>
/// <param name="Target">真实源／装载。</param><param name="LocalMatrix">基础世界矩阵之后的局部视觉矩阵。</param>
public sealed record TokenFamilyTransform(TokenFamilyTarget Target, Affine2 LocalMatrix);

/// <summary>一次纯求值的不可变输出；不会把可变 writer 交给渲染器。</summary>
public sealed partial record FrameOutput
{
    /// <summary>已经按调用次序组合的元素族变换。</summary>
    public ImmutableArray<TokenFamilyTransform> TokenTransforms { get; init; } = [];
    /// <summary>按目标及调用顺序固定的外观输出；没有可写源样式。</summary>
    public ImmutableArray<TokenFamilyStyle> TokenStyles { get; init; } = [];
    /// <summary>已经按调用顺序组合的真实文字标记变换。</summary>
    public ImmutableArray<TextMarkTransform> MarkTransforms { get; init; } = [];
    /// <summary>真实提示标记的两态和装饰输出。</summary>
    public ImmutableArray<TextMarkStyle> MarkStyles { get; init; } = [];
    /// <summary>按调用顺序组合的真实整行局部仿射。</summary>
    public ImmutableArray<LineTransform> LineTransforms { get; init; } = [];
    /// <summary>按调用顺序组合的真实容器组局部仿射。</summary>
    public ImmutableArray<GroupTransform> GroupTransforms { get; init; } = [];
    /// <summary>组共同透明度的有限非负乘数；宿主最终校验并隔离合成。</summary>
    public ImmutableArray<GroupOpacity> GroupOpacities { get; init; } = [];
    /// <summary>每目标最后一次绑定；不同实例由宿主保留各自输出并串接。</summary>
    public ImmutableArray<ImageEffectBinding> ImageEffects { get; init; } = [];
}

/// <summary>本次 Evaluate 的限时输出；完成后继续调用报告阶段错误。</summary>
public sealed partial class FrameWriter
{
    private readonly Dictionary<TokenFamilyTarget, Affine2> _transforms = [];
    private readonly Dictionary<TokenFamilyTarget, GeometrySample> _allowed;
    private readonly EffectCapabilities _capabilities;
    private bool _closed;
    /// <summary>宿主构造合法目标和能力；脚本自己构造的 writer 不会被宿主采纳。</summary>
    public FrameWriter(ImmutableArray<TokenGeometrySample> geometry, EffectCapabilities capabilities,
        BaselineStyles? baseline = null, ResourceCatalog? resources = null, ImmutableArray<TextMarkGeometrySample> markGeometry = default,
        ImmutableArray<LineGeometrySample> lineGeometry = default)
        : this(geometry, capabilities, [], baseline, resources, markGeometry, lineGeometry) { }
    /// <summary>宿主额外提供经显式工程／节点绑定筛选的真实组写范围。</summary>
    public FrameWriter(ImmutableArray<TokenGeometrySample> geometry, EffectCapabilities capabilities,
        ImmutableArray<GroupGeometrySample> groupGeometry, BaselineStyles? baseline = null, ResourceCatalog? resources = null,
        ImmutableArray<TextMarkGeometrySample> markGeometry = default, ImmutableArray<LineGeometrySample> lineGeometry = default)
    {
        if ((capabilities & ~(EffectCapabilities.Transforms | EffectCapabilities.Style | EffectCapabilities.Overlay | EffectCapabilities.ImageEffects)) != 0) throw new ArgumentOutOfRangeException(nameof(capabilities));
        _capabilities = capabilities;
        _allowed = geometry.ToDictionary(static item => item.WithRuby, static item => item.BaseGeometry);
        _allowedMarks = (markGeometry.IsDefault ? [] : markGeometry).ToDictionary(static item => item.Target, static item => item.Geometry);
        _allowedLines = [];
        foreach (var line in lineGeometry.IsDefault ? [] : lineGeometry)
            if (line is null || line.Target is null || !line.Target.LineId.IsValid || !line.Target.BindingId.IsValid || !_allowedLines.Add(line.Target))
                throw new ArgumentException("整行输出范围必须包含唯一真实装载。", nameof(lineGeometry));
        _allowedGroups = [];
        foreach (var group in groupGeometry.IsDefault ? [] : groupGeometry)
            if (group is null || group.Target is null || !group.Target.NodeId.IsValid || !_allowedGroups.Add(group.Target))
                throw new ArgumentException("组输出范围必须包含唯一真实组节点。", nameof(groupGeometry));
        Transforms = new(this);
        _baseline = baseline ?? BaselineStyles.Empty; _resources = resources ?? ResourceCatalog.Empty; Style = new(this);
        ImageEffects = new(this);
        Materials = new(this);
    }
    /// <summary>只写视觉变换，不写字号或场景树。</summary>
    public TransformWriter Transforms { get; }
    /// <summary>限于真实目标的外观描述，不能修改排版或源数据。</summary>
    public StyleWriter Style { get; }
    /// <summary>只写真实目标和固定资源的图绑定，完成后不可复用。</summary>
    public ImageEffectWriter ImageEffects { get; }
    /// <summary>限时写入显式部位／图节点的固定材质 uniform。</summary>
    public MaterialWriter Materials { get; }
    /// <summary>宿主结束求值并取得不可变结果；只能结束一次。</summary>
    public FrameOutput Complete()
    {
        RequireOpen(); _closed = true;
        return new() { TokenTransforms = [.. _transforms.Select(static pair => new TokenFamilyTransform(pair.Key, pair.Value))],
            TokenStyles = [.. _styles.Select(static pair => new TokenFamilyStyle(pair.Key, pair.Value))],
            MarkTransforms = [.. _markTransforms.Select(static pair => new TextMarkTransform(pair.Key, pair.Value))],
            MarkStyles = [.. _markStyles.Select(static pair => new TextMarkStyle(pair.Key, pair.Value))],
            LineTransforms = [.. _lineTransforms.Select(static pair => new LineTransform(pair.Key, pair.Value))],
            GroupTransforms = [.. _groupTransforms.Select(static pair => new GroupTransform(pair.Key, pair.Value))],
            GroupOpacities = [.. _groupOpacities.Select(static pair => new GroupOpacity(pair.Key, pair.Value))],
            ImageEffects = [.. _imageEffects.Values] };
    }
    /// <summary>宿主在失败或离开求值时终止 writer；幂等，不发布失败阶段的输出。</summary>
    public void Invalidate() => _closed = true;
    private void RequireOpen()
    {
        if (_closed) throw new EffectOutputException("InvalidOutputStage", "writer 已离开 Evaluate 阶段。");
    }
    private void Scale(TokenFamilyTarget target, double factor, Pivot pivot)
    {
        RequireOpen(); ArgumentNullException.ThrowIfNull(target);
        if (!_capabilities.HasFlag(EffectCapabilities.Transforms))
            throw new EffectOutputException("UndeclaredCapability", "效果未声明 Transforms 能力。");
        if (!double.IsFinite(factor) || factor <= 0 || !Enum.IsDefined(pivot))
            throw new EffectOutputException("InvalidTransform", "视觉比例必须有限且大于零，并指定有效枢轴。");
        if (!_allowed.TryGetValue(target, out var geometry))
            throw new EffectOutputException("TargetOutsideBinding", "目标不属于本帧实例输出范围：" + target.TokenId.Value);
        // 恒等变换无需独立字簇；也不记录无变化输出。
        if (factor == 1) return;
        if (!geometry.CanTransformIndependently)
            throw new EffectOutputException(geometry.DiagnosticCode ?? "UnsupportedGlyphPartition", "源元素共享字簇，不能独立变形：" + target.TokenId.Value);
        try
        {
            Affine2 local = Affine2.ScaleAt(factor, geometry.AdvanceBounds.Center);
            Affine2 combined = _transforms.GetValueOrDefault(target, Affine2.Identity).Compose(local);
            // 不允许缩到浮点零或失去有限可逆性。
            if (!combined.TryInverse(out _)) throw new ArgumentOutOfRangeException(nameof(factor));
            _transforms[target] = combined;
        }
        catch (ArgumentException exception)
        {
            throw new EffectOutputException("InvalidTransform", "变换组合溢出或不可逆：" + exception.Message);
        }
    }
    /// <summary>限于固定目标的视觉变换 writer。</summary>
    public sealed class TransformWriter
    {
        private readonly FrameWriter _owner;
        internal TransformWriter(FrameWriter owner) { _owner = owner; }
        /// <summary>围绕基础字 Advance 中心缩放字形及全部注音；不重排相邻文字。</summary>
        public void ScaleTokenFamily(TokenFamilyTarget target, double factor, Pivot pivot) => _owner.Scale(target, factor, pivot);
        /// <summary>在真实提示标记的逻辑空间应用有限可逆仿射，不修改源节点。</summary>
        public void ApplyAffine(TextMarkTarget target, Affine2 matrix) => _owner.TransformMark(target, matrix);
        /// <summary>在整行固定逻辑空间应用仿射；关联注音和运行后代跟随，不改源排版。</summary>
        public void ApplyAffine(LineTarget target, Affine2 matrix) => _owner.TransformLine(target, matrix);
        /// <summary>在真实容器组参考空间应用仿射；运行后代自然跟随，不修改源树。</summary>
        public void ApplyAffine(GroupTarget target, Affine2 matrix) => _owner.TransformGroup(target, matrix);
    }
}
