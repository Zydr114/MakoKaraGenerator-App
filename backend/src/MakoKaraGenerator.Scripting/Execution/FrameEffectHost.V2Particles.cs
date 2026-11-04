using System.Collections.Immutable;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Scripting.Particles.V2;
using MakoKaraGenerator.Scripting.V2;

namespace MakoKaraGenerator.Scripting.Execution;

internal sealed partial class FrameEffectHost
{
    // Called once for the actual requested frame, never for legacy replay grids
    // or an anchor supplement. No particle result is filtered by current text visibility.
    private PreparedRenderFrame PreparePureVisualsCore(PreparedRenderFrame text, RuntimeDiagnosticBuffer diagnostics, CancellationToken token)
    {
        var pureOwners = _instances.Where(static item => item.Pure is not null).ToArray();
        if (pureOwners.Length == 0) return text;
        if (pureOwners.All(static item => item.Pure!.Instance is not IPureVisualEffect
            && item.Pure.Prepared.Particles.Continuous.IsEmpty && item.Pure.Prepared.Particles.Bursts.IsEmpty))
            return PreparePureTextSnapshots(text, diagnostics, pureOwners, token);
        var particleBudget = V2Budgets.CreateParticleQuery();
        var transaction = _v2AnchorMemo.Begin();
        V2AnchorRequest query = new(text, transaction, particleBudget, V2Budgets.CreateAnchorQuery(token));
        BirthWindow window = new(Math.Max(0, text.TimeUs - ParticleLimits.MaximumLifetimeUs), text.TimeUs);
        List<(ActiveEffect Active, ImmutableArray<ScheduledBirth> Births)> selected = [];
        // Prove all birth/alive limits before doing any final-text supplement.
        foreach (var active in pureOwners)
        {
            token.ThrowIfCancellationRequested(); var pure = active.Pure!;
            var births = pure.ParticleSchedule!.ReadBirths(window, particleBudget, token);
            int aliveCount = births.Count(item => item.DeathUs > text.TimeUs);
            particleBudget.ReserveAlive(pure.ParticleOwner!, aliveCount, text.TimeUs);
            particleBudget.ReserveBytes(pure.ParticleOwner!, 256L * births.Length + 96L * aliveCount, text.TimeUs);
            selected.Add((active, births));
        }
        List<PreparedV2ParticleSnapshot> snapshots = [];
        List<OverlayBatch> batches = [.. text.OverlayBatches];
        long drawCount = batches.Sum(static batch => batch.ItemCount);
        foreach (var (active, rows) in selected)
        {
            var pure = active.Pure!; var owner = pure.ParticleOwner!;
            List<ParticleBirth> births = new(rows.Length);
            List<ParticleView> alive = [];
            Dictionary<ParticleIdentity, ImmutableArray<ParticleTrailSample>> trails = [];
            foreach (var row in rows)
            {
                token.ThrowIfCancellationRequested(); ParticleBirth? birth = null;
                Invoke(active, "ResolveFinalAnchors", row.BirthUs, diagnostics, _ =>
                {
                    var anchor = ResolveV2Anchor(active, row, query, diagnostics, token);
                    birth = ParticleKinematics.ResolveBirth(row, anchor, owner);
                    if (active.Definition.OverlayLayerOverride is { } layer) birth = birth with { Spec = birth.Spec with { Layer = layer } };
                });
                var resolved = birth ?? throw new InvalidOperationException("最终出生解析没有返回固定记录。");
                births.Add(resolved);
                if (resolved.DeathUs <= text.TimeUs) continue;
                alive.Add(ParticleKinematics.Evaluate(resolved, text.TimeUs, owner));
                trails.Add(resolved.Id, ParticleKinematics.GetTrail(resolved, text.TimeUs, owner, particleBudget, token));
            }
            var snapshot = new PreparedV2ParticleSnapshot(active.Definition.Id, new(window, [.. births]), [.. alive],
                [.. alive.Select(item => new PreparedV2ParticleTrail(item.Id, trails[item.Id]))]);
            snapshots.Add(snapshot);
            Invoke(active, "EvaluateVisuals", text.TimeUs, diagnostics, _ =>
            {
                // Each auto emitter owns exactly one reserved output id. Custom
                // particles remain in the view and produce no automatic batch.
                foreach (var emitter in alive.Where(static item => item.Rendering == ParticleRenderMode.Dots)
                    .GroupBy(static item => item.Id.EmitterId).OrderBy(static group => group.Key.Value, StringComparer.Ordinal))
                {
                    List<Dot> dots = [];
                    foreach (var particle in emitter)
                    {
                        token.ThrowIfCancellationRequested();
                        if (particle.SizePx == 0 || particle.Color.Alpha == 0) continue;
                        HashSet<Point2> drawn = [];
                        foreach (var sample in trails[particle.Id])
                            if (sample.TimeUs != text.TimeUs && drawn.Add(sample.Position)) AddDot(sample.Position, particle);
                        if (drawn.Add(particle.Position)) AddDot(particle.Position, particle);
                    }
                    AddBatch(new DotBatch(active.Definition.Id, new(emitter.Key.Value), emitter.First().Layer, [.. dots]), reserveItems: false);
                    void AddDot(Point2 position, ParticleView particle)
                    {
                        if (++drawCount > _visualBudget.DrawItemLimit) DrawLimit();
                        particleBudget.ReserveBytes(owner, 64, text.TimeUs);
                        dots.Add(new(position, particle.SizePx / 2, particle.Color));
                    }
                }
                if (pure.Instance is not IPureVisualEffect visual) return;
                ParticleFrame particles = new(active.Definition.Id, text.TimeUs, snapshot.Alive, snapshot.Births,
                    id => trails[id], token);
                VisualOutput output = new(active.Definition.Id, active.Definition.PackageDefinition!.Contents.Requires,
                    pure.Prepared.VisualOutputs, active.Definition.Resources, _visualBudget, token);
                try
                {
                    pure.Source.Indexes.SetQueryCancellation(token);
                    VisualFrame frame = new(pure.Source, text.TimeUs, active.Definition.Binding, text.OutputMode,
                        FinalGeometryFor(active, text), particles);
                    using (MeasureAuthor(active, "EvaluateVisuals")) visual.EvaluateVisuals(in frame, pure.Prepared, output);
                    token.ThrowIfCancellationRequested();
                    foreach (var original in output.Complete())
                        AddBatch(active.Definition.OverlayLayerOverride is { } layer ? original with { Layer = layer } : original, reserveItems: true);
                }
                finally { output.Invalidate(); particles.Invalidate(); }

                void AddBatch(OverlayBatch batch, bool reserveItems)
                {
                    if (batches.Any(item => item.InstanceId == batch.InstanceId && item.Id == batch.Id))
                        throw new EffectOutputException("DuplicateOverlayBatch", "自动发射和手工视觉输出存在同名批次。");
                    if (reserveItems)
                    {
                        drawCount = checked(drawCount + batch.ItemCount);
                        if (drawCount > _visualBudget.DrawItemLimit) DrawLimit();
                        particleBudget.ReserveBytes(owner, checked(batch.ItemCount * 96 + 256), text.TimeUs);
                    }
                    batches.Add(batch);
                }
                static void DrawLimit() => throw new EffectOutputException("VisualOutputBudgetExceeded", "全部实例的自动粒子和晚期图元超过固定绘制预算。");
            });
        }
        token.ThrowIfCancellationRequested();
        Invoke(pureOwners[^1], "ResolveFinalAnchors", text.TimeUs, diagnostics, _ => query.Budget.Check());
        var order = _definitions.Select((definition, index) => (definition.Id, index)).ToDictionary(static item => item.Id, static item => item.index);
        var result = text.WithV2ParticleSnapshots([.. snapshots], query.Budget.Statistics)
            .WithOverlaySnapshot([.. batches.OrderBy(batch => order[batch.InstanceId]).ThenBy(static batch => batch.Id.Value, StringComparer.Ordinal)],
                diagnostics.Snapshot(), _visualBudget,
                owners: [.. _definitions.Select(definition => new PreparedOverlayOwner(definition.Id, definition.RuntimeHash, definition.PackageIdentity))]);
        transaction.Commit(token); return result;
    }

    private PreparedRenderFrame PreparePureTextSnapshots(PreparedRenderFrame text, RuntimeDiagnosticBuffer diagnostics,
        ActiveEffect[] pureOwners, CancellationToken token)
    {
        // An empty declaration can never acquire births at a later request. Keep
        // every public owner/window and the same conservative empty-read charge,
        // without creating schedules' query temporaries or an unused anchor memo.
        var budget = V2Budgets.CreateParticleQuery();
        BirthWindow window = new(Math.Max(0, text.TimeUs - ParticleLimits.MaximumLifetimeUs), text.TimeUs);
        var snapshots = ImmutableArray.CreateBuilder<PreparedV2ParticleSnapshot>(pureOwners.Length);
        foreach (var active in pureOwners)
        {
            token.ThrowIfCancellationRequested();
            // CompiledParticleSchedule.Read charges this fixed upper bound even
            // for zero rows. This is budget accounting, not allocated storage.
            budget.ReserveBytes(active.Pure!.ParticleOwner!, 16_384, text.TimeUs);
            snapshots.Add(new(active.Definition.Id, new(window, []), [], []));
        }
        token.ThrowIfCancellationRequested();
        var batches = text.OverlayBatches;
        if (batches.Length > 1)
        {
            var order = _definitions.Select((definition, index) => (definition.Id, index))
                .ToDictionary(static item => item.Id, static item => item.index);
            batches = [.. batches.OrderBy(batch => order[batch.InstanceId])
                .ThenBy(static batch => batch.Id.Value, StringComparer.Ordinal)];
        }
        return text.WithV2ParticleSnapshots(snapshots.MoveToImmutable(), new(0, 0, 0, 0, 0))
            .WithOverlaySnapshot(batches, diagnostics.Snapshot(), _visualBudget,
                owners: [.. _definitions.Select(definition => new PreparedOverlayOwner(definition.Id, definition.RuntimeHash, definition.PackageIdentity))]);
    }
}
