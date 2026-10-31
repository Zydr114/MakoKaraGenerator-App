namespace MakoKaraGenerator.Scripting;

public sealed partial class FrameWriter
{
    private StyleUpdate UniformUpdate(MaterialHandle handle, MaterialSlot slot, string nodeId, string name, UniformValue value)
    {
        RequireOpen(); ArgumentNullException.ThrowIfNull(handle); ArgumentNullException.ThrowIfNull(value);
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId); ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!Enum.IsDefined(slot)) throw new EffectOutputException("InvalidMaterialSlot", "uniform 必须显式选择未唱／已唱／描边。");
        if (!_resources.Contains(handle)) throw new EffectOutputException("InvalidMaterialResource", "材质不属于本实例固定资源修订。");
        return StyleUpdate.Uniform(new(slot, handle, nodeId, name, value));
    }
    /// <summary>仅 Evaluate 内有效；不安装材质，不修改图源码、参数或工程。</summary>
    public sealed class MaterialWriter
    {
        private readonly FrameWriter _owner;
        internal MaterialWriter(FrameWriter owner) { _owner = owner; }
        /// <summary>覆盖真实元素族指定部位的一个图节点 uniform，基础字与注音共同使用。</summary>
        public void SetUniform(MaterialHandle handle, TokenFamilyTarget target, MaterialSlot slot, string nodeId, string name, UniformValue value)
            => _owner.WriteStyle(target, _owner.UniformUpdate(handle, slot, nodeId, name, value));
        /// <summary>覆盖真实前奏标记指定部位的一个图节点 uniform；两态对应 Pending／Elapsed。</summary>
        public void SetUniform(MaterialHandle handle, TextMarkTarget target, MaterialSlot slot, string nodeId, string name, UniformValue value)
            => _owner.WriteMarkStyle(target, _owner.UniformUpdate(handle, slot, nodeId, name, value));
    }
}
