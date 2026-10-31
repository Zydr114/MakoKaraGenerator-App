namespace MakoKaraGenerator.Backend.Hosting;

/// <summary>固定预览输入的整组租约；独立运行引用和设备切换引用共享同一资源所有权。</summary>
internal sealed class BackendPreviewRetentionLease : IDisposable
{
    private sealed class SharedState(IDisposable? resource)
    {
        private readonly object _gate = new();
        private int _references;
        private bool _released;

        internal BackendPreviewRetentionLease Acquire()
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_released, this);
                _references++;
                return new(this);
            }
        }

        internal void Release()
        {
            lock (_gate)
            {
                if (--_references != 0) return;
                _released = true;
            }
            resource?.Dispose();
        }
    }

    private SharedState? _state;
    private BackendPreviewRetentionLease(SharedState state) => _state = state;

    internal static IDisposable Own(IDisposable? resource) => resource is BackendPreviewRetentionLease
        ? resource : new SharedState(resource).Acquire();

    internal BackendPreviewRetentionLease AcquireReference() =>
        (_state ?? throw new ObjectDisposedException(nameof(BackendPreviewRetentionLease))).Acquire();

    public void Dispose() => Interlocked.Exchange(ref _state, null)?.Release();
}
