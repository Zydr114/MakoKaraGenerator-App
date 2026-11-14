using MakoKaraGenerator.Rendering;

namespace MakoKaraGenerator.Scripting.Execution;

internal sealed class FrameSession(FrameEffectHost host) : IEvaluatedFrameSource, IEvaluatedFrameInspectionSource
{
    /// <inheritdoc/>
    public System.Collections.Immutable.ImmutableArray<RenderEffectInstanceInfo> DescribeEffects() => host.DescribeEffects();
    private readonly SemaphoreSlim _serial = new(1, 1);
    private bool _disposed, _poisoned;

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
                if (_poisoned)
                {
                    host.Load(cancellationToken);
                    _poisoned = false;
                }
                RuntimeDiagnosticBuffer diagnostics = new(host.Diagnostics.Snapshot());
                PreparedRenderFrame result = host.Evaluate(timeUs, outputMode, target, diagnostics, cancellationToken);
                result = host.PreparePureVisuals(result, diagnostics, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                return result;
            }
            catch
            {
                // 取消或失败后不得继续使用已部分推进的状态；下次从固定输入重建。
                _poisoned = true;
                try { host.Unload(); } catch { /* 保留本次原始失败，所有模块已释放。 */ }
                throw;
            }
        }
        finally { _serial.Release(); }
    }
    public async ValueTask DisposeAsync()
    {
        await _serial.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return; _disposed = true;
            host.Dispose();
        }
        finally { _serial.Release(); }
        // 门闩保留，排队或重复 Dispose 可安全退出。
    }
}
