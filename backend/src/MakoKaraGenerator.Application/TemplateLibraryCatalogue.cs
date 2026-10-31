using System.Collections.Immutable;
using System.Diagnostics;
using MakoKaraGenerator.Core.Persistence;

namespace MakoKaraGenerator.Application;

/// <summary>目录只读来源；变化通知只能携带本次成功发布的固定快照。</summary>
public interface ITemplateLibrarySource
{
    /// <summary>实际库索引路径。</summary>
    string Path { get; }
    /// <summary>读取完整闭包及诊断。</summary>
    StyleTemplateLibraryLoadResult Read();
    /// <summary>读取实际索引字节身份。</summary>
    LibraryIndexRevision ReadRevision();
    /// <summary>本进程成功发布的固定快照。</summary>
    event Action<StyleTemplateLibraryLoadResult>? Changed;
}

/// <summary>保留最后成功目录；错误和过期状态不伪装成空库。</summary>
public sealed record TemplateCatalogueSnapshot(StyleTemplateLibraryLoadResult Library, bool IsLoaded,
    bool IsStale, bool IsRefreshing, string? Error, long Sequence)
{
    /// <summary>仅完成复核的有效目录可以发起应用或更新。</summary>
    public bool CanApply => IsLoaded && !IsStale && !IsRefreshing && Error is null;
}

/// <summary>应用级模板目录：选择与编辑只读内存，激活及失效通过单后台读取器复核。</summary>
public sealed class TemplateLibraryCatalogue : IDisposable, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly ITemplateLibrarySource _source;
    private readonly TemplateCatalogueMonitor? _monitor;
    private TemplateCatalogueSnapshot _snapshot = new(new([], []), false, true, false, null, 0);
    private int _clients;
    private long _epoch;
    private bool _running, _pending, _full, _disposed;
    private TaskCompletionSource _idle = Finished();

    /// <summary>不在构造线程读盘；文件通知只标记失效，读取工作由后台拥有。</summary>
    public TemplateLibraryCatalogue(ITemplateLibrarySource source, bool watchFiles = true)
    {
        ArgumentNullException.ThrowIfNull(source); _source = source;
        if (watchFiles) _monitor = new(source.Path, Invalidate);
        _source.Changed += OnPublished;
    }
    /// <summary>当前不可变展示快照。</summary>
    public TemplateCatalogueSnapshot Snapshot { get { lock (_gate) return _snapshot; } }
    /// <summary>从任意线程通知；视图自行切线程并重新取得最新快照。</summary>
    public event Action? Changed;

    /// <summary>工作区激活时后台复核；释放最后租约后不再启动读取。</summary>
    public IDisposable Activate()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this); _clients++;
            if (_clients == 1) RequestLocked(false);
        }
        Notify(); return new Lease(this);
    }
    /// <summary>用户要求完整刷新；隐藏工作区只记录失效，不读盘。</summary>
    public void Refresh() => Invalidate(true);
    /// <summary>等待当前合并读取批次完成；不主动读盘。</summary>
    public Task WaitForIdleAsync() { lock (_gate) return _idle.Task; }

    private void Invalidate(bool full)
    {
        lock (_gate)
        {
            if (_disposed) return;
            RequestLocked(full);
        }
        Notify();
    }
    private void RequestLocked(bool full)
    {
        _epoch++; _full |= full;
        _snapshot = _snapshot with { IsStale = true, IsRefreshing = _clients > 0, Sequence = _snapshot.Sequence + 1 };
        if (_clients == 0) return;
        _pending = true;
        if (_running) return;
        _running = true; _idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Task.Run(ReadLoop);
    }
    private void ReadLoop()
    {
        while (true)
        {
            long epoch; bool full; TemplateCatalogueSnapshot previous;
            lock (_gate)
            {
                if (_disposed || _clients == 0 || !_pending)
                { _running = false; _idle.TrySetResult(); return; }
                _pending = false; epoch = _epoch; full = _full; _full = false; previous = _snapshot;
            }
            StyleTemplateLibraryLoadResult? loaded = null; string? error = null;
            try
            {
                _monitor?.EnsureWatching();
                var revision = _source.ReadRevision();
                loaded = full || !previous.IsLoaded || previous.Error is not null || previous.Library.Revision != revision
                    ? _source.Read() : previous.Library;
                if (_source.ReadRevision() != loaded.Revision) throw new IOException("模板索引在读取期间改变，请刷新目录。");
                if (loaded.VersionMismatch || loaded.Issues.Count != 0)
                    error = string.Join("\n", loaded.Issues.DefaultIfEmpty("模板库版本不匹配。"));
            }
            catch (Exception failure)
            { error = failure.Message; }
            lock (_gate)
            {
                // A newer local publication, invalidation or workspace transition owns the result.
                if (_disposed || _clients == 0 || epoch != _epoch)
                { _full |= full; continue; }
                _snapshot = error is null && loaded is not null
                    ? new(Freeze(loaded), true, false, false, null, _snapshot.Sequence + 1)
                    : _snapshot with { IsStale = true, IsRefreshing = false, Error = error, Sequence = _snapshot.Sequence + 1 };
            }
            Notify();
        }
    }
    private void OnPublished(StyleTemplateLibraryLoadResult library)
    {
        if (library.VersionMismatch || library.Issues.Count != 0) { Invalidate(true); return; }
        lock (_gate)
        {
            if (_disposed) return;
            // Commit notifications arrive after the write lease is released and can be reordered.
            // Display the fixed publication, but verify actual bytes before permitting new applications.
            _snapshot = new(Freeze(library), true, true, false, null, _snapshot.Sequence + 1);
            RequestLocked(false);
        }
        Notify();
    }
    private void Deactivate()
    {
        lock (_gate)
        {
            if (_disposed || --_clients != 0) return;
            _epoch++; _pending = false;
            _snapshot = _snapshot with { IsStale = true, IsRefreshing = false, Sequence = _snapshot.Sequence + 1 };
        }
        Notify();
    }
    private void Notify()
    {
        foreach (Action callback in Changed?.GetInvocationList() ?? [])
            try { callback(); } catch (Exception failure) { Trace.TraceError("模板目录观察者失败：{0}", failure); }
    }
    private static StyleTemplateLibraryLoadResult Freeze(StyleTemplateLibraryLoadResult library)
        => library with { Templates = library.Templates.ToImmutableArray(), Issues = library.Issues.ToImmutableArray() };
    private static TaskCompletionSource Finished() { var source = new TaskCompletionSource(); source.SetResult(); return source; }
    /// <summary>拒绝新读取，释放文件监视；已开始的只读工作完成后丢弃结果。</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true; _epoch++; _pending = false;
            _snapshot = _snapshot with { IsStale = true, IsRefreshing = false, Sequence = _snapshot.Sequence + 1 };
        }
        _source.Changed -= OnPublished; _monitor?.Dispose(); GC.SuppressFinalize(this);
    }
    /// <summary>另外等待已经开始的后台只读工作结束。</summary>
    public async ValueTask DisposeAsync() { Dispose(); await WaitForIdleAsync().ConfigureAwait(false); }
    private sealed class Lease(TemplateLibraryCatalogue owner) : IDisposable
    {
        private TemplateLibraryCatalogue? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Deactivate();
    }
}
