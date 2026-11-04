using System.Collections.Immutable;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Scripting.Particles;

namespace MakoKaraGenerator.Scripting.Execution;

internal sealed partial class FrameEffectHost
{
    private readonly List<BurstIntent> _pendingBursts = [];
    public void Dispatch(TimelineEvent evt, TimelineHistory history, bool collectBursts, CancellationToken cancellationToken)
    {
        foreach (var active in _instances)
        {
            if (active.Pure is not null) continue;
            cancellationToken.ThrowIfCancellationRequested();
            if (!active.Subscription!.Matches(evt)) continue;
            var definition = active.Definition;
            BurstWriter writer = new(definition.Id, definition.Capabilities, evt);
            try
            {
                InvokeContext(active, "OnEvent", evt.TimeUs, Diagnostics, (reporter, queries) =>
                {
                    active.Instance.OnEvent(evt, new()
                    { Event = evt, Project = _project, Parameters = definition.Parameters, Resources = definition.Resources, Random = active.Random,
                        History = history, Binding = definition.Binding, Bursts = writer, Diagnostics = reporter,
                        GeometryQueries = queries, CancellationToken = cancellationToken });
                    var output = writer.Complete();
                    if (definition.OverlayLayerOverride is { } layer) output = [.. output.Select(intent => intent with { Descriptor = intent.Descriptor with { Layer = layer } })];
                    if (definition.PackageDeclaration is { } declaration && output.Any(intent => !declaration.Outputs.Any(item => item.Kind == "Overlay" && item.Id == intent.EmitterId.Value)))
                        throw new EffectOutputException("UndeclaredPackageOutput", "Burst 输出未在固定包导出中声明。");
                    // 独立端点重放仍运行相同事件状态，但不消费视觉意图或分配粒子。
                    if (collectBursts) _pendingBursts.AddRange(output);
                }, cancellationToken);
            }
            finally { writer.Invalidate(); }
        }
    }
    public void ResolveBursts(PreparedRenderFrame text, CancellationToken cancellationToken)
    {
        // 列表保留真实事件排序、实例数组排序和同事件 emitter ordinal 排序。
        foreach (var intent in _pendingBursts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var active = _instances.Single(item => item.Definition.Id == intent.InstanceId);
            Invoke(active, "ResolveBurst", intent.TimeUs, Diagnostics, ignoredReporter =>
            {
                if (intent.TimeUs != text.TimeUs) throw new EffectOutputException("InvalidBurstTime", "Burst 只能在当前真实事件边界解析。");
                var anchors = ResolveBurstAnchors(active, intent.Descriptor.Anchor, text);
                ParticleOperation(() => { _particles.Burst(intent, active.Random, anchors, cancellationToken); return true; });
            });
        }
        _pendingBursts.Clear();
    }
    private ImmutableArray<ResolvedParticleAnchor> ResolveBurstAnchors(ActiveEffect active, BurstAnchor anchor, PreparedRenderFrame text)
    {
        var geometry = FinalGeometryFor(active, text);
        var scope = active.Subscription!.OutputScope;
        IEnumerable<ResolvedParticleAnchor> result; int expected;
        switch (anchor)
        {
            case BurstAnchor.BindingCursors cursors:
                return [.. geometry.Playback.GetCursors(cursors.Mode)
                    .OrderBy(static cursor => cursor.TrackId.Value, StringComparer.Ordinal).ThenBy(static cursor => cursor.BindingId.Value, StringComparer.Ordinal)
                    .Select(static cursor => new ResolvedParticleAnchor(new ParticleSource.Cursor(cursor.TrackId, cursor.Identity), cursor.Position))];
            case BurstAnchor.SourceToken token:
                if (!Content.Tokens.Any(item => item.Id == token.Id)) throw new EffectOutputException("UnknownBurstSource", "Burst 源元素不存在。");
                RequireScope(scope.TokenIds.Contains(token.Id));
                expected = geometry.Presentations.TokensInBinding.Count(item => item.TokenId == token.Id);
                result = geometry.Tokens.Where(item => item.WithRuby.TokenId == token.Id)
                    .Select(static item => new ResolvedParticleAnchor(new ParticleSource.Token(item.WithRuby), Center(item.BaseGeometry)));
                break;
            case BurstAnchor.SourceLine line:
                if (!Content.Lines.Any(item => item.Id == line.Id)) throw new EffectOutputException("UnknownBurstSource", "Burst 源行不存在。");
                RequireScope(active.Definition.Binding is not EffectBinding.Tokens && scope.LineIds.Contains(line.Id));
                expected = geometry.Presentations.LinesInBinding.Count(item => item.LineId == line.Id);
                result = geometry.Lines.Where(item => item.Target.LineId == line.Id)
                    .Select(static item => new ResolvedParticleAnchor(new ParticleSource.Line(item.Target), Center(item.BaseGeometry)));
                break;
            case BurstAnchor.TextMark mark:
                if (!_sourceMarkStyles.ContainsKey(mark.Id)) throw new EffectOutputException("UnknownBurstSource", "Burst 节点不是实际文字标记。");
                RequireScope(scope.AllowsMark(mark.Id));
                expected = geometry.Presentations.MarksInBinding.Count(item => item.Target.NodeId == mark.Id);
                result = geometry.Marks.Where(item => item.Target.NodeId == mark.Id)
                    .Select(static item => new ResolvedParticleAnchor(new ParticleSource.TextMark(item.Target), Center(item.Geometry)));
                break;
            default: throw new EffectOutputException("InvalidBurst", "未知 Burst 锚点类型。");
        }
        var anchors = result.OrderBy(static item => item.Source.BindingId.Value, StringComparer.Ordinal).ToImmutableArray();
        if (anchors.IsEmpty || anchors.Length != expected) throw new EffectOutputException("BurstAnchorUnavailable", "指定的 Burst 锚点在事件时刻没有可用装载或最终几何。");
        return anchors;
        static Point2 Center(GeometrySample sample) => sample.BaseWorldMatrix.Map(sample.AdvanceBounds.Center);
        static void RequireScope(bool allowed)
        { if (!allowed) throw new EffectOutputException("BurstTargetOutsideBinding", "Burst 锚点不在输出绑定内；扩大源订阅不会扩大输出权。"); }
    }
}
