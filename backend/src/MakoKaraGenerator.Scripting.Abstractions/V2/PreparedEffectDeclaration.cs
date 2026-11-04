using System.Collections.Immutable;

namespace MakoKaraGenerator.Scripting.V2;

/// <summary>宿主从已验证包清单适配的发射器上限，不向作者提供新的准入入口。</summary>
internal sealed record ParticleEmitterDeclaration(EmitterId Id, string Kind, long MaxLifetimeUs);

/// <summary>当前 controller 已声明的 canvas 输出及其独立能力。</summary>
internal sealed record CanvasOutputDeclaration(OutputId Id, ImmutableArray<string> Requires);

/// <summary>Declare 后、发布固定结果前的第二次声明校验；不能以构造器检查代替。</summary>
internal static class PreparedEffectDeclaration
{
    internal static void Validate(PreparedEffect prepared, DeclareContext source, ImmutableArray<string> capabilities,
        ImmutableArray<ParticleEmitterDeclaration> emitters, ImmutableArray<CanvasOutputDeclaration> outputs,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested(); ArgumentNullException.ThrowIfNull(prepared); ArgumentNullException.ThrowIfNull(source);
        if (!source.InstanceId.IsValid || capabilities.IsDefault || emitters.IsDefault || outputs.IsDefault
            || capabilities.Any(string.IsNullOrWhiteSpace) || capabilities.Distinct(StringComparer.Ordinal).Count() != capabilities.Length
            || prepared.Particles is null || prepared.GeometrySampling is null || prepared.VisualOutputs.IsDefault)
            throw Invalid("固定输入、准备结果或包声明无效。");
        if (emitters.Length > ParticleLimits.MaximumEmitters) throw Budget();
        var packageCapabilities = capabilities.ToImmutableHashSet(StringComparer.Ordinal);
        Dictionary<EmitterId, ParticleEmitterDeclaration> manifest = [];
        foreach (var emitter in emitters)
        {
            token.ThrowIfCancellationRequested();
            if (emitter is null || !emitter.Id.IsValid || emitter.Kind is not ("cursor" or "burst")
                || emitter.MaxLifetimeUs is <= 0 or > ParticleLimits.MaximumLifetimeUs || !manifest.TryAdd(emitter.Id, emitter))
                throw Invalid("发射器清单必须具有唯一身份、明确种类与合法最大寿命。");
            RequirePackage(emitter.Kind == "cursor" ? "particles.continuous" : "particles.burst");
        }
        Dictionary<OutputId, CanvasOutputDeclaration> canvas = [];
        foreach (var output in outputs)
        {
            token.ThrowIfCancellationRequested();
            if (output is null || !output.Id.IsValid || output.Requires.IsDefault || output.Requires.Any(string.IsNullOrWhiteSpace)
                || output.Requires.Distinct(StringComparer.Ordinal).Count() != output.Requires.Length || !canvas.TryAdd(output.Id, output))
                throw Invalid("画布输出必须是唯一的固定声明。");
            foreach (string capability in output.Requires) RequirePackage(capability);
        }

        var schedule = prepared.Particles;
        if (schedule.Continuous.IsDefault || schedule.Bursts.IsDefault) throw Invalid("发射计划不能使用 default 集合。");
        if ((long)schedule.Continuous.Length + schedule.Bursts.Length > ParticleLimits.MaximumEmitters) throw Budget();
        HashSet<EmitterId> planned = []; Dictionary<EmitterId, (OverlayLayer Layer, ParticleRenderMode Rendering)> renderPolicies = [];
        long rateElements = 0, burstRows = 0;
        foreach (var plan in schedule.Continuous)
        {
            token.ThrowIfCancellationRequested();
            if (plan is null || plan.Rate is null || plan.Bridge is null || !Enum.IsDefined(plan.AnchorMode)
                || !Enum.IsDefined(plan.Bridge.Mode) || plan.Bridge.Curve is null || plan.Bridge.DurationUs < 0 || plan.Bridge.GapLimitUs < 0
                || plan.Bridge.Mode == TransitionMode.Bridge && plan.Bridge.DurationUs == 0
                || plan.Rate.Segments.IsDefault || plan.Rate.EnabledRanges.IsDefault)
                throw Invalid("连续计划的速率轨、锚点或桥接策略无效。");
            var declaration = RequireEmitter(plan.Id, "cursor", "particles.continuous");
            ValidateParticle(plan.Particle, declaration);
            RequireRenderCapability(plan.Id, plan.Particle);
            rateElements += (long)plan.Rate.Segments.Length + plan.Rate.EnabledRanges.Length;
            if (rateElements > ParticleLimits.MaximumRateElements) throw Budget();
            long previous = -1;
            foreach (var segment in plan.Rate.Segments)
            {
                token.ThrowIfCancellationRequested();
                if (segment is null || !segment.Range.IsValid || segment.Range.StartUs < previous
                    || !double.IsFinite(segment.StartPerSecond) || segment.StartPerSecond < 0
                    || !double.IsFinite(segment.EndPerSecond) || segment.EndPerSecond < 0)
                    throw new EffectOutputException("InvalidParticleRate", "速率段必须有效、有序、互不重叠且有限非负。");
                previous = segment.Range.EndUs;
            }
            ValidateRanges(plan.Rate.EnabledRanges, token);
        }
        foreach (var plan in schedule.Bursts)
        {
            token.ThrowIfCancellationRequested();
            if (plan is null || plan.Selector is null || plan.Rows.IsDefault) throw Invalid("Burst 计划必须具有真实 selector 与明确行集合。");
            var declaration = RequireEmitter(plan.Id, "burst", "particles.burst");
            burstRows += plan.Rows.Length; if (burstRows > ParticleLimits.MaximumBurstRows) throw Budget();
            HashSet<EventId> eventIds = [];
            foreach (var row in plan.Rows)
            {
                token.ThrowIfCancellationRequested();
                if (row is null || !row.EventId.IsValid || row.Count < 0 || row.Anchor is null || !row.Anchor.IsValid || !eventIds.Add(row.EventId)
                    || source.Timeline.Ordinal(plan.Selector, row.EventId) is null)
                    throw Invalid("Burst 行需要属于本 selector 的真实唯一事件、非负数量与有效锚点。");
                ValidateParticle(row.Particle, declaration); RequireRenderCapability(plan.Id, row.Particle);
            }
        }

        HashSet<OutputId> visualIds = [];
        foreach (var output in prepared.VisualOutputs)
        {
            token.ThrowIfCancellationRequested();
            if (output is null || !output.Id.IsValid || !Enum.IsDefined(output.Primitive) || !Enum.IsDefined(output.Layer)
                || !visualIds.Add(output.Id) || manifest.ContainsKey(new EmitterId(output.Id.Value)))
                throw new EffectOutputException("InvalidVisualDeclaration", "晚期输出需有唯一身份、明确类型和层，不能占用发射器保留身份。");
            RequireOutput(output.Id, VisualCapability(output.Primitive));
        }
        var sampling = prepared.GeometrySampling;
        if (!Enum.IsDefined(sampling.Mode) || sampling.ContinuousRanges.IsDefault || sampling.DiscontinuitiesUs.IsDefault)
            throw Invalid("最终几何采样策略无效。");
        if ((long)sampling.ContinuousRanges.Length + sampling.DiscontinuitiesUs.Length > ParticleLimits.MaximumSourceIntervals) throw Budget();
        ValidateRanges(sampling.ContinuousRanges, token);
        long previousTime = -1;
        foreach (long time in sampling.DiscontinuitiesUs)
        {
            token.ThrowIfCancellationRequested();
            if (time < 0 || time <= previousTime) throw Invalid("几何断点必须非负且严格递增。");
            previousTime = time;
        }
        token.ThrowIfCancellationRequested();

        void RequirePackage(string capability)
        {
            if (!packageCapabilities.Contains(capability))
                throw new EffectOutputException("UndeclaredCapability", "包未声明能力：" + capability);
        }
        void RequireOutput(OutputId id, string capability)
        {
            RequirePackage(capability);
            if (!canvas.TryGetValue(id, out var output) || !output.Requires.Contains(capability, StringComparer.Ordinal))
                throw new EffectOutputException("UndeclaredPackageOutput", "当前 controller 未声明具名 canvas 输出及其能力：" + id.Value);
        }
        ParticleEmitterDeclaration RequireEmitter(EmitterId id, string kind, string capability)
        {
            if (!id.IsValid || !planned.Add(id) || !manifest.TryGetValue(id, out var declaration) || declaration.Kind != kind)
                throw new EffectOutputException("UndeclaredEmitter", "发射计划需要唯一且与包清单种类一致的发射器身份。");
            RequireOutput(new OutputId(id.Value), capability); return declaration;
        }
        void RequireRenderCapability(EmitterId id, ParticleSpec particle)
        {
            var policy = (particle.Layer, particle.Rendering);
            if (renderPolicies.TryGetValue(id, out var previous) && previous != policy)
                throw new EffectOutputException("ParticleEmitterRenderPolicyMismatch", "同一发射器必须固定图层与自动／Custom 绘制策略。");
            renderPolicies[id] = policy;
            if (particle.Rendering == ParticleRenderMode.Dots) RequireOutput(new OutputId(id.Value), "visuals.dots");
        }
    }

    private static void ValidateParticle(ParticleSpec? particle, ParticleEmitterDeclaration declaration)
    {
        if (particle is null) throw Invalid("发射计划必须具有封闭粒子描述。");
        particle.Validate();
        if (particle.LifetimeUs > declaration.MaxLifetimeUs)
            throw new EffectOutputException("ParticleLifetimeExceeded", "实际粒子寿命超过包发射器声明。");
    }
    private static void ValidateRanges(ImmutableArray<TimeRange> ranges, CancellationToken token)
    {
        long previous = -1;
        foreach (var range in ranges)
        {
            token.ThrowIfCancellationRequested();
            if (!range.IsValid || range.StartUs < previous) throw Invalid("时间窗必须有效、有序且互不重叠。");
            previous = range.EndUs;
        }
    }
    internal static string VisualCapability(VisualPrimitiveKind kind) => kind switch
    {
        VisualPrimitiveKind.Dots => "visuals.dots", VisualPrimitiveKind.Segments => "visuals.segments",
        VisualPrimitiveKind.Trails => "visuals.trails", VisualPrimitiveKind.Sprites => "visuals.sprites",
        _ => throw new EffectOutputException("InvalidVisualDeclaration", "未知晚期图元种类。"),
    };
    private static EffectOutputException Invalid(string message) => new("InvalidParticleDeclaration", message);
    private static EffectOutputException Budget() => new("ParticlePrepareBudgetExceeded", "声明超过发射器、速率段、启用窗或 Burst 行的固定预算。");
}
