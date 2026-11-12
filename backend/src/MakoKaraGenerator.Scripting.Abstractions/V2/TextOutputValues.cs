using System.Collections.Immutable;

namespace MakoKaraGenerator.Scripting.V2;

/// <summary>SDK v2 的封闭文字目标；沿用既有源／装载身份，不构造另一套目标编号。</summary>
public abstract record TextTarget
{
    private TextTarget() { }
    /// <summary>基础字和其全部关联注音。</summary>
    public sealed record Family(TokenFamilyTarget Value) : TextTarget;
    /// <summary>显式只包含基础字。</summary>
    public sealed record Base(BaseTokenTarget Value) : TextTarget;
    /// <summary>一次真实整行装载，包含关联注音。</summary>
    public sealed record Line(LineTarget Value) : TextTarget;
    /// <summary>真实容器组。</summary>
    public sealed record Group(GroupTarget Value) : TextTarget;
    /// <summary>一次真实文字标记装载。</summary>
    public sealed record Mark(TextMarkTarget Value) : TextTarget;
    /// <summary>整行唯一整形结果中的真实不可拆基础字或 Ruby 单元。</summary>
    public sealed record Cluster(TextClusterTarget Value) : TextTarget;

    /// <summary>保留字族目标的真实身份。</summary>
    public static implicit operator TextTarget(TokenFamilyTarget value) => new Family(value);
    /// <summary>保留仅基础字目标的真实身份。</summary>
    public static implicit operator TextTarget(BaseTokenTarget value) => new Base(value);
    /// <summary>保留整行装载的真实身份。</summary>
    public static implicit operator TextTarget(LineTarget value) => new Line(value);
    /// <summary>保留容器组的真实身份。</summary>
    public static implicit operator TextTarget(GroupTarget value) => new Group(value);
    /// <summary>保留提示文字标记的真实身份。</summary>
    public static implicit operator TextTarget(TextMarkTarget value) => new Mark(value);
    /// <summary>保留真实字簇及其所属族／注音身份。</summary>
    public static implicit operator TextTarget(TextClusterTarget value) => new Cluster(value);
}

/// <summary>样式写入作用的演唱状态；未写的一态继承源基线。</summary>
public enum TextState
{
    /// <summary>未唱；文字标记对应 Pending。</summary>
    Unsung,
    /// <summary>已唱；文字标记对应 Elapsed。</summary>
    Sung,
    /// <summary>两态：等于未唱、已唱各写一次。</summary>
    Both,
}

/// <summary>一条已校验的不可变文字输出；数组顺序即作者调用顺序，合成不得任意排序。</summary>
public abstract partial record TextWrite
{
    private protected TextWrite(TextTarget target) { Target = target; }
    /// <summary>已经验证属于本帧输出范围的真实目标。</summary>
    public TextTarget Target { get; }

    /// <summary>在目标局部逻辑空间追加仿射；新 B 作用于既有 A，组合为 B*A。</summary>
    public sealed record Transform : TextWrite
    {
        internal Transform(TextTarget target, Affine2 localMatrix) : base(target) { LocalMatrix = localMatrix; }
        /// <summary>本次追加的有限且后端 float 精度可逆矩阵。</summary>
        public Affine2 LocalMatrix { get; }
    }
    /// <summary>独立的不透明度因子；组由隔离合成处理，不逐字重复相乘。</summary>
    public sealed record Opacity : TextWrite
    {
        internal Opacity(TextTarget target, double factor) : base(target) { Factor = factor; }
        /// <summary>有限 [0,1] 因子。</summary>
        public double Factor { get; }
    }
    /// <summary>一态的填充，后写覆盖。</summary>
    public sealed record Fill : TextWrite
    {
        internal Fill(TextTarget target, TextState state, MaterialValue material) : base(target) { State = state; Material = material; }
        /// <summary>未唱或已唱；「两态」在写入时已展开成两条。</summary>
        public TextState State { get; }
        /// <summary>完整固定材质。</summary>
        public MaterialValue Material { get; }
    }
    /// <summary>一态的描边整表替换；空表为无描边，未写继承。</summary>
    public sealed record Strokes : TextWrite
    {
        internal Strokes(TextTarget target, TextState state, ImmutableArray<StrokeValue> layers) : base(target) { State = state; Layers = layers; }
        /// <summary>未唱或已唱；「两态」在写入时已展开成两条。</summary>
        public TextState State { get; }
        /// <summary>由内向外 0～4 层。</summary>
        public ImmutableArray<StrokeValue> Layers { get; }
        /// <summary>逐层比较。</summary>
        public bool Equals(Strokes? other) => ReferenceEquals(this, other) || other is not null && Target == other.Target
            && State == other.State && Layers.SequenceEqual(other.Layers);
        /// <inheritdoc/>
        public override int GetHashCode()
        {
            HashCode hash = new(); hash.Add(Target); hash.Add(State);
            foreach (var layer in Layers) hash.Add(layer);
            return hash.ToHashCode();
        }
    }
    /// <summary>一态的阴影整体替换；null 明确关闭，未写继承。</summary>
    public sealed record Shadow : TextWrite
    {
        internal Shadow(TextTarget target, TextState state, ShadowValue? value) : base(target) { State = state; Value = value; }
        /// <summary>未唱或已唱；「两态」在写入时已展开成两条。</summary>
        public TextState State { get; }
        /// <summary>新的完整阴影或 null。</summary>
        public ShadowValue? Value { get; }
    }
    /// <summary>一态的外发光整体替换；null 明确关闭，未写继承。</summary>
    public sealed record Glow : TextWrite
    {
        internal Glow(TextTarget target, TextState state, GlowValue? value) : base(target) { State = state; Value = value; }
        /// <summary>未唱或已唱；「两态」在写入时已展开成两条。</summary>
        public TextState State { get; }
        /// <summary>新的完整外发光或 null。</summary>
        public GlowValue? Value { get; }
    }
    /// <summary>扫字样式整体替换（两态共享）。</summary>
    public sealed record Wipe : TextWrite
    {
        internal Wipe(TextTarget target, WipeValue value) : base(target) { Value = value; }
        /// <summary>新的扫字样式。</summary>
        public WipeValue Value { get; }
    }
}

/// <summary>一次成功文字阶段的不可变事务结果；失败阶段不会产生可发布的部分结果。</summary>
public sealed record TextOutputSnapshot
{
    internal TextOutputSnapshot(ImmutableArray<TextWrite> writes) { Writes = writes; }
    /// <summary>完整调用顺序的已校验输出，宿主按目标层级与实例顺序消费。</summary>
    public ImmutableArray<TextWrite> Writes { get; }
    /// <summary>没有写入的真实空结果。</summary>
    public static TextOutputSnapshot Empty { get; } = new([]);
}
