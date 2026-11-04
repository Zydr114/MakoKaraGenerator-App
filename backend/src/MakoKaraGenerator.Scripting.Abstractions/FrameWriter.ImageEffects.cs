namespace MakoKaraGenerator.Scripting;

/// <summary>图效果的真实装载目标；组须由工程或显式节点绑定授权。</summary>
public abstract record ImageEffectTarget
{
    private ImageEffectTarget() { }
    /// <summary>整行目标。</summary><param name="Target">真实行装载。</param>
    public sealed record Line(LineTarget Target) : ImageEffectTarget;
    /// <summary>基础字及关联注音族。</summary><param name="Target">真实元素族装载。</param>
    public sealed record Token(TokenFamilyTarget Target) : ImageEffectTarget;
    /// <summary>前奏提示标记。</summary><param name="Target">真实节点、提示及装载。</param>
    public sealed record Mark(TextMarkTarget Target) : ImageEffectTarget;
    /// <summary>真实容器组，包括本帧没有可见孩子的组。</summary><param name="Target">已授权的真实组节点。</param>
    public sealed record Group(GroupTarget Target) : ImageEffectTarget;
}

/// <summary>同实例同目标最后一次请求的固定描述；没有原生图像或路径。</summary>
/// <param name="Target">已校验目标。</param><param name="Graph">本实例固定包的图导出。</param>
/// <param name="Parameters">整体替换的不可变参数，不合并上一绑定。</param>
public sealed record ImageEffectBinding(ImageEffectTarget Target, GraphHandle Graph, ParameterValues Parameters)
{
    /// <summary>从完整不可变值创建绑定；不会丢失任何覆盖。</summary>
    public ImageEffectBinding(ImageEffectTarget target, GraphBindingValue value) : this(target,
        (value ?? throw new ArgumentNullException(nameof(value))).Graph, value.Parameters)
    { Uniforms = value.Uniforms; Transforms = value.Transforms; }
    /// <summary>完整节点／uniform 覆盖；默认沿用固定图值。</summary>
    public GraphUniformValues Uniforms { get; init; } = GraphUniformValues.Empty;
    /// <summary>完整 Transform 节点覆盖；默认沿用固定图值。</summary>
    public GraphTransformValues Transforms { get; init; } = GraphTransformValues.Empty;
    /// <summary>比较完整绑定值，保留参数、uniform 与 Transform 内容身份。</summary>
    public bool Equals(ImageEffectBinding? other) => ReferenceEquals(this, other) || other is not null
        && Target == other.Target && Graph == other.Graph && Parameters is not null && other.Parameters is not null
        && Parameters.ContentEquals(other.Parameters) && GraphBindingValue.UniformsEqual(Uniforms, other.Uniforms) && Equals(Transforms, other.Transforms);
    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Target, Graph, Parameters?.ContentHash() ?? 0, GraphBindingValue.UniformsHash(Uniforms), Transforms);
}

public sealed partial class FrameWriter
{
    private readonly Dictionary<ImageEffectTarget, ImageEffectBinding> _imageEffects = [];
    private void BindImageEffect(ImageEffectTarget target, GraphHandle graph, ParameterValues parameters)
    {
        RequireOpen(); ArgumentNullException.ThrowIfNull(target); ArgumentNullException.ThrowIfNull(graph); ArgumentNullException.ThrowIfNull(parameters);
        if (!_capabilities.HasFlag(EffectCapabilities.ImageEffects)) throw new EffectOutputException("UndeclaredCapability", "效果未声明 ImageEffects 能力。");
        switch (target)
        {
            case ImageEffectTarget.Token { Target: { } token } when _allowed.TryGetValue(token, out var geometry):
                if (!geometry.CanTransformIndependently)
                    throw new EffectOutputException(geometry.DiagnosticCode ?? "UnsupportedGlyphPartition", "元素共享字簇，不能独立绑定图效果。");
                break;
            case ImageEffectTarget.Line { Target: { } line } when _allowedLines.Contains(line):
                break;
            case ImageEffectTarget.Mark { Target: { } mark } when _allowedMarks.ContainsKey(mark):
                break;
            case ImageEffectTarget.Group { Target: { } group } when _allowedGroups.Contains(group):
                break;
            default: throw new EffectOutputException("TargetOutsideBinding", "图目标不是本帧实例范围内的真实装载。");
        }
        if (!_resources.Contains(graph)) throw new EffectOutputException("InvalidGraphResource", "图导出不属于本实例固定包修订。");
        parameters.ValidateResources(_resources);
        _imageEffects[target] = new(target, graph, parameters);
    }
    /// <summary>Evaluate 阶段的图描述 writer；同实例同目标整体替换，跨实例顺序由宿主保持。</summary>
    public sealed class ImageEffectWriter
    {
        private readonly FrameWriter _owner;
        internal ImageEffectWriter(FrameWriter owner) { _owner = owner; }
        /// <summary>绑定整行图；参数整体替换同目标的前次请求。</summary>
        public void Bind(LineTarget target, GraphHandle graph, ParameterValues parameters) => _owner.BindImageEffect(new ImageEffectTarget.Line(target), graph, parameters);
        /// <summary>绑定独立元素族图；包配方决定注音捕获。</summary>
        public void Bind(TokenFamilyTarget target, GraphHandle graph, ParameterValues parameters) => _owner.BindImageEffect(new ImageEffectTarget.Token(target), graph, parameters);
        /// <summary>绑定真实提示标记图。</summary>
        public void Bind(TextMarkTarget target, GraphHandle graph, ParameterValues parameters) => _owner.BindImageEffect(new ImageEffectTarget.Mark(target), graph, parameters);
        /// <summary>绑定已授权的真实组图；参数整体替换同目标的前次请求，隐藏组仍验证。</summary>
        /// <param name="target">工程或显式节点绑定授权的真实组。</param>
        /// <param name="graph">本实例固定包修订的图导出。</param>
        /// <param name="parameters">本次绑定完整的不可变参数。</param>
        public void Bind(GroupTarget target, GraphHandle graph, ParameterValues parameters) => _owner.BindImageEffect(new ImageEffectTarget.Group(target), graph, parameters);
    }
}
