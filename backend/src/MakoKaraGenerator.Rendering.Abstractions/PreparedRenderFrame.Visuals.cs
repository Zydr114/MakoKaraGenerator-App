using System.Collections.Immutable;
using MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering;

public sealed partial class PreparedRenderFrame
{
    /// <summary>全部图元依稳定实例／批次顺序固定，共用名字和预算。</summary>
    public ImmutableArray<OverlayBatch> OverlayBatches { get; } = [];
    /// <summary>图元作者的固定编译与包身份，供后端错误保留真实来源。</summary>
    public ImmutableArray<PreparedOverlayOwner> OverlayOwners { get; private init; } = [];
    /// <summary>完整输出中的圆点视图，不丢弃其他图元。</summary>
    public ImmutableArray<DotBatch> DotBatches { get; } = [];
    /// <summary>本帧宿主管理连续粒子的不可变真实视图。</summary>
    public ImmutableArray<ParticleSample> Particles { get; } = [];
    /// <summary>最近完成固定区间的全部真实出生，纯帧没有该服务。</summary>
    public EmissionSchedule? EmissionSchedule { get; }
    /// <summary>本帧固定的全实例绘制预算，不因预览密度自动减少。</summary>
    public VisualOutputBudget VisualBudget { get; } = VisualOutputBudget.Default;
    /// <summary>附加已完成的视觉输出和诊断，保留文字／几何闭包的全部引用。</summary>
    public PreparedRenderFrame WithVisualOutput(ImmutableArray<DotBatch> batches, ImmutableArray<RuntimeDiagnostic> diagnostics,
        VisualOutputBudget? budget = null, ImmutableArray<ParticleSample> particles = default, EmissionSchedule? emissionSchedule = null)
        => WithOverlayOutput(batches.IsDefault ? default : [.. batches.Cast<OverlayBatch>()], diagnostics, budget, particles, emissionSchedule);
    /// <summary>附加封闭图元协议，保留完整文字、几何及资源闭包。</summary>
    public PreparedRenderFrame WithOverlayOutput(ImmutableArray<OverlayBatch> batches, ImmutableArray<RuntimeDiagnostic> diagnostics,
        VisualOutputBudget? budget = null, ImmutableArray<ParticleSample> particles = default, EmissionSchedule? emissionSchedule = null,
        ImmutableArray<PreparedOverlayOwner> owners = default)
        => WithOverlaySnapshot(batches, OverlayDiagnostics(diagnostics),
            budget, particles, emissionSchedule, owners);
    private RuntimeDiagnosticSnapshot OverlayDiagnostics(ImmutableArray<RuntimeDiagnostic> diagnostics)
    {
        if (diagnostics.IsDefault || diagnostics.Any(static item => item is null || item.Diagnostic is null
            || item.Diagnostic.Severity == DiagnosticSeverity.Error))
            throw new PreparedFrameException("InvalidPreparedDiagnostics", "成功视觉帧不能包含无效或 Error 诊断。");
        return diagnostics.SequenceEqual(Diagnostics) ? DiagnosticSnapshot : RuntimeDiagnosticSnapshot.FromArray(diagnostics);
    }
    /// <summary>宿主附加圆点输出与稳定发生快照，不读取完整兼容诊断数组。</summary>
    public PreparedRenderFrame WithVisualSnapshot(ImmutableArray<DotBatch> batches, RuntimeDiagnosticSnapshot diagnostics,
        VisualOutputBudget? budget = null, ImmutableArray<ParticleSample> particles = default, EmissionSchedule? emissionSchedule = null)
        => WithOverlaySnapshot(batches.IsDefault ? default : [.. batches.Cast<OverlayBatch>()], diagnostics, budget, particles, emissionSchedule);
    /// <summary>宿主附加封闭图元及完整稳定诊断日志。</summary>
    public PreparedRenderFrame WithOverlaySnapshot(ImmutableArray<OverlayBatch> batches, RuntimeDiagnosticSnapshot diagnostics,
        VisualOutputBudget? budget = null, ImmutableArray<ParticleSample> particles = default, EmissionSchedule? emissionSchedule = null,
        ImmutableArray<PreparedOverlayOwner> owners = default)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        budget ??= VisualOutputBudget.Default;
        if (particles.IsDefault) particles = Particles;
        emissionSchedule ??= EmissionSchedule;
        if (owners.IsDefault) owners = OverlayOwners;
        if (owners.Any(owner => owner is null || !owner.InstanceId.IsValid || string.IsNullOrWhiteSpace(owner.ArtifactHash))
            || owners.Select(owner => owner.InstanceId).Distinct().Count() != owners.Length)
            throw new PreparedFrameException("InvalidPreparedOverlayOwner", "图元作者来源须有唯一真实实例与编译身份。");
        if (particles.Any(item => item is null || !Enum.IsDefined(item.Layer) || !item.Id.InstanceId.IsValid || !item.Id.EmitterId.IsValid || item.Source is null || item.EventId is { IsValid: false } || item.BirthUs < 0
            || item.DeathUs <= TimeUs || item.BirthUs > TimeUs || item.Trail.IsDefault || !double.IsFinite(item.SizePx) || item.SizePx < 0)
            || particles.Select(item => item.Id).Distinct().Count() != particles.Length)
            throw new PreparedFrameException("InvalidPreparedParticles", "粒子身份、时间、样本或尺寸无效。");
        if (batches.IsDefault) throw new PreparedFrameException("InvalidPreparedOverlay", "图元批次不能是 default。");
        if (diagnostics.HasError)
            throw new PreparedFrameException("InvalidPreparedDiagnostics", "成功视觉帧不能包含无效或 Error 诊断。");
        HashSet<(EffectInstanceId, OutputId)> identities = [];
        long count = 0, bytes = 0;
        foreach (var batch in batches)
        {
            if (batch is null || !batch.InstanceId.IsValid || !batch.Id.IsValid || !Enum.IsDefined(batch.Layer)
                || !batch.IsValid || !identities.Add((batch.InstanceId, batch.Id)))
                throw new PreparedFrameException("InvalidPreparedOverlay", "图元批次身份／层级／数组或数值无效。");
            count = checked(count + batch.ItemCount);
            if (count > budget.DrawItemLimit)
                throw new PreparedFrameException("VisualOutputBudgetExceeded", $"全部实例绘制项请求 {count}，限制 {budget.DrawItemLimit}。");
            long additional;
            try { additional = OverlayMemoryCost.Estimate(batch); }
            catch (OverflowException) { throw MemoryExceeded(batch); }
            if (additional > Scripting.V2.ParticleLimits.MaximumFrameBytes - bytes) throw MemoryExceeded(batch);
            bytes += additional;
        }
        if (owners.SequenceEqual(OverlayOwners) && batches.SequenceEqual(OverlayBatches) && ReferenceEquals(diagnostics, DiagnosticSnapshot) && budget == VisualBudget && particles.SequenceEqual(Particles) && ReferenceEquals(emissionSchedule, EmissionSchedule)) return this;
        return new(this, batches, diagnostics, budget, particles, emissionSchedule) { OverlayOwners = owners };

        Exception MemoryExceeded(OverlayBatch batch)
        {
            var error = new PreparedFrameException("VisualOutputMemoryExceeded", "全部实例图元快照超过固定帧字节预算。");
            var owner = owners.SingleOrDefault(item => item.InstanceId == batch.InstanceId);
            return owner is null ? error : owner.Failure(TimeUs, error.Code, error);
        }
    }
    private PreparedRenderFrame(PreparedRenderFrame source, ImmutableArray<OverlayBatch> batches,
        RuntimeDiagnosticSnapshot diagnostics, VisualOutputBudget budget, ImmutableArray<ParticleSample> particles, EmissionSchedule? emissionSchedule)
    {
        Snapshot = source.Snapshot; TimeUs = source.TimeUs; OutputMode = source.OutputMode; Target = source.Target;
        Scene = source.Scene; HierarchyScene = source.HierarchyScene; LineTransforms = source.LineTransforms;
        GroupTransforms = source.GroupTransforms;
        GroupOpacities = source.GroupOpacities;
        TokenTransforms = source.TokenTransforms; TokenStyles = source.TokenStyles; _styles = source._styles;
        BaseTransforms = source.BaseTransforms; BaseStyles = source.BaseStyles; ClusterTransforms = source.ClusterTransforms;
        BaseDrawingStyles = source.BaseDrawingStyles;
        TextWarps = source.TextWarps;
        MarkTransforms = source.MarkTransforms; MarkStyles = source.MarkStyles; _markStyles = source._markStyles;
        FinalLineGeometry = source.FinalLineGeometry; FinalGeometry = source.FinalGeometry;
        FinalMarkGeometry = source.FinalMarkGeometry; FinalGroupGeometry = source.FinalGroupGeometry; Playback = source.Playback;
        DiagnosticSnapshot = diagnostics; OverlayOwners = source.OverlayOwners; OverlayBatches = batches; DotBatches = [.. batches.OfType<DotBatch>()]; Particles = particles; VisualBudget = budget; EmissionSchedule = emissionSchedule;
        ImageEffects = source.ImageEffects; Materials = source.Materials; SourceMaterials = source.SourceMaterials;
        V2ParticleSnapshots = source.V2ParticleSnapshots; V2Particles = source.V2Particles; V2AnchorStatistics = source.V2AnchorStatistics;
        _appearances = source._appearances; _markAppearances = source._markAppearances;
    }
}
