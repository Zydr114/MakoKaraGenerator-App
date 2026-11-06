using System.Collections.Immutable;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Rendering.Abstractions;

namespace MakoKaraGenerator.Scripting.Execution;

/// <summary>固定 120 Hz 与精确源事件的独立语义会话；不由 UI 帧推进作者状态。</summary>
public sealed class TimelineFrameSourceFactory : IEvaluatedFrameSourceFactory
{
    private readonly FrameSessionFactory _factory;
    /// <summary>固定闭包；更改输入或重放原因须新建工厂／会话，指纹须覆盖订阅、音频修订与 required 声明及视觉／粒子预算。</summary>
    public TimelineFrameSourceFactory(ImmutableArray<CompiledFrameEffect> effects, string inputsFingerprint,
        Func<ILineGeometryProvider> geometryFactory, ReplayReason reason, AudioFeatures? audio = null, VisualOutputBudget? visualBudget = null, ParticleSimulationBudget? particleBudget = null, PreparedSourceMaterials? sourceMaterials = null, ProjectMediaFeatureBinding? mediaBinding = null)
    {
        if (!Enum.IsDefined(reason)) throw new ArgumentOutOfRangeException(nameof(reason));
        _factory = new(effects, inputsFingerprint, geometryFactory, audio, reason, visualBudget, particleBudget, sourceMaterials, mediaBinding: mediaBinding);
    }
    /// <inheritdoc/>
    public ValueTask<IEvaluatedFrameSource> CreateAsync(ProjectSnapshot snapshot, CancellationToken cancellationToken = default)
        => _factory.CreateAsync(snapshot, cancellationToken);
}

/// <summary>R0 已接受的整数微秒 120 Hz 网格；不随渲染帧率变化。</summary>
public static class SemanticSimulationGrid
{
    /// <summary>第 n 个点 floor(n×1000000/120)，避免中间乘法溢出。</summary>
    public static long TimeAt(long index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        return checked(index / 120 * 1_000_000 + index % 120 * 1_000_000 / 120);
    }
    /// <summary>不晚于目标时刻的最后网格索引；含 floor 造成的整数边界。</summary>
    public static long IndexAtOrBefore(long timeUs)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(timeUs);
        return checked(timeUs / 1_000_000 * 120 + ((timeUs % 1_000_000 + 1) * 120 - 1) / 1_000_000);
    }
}
