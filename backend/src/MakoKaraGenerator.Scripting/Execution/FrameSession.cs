using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Scripting.Projection;

namespace MakoKaraGenerator.Scripting.Execution;

internal sealed class FrameSession(FrameEffectHost host, TimelineProjection? timeline, ReplayReason? initialReason,
    CancellationToken initialCancellationToken) : IEvaluatedFrameSource, IEvaluatedFrameInspectionSource
{
    /// <inheritdoc/>
    public System.Collections.Immutable.ImmutableArray<RenderEffectInstanceInfo> DescribeEffects() => host.DescribeEffects();
    private readonly SemaphoreSlim _serial = new(1, 1);
    private TimelineHistory.ValidatedEventTable? _eventTable = timeline?.CreateHistoryTable(initialReason ?? ReplayReason.Initial, initialCancellationToken);
    private TimelineHistory? _history;
    private ReplayReason _reason = initialReason ?? ReplayReason.Initial;
    private long _lastBoundary = -1, _lastRequest = -1;
    private int _nextEvent;
    private PreparedRenderFrame? _boundaryFrame;
    private bool _disposed, _poisoned;
    private readonly bool _anchorQuery = initialReason == ReplayReason.AnchorQuery;

    public async ValueTask<PreparedRenderFrame> PrepareAsync(long timeUs, OutputMode outputMode, RenderTarget target,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(timeUs);
        if (!Enum.IsDefined(outputMode) || target.PixelWidth <= 0 || target.PixelHeight <= 0) throw new ArgumentOutOfRangeException(nameof(outputMode));
        await _serial.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this); cancellationToken.ThrowIfCancellationRequested();
            try
            {
                host.ValidateMediaOutput(outputMode);
                bool changedOutput = timeline is not null && _boundaryFrame is { } previousOutput
                    && (previousOutput.OutputMode != outputMode || previousOutput.Target != target);
                if (_poisoned || timeline is not null && timeUs < _lastRequest || changedOutput)
                    Reset(changedOutput ? ReplayReason.InputsChanged : ReplayReason.Seek, cancellationToken);
                if (timeline is not null)
                {
                    if (_lastBoundary < 0) await ProcessBoundaryAsync(0, outputMode, target, cancellationToken).ConfigureAwait(false);
                    while (NextBoundary() is { } next && next <= timeUs)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (!_anchorQuery) host.AdvanceParticles(next, outputMode, target, _history!, cancellationToken);
                        host.Advance(new(_lastBoundary, next), _history!, cancellationToken);
                        await ProcessBoundaryAsync(next, outputMode, target, cancellationToken).ConfigureAwait(false);
                    }
                }
                PreparedRenderFrame result;
                if (host.CanReuseBoundaryFrame && _boundaryFrame is { } frame
                    && frame.TimeUs == timeUs && frame.OutputMode == outputMode && frame.Target == target)
                    result = frame;
                else
                {
                    RuntimeDiagnosticBuffer diagnostics = new(host.Diagnostics.Snapshot());
                    result = host.Evaluate(timeUs, outputMode, target, _history, diagnostics, cancellationToken);
                    if (!_anchorQuery)
                    {
                        if (timeline is null) await host.EvaluateEmittersAsync(result, null, timeUs, diagnostics, cancellationToken).ConfigureAwait(false);
                        else result = host.PrepareParticles(result, _history!, diagnostics, cancellationToken);
                        result = host.EvaluateVisuals(result, _boundaryFrame, _history, diagnostics, cancellationToken);
                    }
                }
                if (!_anchorQuery)
                {
                    RuntimeDiagnosticBuffer diagnostics = new(result.DiagnosticSnapshot);
                    result = host.PreparePureVisuals(result, diagnostics, cancellationToken);
                }
                cancellationToken.ThrowIfCancellationRequested();
                _lastRequest = timeUs;
                return result;
            }
            catch
            {
                // 取消或失败后不得继续使用已部分推进的状态；下次从固定输入重建。
                _poisoned = true; _boundaryFrame = null;
                try { host.Unload(); } catch { /* 保留本次原始失败，所有模块已释放。 */ }
                throw;
            }
        }
        finally { _serial.Release(); }
    }
    private long? NextBoundary() => NextBoundaryAfter(_lastBoundary);
    private long? NextBoundaryAfter(long timeUs)
    {
        long? nextGrid;
        try { nextGrid = SemanticSimulationGrid.TimeAt(checked(SemanticSimulationGrid.IndexAtOrBefore(timeUs) + 1)); }
        catch (OverflowException) { nextGrid = null; } // Int64 时间之外没有下一网格，不回绕。
        long? nextEvent = timeline is not null && _nextEvent < timeline.Events.Length ? timeline.Events[_nextEvent].TimeUs : null;
        return nextGrid is { } grid && nextEvent is { } evt ? Math.Min(grid, evt) : nextGrid ?? nextEvent;
    }
    private async ValueTask ProcessBoundaryAsync(long timeUs, OutputMode outputMode, RenderTarget target, CancellationToken cancellationToken)
    {
        var previous = _boundaryFrame; var previousHistory = _history;
        while (_nextEvent < timeline!.Events.Length && timeline.Events[_nextEvent].TimeUs == timeUs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var evt = _eventTable!.EventAt(_nextEvent++);
            _history = _eventTable.Prefix(timeUs, _nextEvent);
            host.Dispatch(evt, _history, !_anchorQuery, cancellationToken);
        }
        _history = _eventTable!.Prefix(timeUs, _nextEvent);
        // Text-only legacy state never observes another author's final geometry. Skip
        // pure calls on its replay grid, then evaluate all text once at the request.
        var text = host.Evaluate(timeUs, outputMode, target, _history, host.Diagnostics, cancellationToken,
            includePure: host.RequiresPureAtLegacyBoundaries);
        if (!_anchorQuery)
        {
            await host.EvaluateEmittersAsync(text, _history, NextBoundaryAfter(timeUs) ?? timeUs, host.Diagnostics, cancellationToken).ConfigureAwait(false);
            host.ResolveBursts(text, cancellationToken);
            text = host.PrepareParticles(text, _history, host.Diagnostics, cancellationToken);
            if (previous is not null)
                host.AdvanceVisuals(new(previous.TimeUs, timeUs), previous, text, previousHistory!, _history, cancellationToken);
            text = host.EvaluateVisuals(text, text, _history, host.Diagnostics, cancellationToken);
        }
        _boundaryFrame = text;
        _lastBoundary = timeUs;
    }
    private void Reset(ReplayReason reason, CancellationToken cancellationToken)
    {
        _reason = _anchorQuery ? ReplayReason.AnchorQuery : reason; _history = null; _eventTable = null; _nextEvent = 0;
        _lastBoundary = -1; _lastRequest = -1; _boundaryFrame = null;
        host.Load(timeline is not null, cancellationToken, preservePure: !_poisoned);
        _eventTable = timeline?.CreateHistoryTable(_reason, cancellationToken);
        _poisoned = false;
    }
    public async ValueTask DisposeAsync()
    {
        await _serial.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return; _disposed = true;
            _eventTable = null; _history = null; _boundaryFrame = null;
            host.Dispose();
        }
        finally { _serial.Release(); }
        // 门闩保留，排队或重复 Dispose 可安全退出。
    }
}
