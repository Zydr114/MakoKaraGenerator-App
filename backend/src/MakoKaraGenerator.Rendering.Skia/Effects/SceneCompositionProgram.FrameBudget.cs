namespace MakoKaraGenerator.Rendering.Skia;

internal sealed partial class SceneCompositionProgram
{
    private ImageSurfaceBudgetFrame? _activeFrame;
    internal IDisposable BeginSceneFrame(ImageSurfaceBudgetFrame frame, Action chargePass)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_activeFrame is not null) throw new InvalidOperationException("同一宿主合成器不能重入一帧。");
            var lease = frame.BeginScenePassBudget(chargePass);
            _activeFrame = frame;
            return new FrameLease(this, lease);
        }
    }
    private sealed class FrameLease(SceneCompositionProgram owner, IDisposable lease) : IDisposable
    {
        private SceneCompositionProgram? _owner = owner;
        public void Dispose()
        {
            var value = Interlocked.Exchange(ref _owner, null);
            if (value is null) return;
            lock (value._gate) { value._activeFrame = null; lease.Dispose(); }
        }
    }
}
