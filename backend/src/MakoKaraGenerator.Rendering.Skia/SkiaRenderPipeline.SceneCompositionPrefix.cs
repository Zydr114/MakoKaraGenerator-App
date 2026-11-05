using MakoKaraGenerator.Core.Effects;

namespace MakoKaraGenerator.Rendering.Skia;

public sealed partial class SkiaRenderPipeline
{
    // One foreground-only, fully closed aggregate. This never owns a screen
    // snapshot, an author History, a graph result, or any background/overlay.
    // Scope entry is borrowed and immutable while a child is executing.
    private sealed class SceneCompositionPrefix : IDisposable
    {
        private readonly SceneCompositionProgram _program;
        private readonly ImageSurfaceBudgetFrame _resources;
        private readonly CancellationToken _cancellationToken;
        private ImageCompositionPayload? _payload;
        internal ImageGraphFrameContext Context { get; }
        internal long Version { get; private set; }
        internal ImageCompositionPayload? Payload => _payload;

        internal SceneCompositionPrefix(SceneCompositionProgram program, ImageSurfaceBudgetFrame resources,
            ImageGraphFrameContext context, CancellationToken cancellationToken)
        { _program = program; _resources = resources; Context = context; _cancellationToken = cancellationToken; }

        internal SceneCompositionPrefix Fork()
        {
            _cancellationToken.ThrowIfCancellationRequested();
            var next = new SceneCompositionPrefix(_program, _resources, Context, _cancellationToken);
            try
            {
                if (_payload is not null) next.Append(_payload);
                next.Version = Version;
                return next;
            }
            catch { next.Dispose(); throw; }
        }

        internal void Append(ImageCompositionPayload completed)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            long version = checked(Version + 1);
            // ComposePayloads copies the sole input too; caller retains ownership.
            // Complete E is retained: independent .8 + .8 is .96, never max(.8,.8).
            var next = _program.ComposePayloads(_payload is null ? [completed] : [_payload, completed],
                _resources, cancellationToken: _cancellationToken);
            var previous = _payload; _payload = next; Version = version; previous?.Dispose();
        }

        public void Dispose() { _payload?.Dispose(); _payload = null; }
    }
}
