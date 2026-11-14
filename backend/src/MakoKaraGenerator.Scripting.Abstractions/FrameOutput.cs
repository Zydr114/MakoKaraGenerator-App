using System.Collections.Immutable;

namespace MakoKaraGenerator.Scripting;

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

/// <summary>当前真实整行的局部视觉仿射，包含关联注音，不改排版。</summary>
/// <param name="Target">真实行和装载。</param><param name="LocalMatrix">LineGeometry 的逻辑坐标矩阵。</param>
public sealed record LineTransform(LineTarget Target, Affine2 LocalMatrix);

/// <summary>真实容器组的局部视觉仿射，不改源布局或显示调度。</summary>
/// <param name="Target">真实组节点。</param><param name="LocalMatrix">组参考框逻辑空间的矩阵。</param>
public sealed record GroupTransform(GroupTarget Target, Affine2 LocalMatrix);

/// <summary>组的共同透明度乘数；最终范围由宿主在全部实例组合后校验。</summary>
/// <param name="Target">真实组节点。</param><param name="Factor">有限非负乘数，不包含孩子自身透明度。</param>
public sealed record GroupOpacity(GroupTarget Target, double Factor);

/// <summary>一次纯求值的不可变输出；不会把可变对象交给渲染器。</summary>
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
