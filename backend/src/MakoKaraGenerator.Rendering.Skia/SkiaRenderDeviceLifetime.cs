namespace MakoKaraGenerator.Rendering.Skia;

/// <summary>自有设备在创建者及所有返回表面释放后才销毁。</summary>
internal sealed class SkiaRenderDeviceLifetime(ISkiaRenderDevice device) : IDisposable
{
    private readonly object _gate = new();
    private int _references = 1;
    private bool _ownerReleased;

    internal Action Acquire()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_ownerReleased, this);
            _references++;
        }
        int released = 0;
        return () => { if (Interlocked.Exchange(ref released, 1) == 0) Release(); };
    }

    private void Release()
    {
        bool final;
        lock (_gate) { final = --_references == 0; }
        if (final) device.Dispose();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_ownerReleased) return;
            _ownerReleased = true;
        }
        Release();
    }
}
