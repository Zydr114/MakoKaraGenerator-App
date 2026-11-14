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
