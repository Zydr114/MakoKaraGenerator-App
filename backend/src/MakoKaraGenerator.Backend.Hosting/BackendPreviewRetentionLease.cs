namespace MakoKaraGenerator.Backend.Hosting;

/// <summary>固定预览输入的整组租约；独立运行引用和设备切换引用共享同一资源所有权。</summary>
internal sealed class BackendPreviewRetentionLease : IDisposable
{
    private sealed class SharedState(IDisposable? resource)
    {
        private readonly object _gate = new();
        private int _references;
        private bool _releaseStarted;

        internal BackendPreviewRetentionLease Acquire()
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_releaseStarted, this);
                _references++;
                return new(this);
            }
        }

        internal void Release()
        {
            lock (_gate)
            {
                if (_references > 1) { _references--; return; }
                // 清理可能已部分执行；失败后保留最后引用重试，但不能再借出输入。
                _releaseStarted = true;
                resource?.Dispose();
                _references = 0;
            }
        }
    }

    private SharedState? _state;
    private readonly object _disposeGate = new();
    private BackendPreviewRetentionLease(SharedState state) => _state = state;

    internal static IDisposable Own(IDisposable? resource) => resource is BackendPreviewRetentionLease
        ? resource : new SharedState(resource).Acquire();

    internal BackendPreviewRetentionLease AcquireReference()
    {
        lock (_disposeGate)
            return (_state ?? throw new ObjectDisposedException(nameof(BackendPreviewRetentionLease))).Acquire();
    }

    public void Dispose()
    {
        lock (_disposeGate)
        {
            _state?.Release();
            _state = null;
        }
    }
}
