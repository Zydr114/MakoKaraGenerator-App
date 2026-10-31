using System.Collections.Immutable;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Scripting.Particles;
using MakoKaraGenerator.Scripting.Projection;

namespace MakoKaraGenerator.Scripting.Execution;

internal sealed partial class FrameEffectHost
{
    private readonly TimelineProjection? _timeline;
    private readonly SemanticAnchorQuery? _anchorQuery;
    private readonly Dictionary<BridgeEndpointKey, PlaybackCursor?> _bridgeEndpoints = [];
    private readonly record struct BridgeEndpointKey(CursorIdentity Identity, CursorEndpointKind Kind, CursorAnchorMode Anchor,
        OutputMode Mode, RenderTarget Target);

    private async ValueTask<ImmutableArray<ResolvedCursorBridge>> ResolveBridgesAsync(ActiveEffect active, EmitterBinding emitter,
        PreparedRenderFrame text, long nextBoundaryUs, RuntimeDiagnosticBuffer diagnostics, CancellationToken cancellationToken)
    {
        var descriptor = emitter.Descriptor;
        if (descriptor.TransitionMode != TransitionMode.Bridge || descriptor.BridgeDurationUs == 0
            || descriptor.BridgeGapLimitUs == 0 || nextBoundaryUs <= text.TimeUs) return [];
        if (_timeline is null || _anchorQuery is null) throw Failure(active.Definition, "PrepareCursorBridge", text.TimeUs,
            new EffectOutputException("TimelineSessionRequired", "桥接需要固定时间线及独立端点会话。"), diagnostics.Snapshot());
        List<ResolvedCursorBridge> bridges = [];
        foreach (var edge in _timeline.CursorTransitions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (edge.GapUs <= 0 || edge.GapUs > descriptor.BridgeGapLimitUs || edge.To.TimeRange.StartUs <= text.TimeUs) continue;
            long start = edge.To.TimeRange.StartUs - Math.Min(edge.GapUs, descriptor.BridgeDurationUs);
            if (start >= nextBoundaryUs || !active.Subscription!.OutputScope.Matches(edge)) continue;
            var previous = await EndpointAsync(edge.From, CursorEndpointKind.PreviousEndLimit).ConfigureAwait(false);
            var next = await EndpointAsync(edge.To, CursorEndpointKind.NextStart).ConfigureAwait(false);
            if (previous is null || next is null)
            {
                Invoke(active, "PrepareCursorBridge", text.TimeUs, diagnostics, reporter => reporter.Report(
                    DiagnosticSeverity.Warning, "CursorBridgeEndpointUnavailable", "真实后继端点没有最终几何，停发本条桥接路径。"));
                continue;
            }
            bridges.Add(new(new(edge, new(start, edge.To.TimeRange.StartUs)), previous.Position, next.Position, descriptor.BridgeCurve));
        }
        return [.. bridges];

        async ValueTask<PlaybackCursor?> EndpointAsync(CursorIdentity identity, CursorEndpointKind kind)
        {
            BridgeEndpointKey key = new(identity, kind, descriptor.AnchorMode, text.OutputMode, text.Target);
            if (_bridgeEndpoints.TryGetValue(key, out var cached)) return cached;
            // 独立未来作者 Error 保留其实际实例身份；不得重新包装成请求桥接的实例。
            var endpoint = await _anchorQuery.ResolveCursorAsync(identity, kind, descriptor.AnchorMode,
                text.OutputMode, text.Target, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            _bridgeEndpoints.Add(key, endpoint); return endpoint;
        }
    }
}
