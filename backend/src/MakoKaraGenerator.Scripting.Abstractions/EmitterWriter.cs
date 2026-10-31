using System.Collections.Immutable;

namespace MakoKaraGenerator.Scripting;

/// <summary>仅 EvaluateEmitters 有效的连续描述输出，不绘图或推进粒子。</summary>
public sealed class EmitterWriter
{
    private readonly EffectInstanceId _instance;
    private readonly EffectCapabilities _capabilities;
    private readonly Dictionary<EmitterId, EmitterBinding> _emitters = [];
    private bool _closed;
    /// <summary>宿主固定实例和声明能力；作者自行创建的 writer 不被采纳。</summary>
    public EmitterWriter(EffectInstanceId instance, EffectCapabilities capabilities)
    {
        if (!instance.IsValid) throw new ArgumentException("实例身份无效。", nameof(instance));
        if ((capabilities & ~(EffectCapabilities.Transforms | EffectCapabilities.Style | EffectCapabilities.Overlay | EffectCapabilities.ImageEffects)) != 0)
            throw new ArgumentOutOfRangeException(nameof(capabilities));
        _instance = instance; _capabilities = capabilities;
    }
    /// <summary>同一边界同身份最后描述生效；省略表示下一固定区间不发射。</summary>
    public void Set(EmitterId id, CursorEmitterDescriptor descriptor)
    {
        RequireOpen();
        if (!_capabilities.HasFlag(EffectCapabilities.Overlay)) throw new EffectOutputException("UndeclaredCapability", "发射器须声明 Overlay 能力。");
        if (!id.IsValid || descriptor is null) throw new EffectOutputException("InvalidEmitter", "发射器须有真实身份和描述。");
        descriptor.Validate(); _emitters[id] = new(_instance, id, descriptor);
    }
    /// <summary>一次结束并固定 ordinal 身份顺序。</summary>
    public ImmutableArray<EmitterBinding> Complete()
    {
        RequireOpen(); _closed = true;
        return [.. _emitters.Values.OrderBy(static item => item.Id.Value, StringComparer.Ordinal)];
    }
    /// <summary>离开或失败阶段后关闭，不发布失败输出。</summary>
    public void Invalidate() => _closed = true;
    private void RequireOpen()
    { if (_closed) throw new EffectOutputException("InvalidEmissionOutputStage", "writer 已离开 EvaluateEmitters 阶段。"); }
}
