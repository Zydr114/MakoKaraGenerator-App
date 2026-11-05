using System.Runtime.ExceptionServices;

namespace MakoKaraGenerator.Scripting.V2;

public sealed partial class TextOutput
{
    /// <summary>写入最终安装材质部位的具名 uniform；完整材质替换会清除先前覆盖作用域。</summary>
    public void SetMaterialUniform(TextTarget target, MaterialSlot slot, MaterialHandle material, string nodeId, string name, UniformValue value)
    {
        RequireOpen();
        try
        {
            ArgumentNullException.ThrowIfNull(target); ArgumentNullException.ThrowIfNull(material); ArgumentNullException.ThrowIfNull(value);
            if (string.IsNullOrWhiteSpace(nodeId) || string.IsNullOrWhiteSpace(name))
                throw new EffectOutputException("InvalidMaterialUniformMetadata", "节点与 uniform 名须为明确非空身份。");
            if (!Enum.IsDefined(slot)) throw new EffectOutputException("InvalidMaterialSlot", "uniform 须明确选择材质部位。");
            Require(target, "material.uniform");
            if (target is not (TextTarget.Family or TextTarget.Base or TextTarget.Mark))
                throw new EffectOutputException("MaterialUniformTargetPending", "此粒度的独立包材质 uniform 执行尚未接通。");
            RequireIndependentStyle(target);
            if (!_resources.Contains(material)) throw new EffectOutputException("InvalidMaterialResource", "材质不属于本实例固定包修订。");
            _graphBudget.ChargeMaterialUniform(material, nodeId, name, value, GraphCancellation);
            _writes.Add(new TextWrite.MaterialUniform(target, slot, material, nodeId, name, value));
        }
        catch (Exception error) { _graphFailure = ExceptionDispatchInfo.Capture(error); throw; }
    }

    // Shared with Fill/Stroke replacement. Charge before publishing every write,
    // including values that a later replacement removes from the final scope.
    private void WriteMaterialReplacement(Action action)
    {
        RequireOpen();
        try
        {
            action();
        }
        catch (Exception error) { _graphFailure = ExceptionDispatchInfo.Capture(error); throw; }
    }
}

public abstract partial record TextWrite
{
    /// <summary>固定 owning 材质、部位与节点 uniform；保留完整原始写入直到最终身份归约。</summary>
    public sealed record MaterialUniform : TextWrite
    {
        internal MaterialUniform(TextTarget target, MaterialSlot slot, MaterialHandle material, string nodeId, string name, UniformValue value) : base(target)
        { Slot = slot; Material = material; NodeId = nodeId; Name = name; Value = value; }
        /// <summary>真实材质部位。</summary>
        public MaterialSlot Slot { get; }
        /// <summary>必须与最终安装材质匹配的固定身份。</summary>
        public MaterialHandle Material { get; }
        /// <summary>最终 RuntimeShader 节点。</summary>
        public string NodeId { get; }
        /// <summary>具名 uniform。</summary>
        public string Name { get; }
        /// <summary>不可变 intrinsic 合法值。</summary>
        public UniformValue Value { get; }
    }
}
