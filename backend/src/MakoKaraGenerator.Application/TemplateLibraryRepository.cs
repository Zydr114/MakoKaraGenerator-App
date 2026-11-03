using System.Collections.Immutable;
using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Application;

/// <summary>模板库的修订检查与协作提交入口；保持模板schema和闭包原子发布协议。</summary>
public sealed class TemplateLibraryRepository : ITemplateLibrarySource
{
    private readonly Func<string> _resolvePath;
    private readonly object _gate = new();
    private LibraryRepository<StyleTemplate, StyleTemplateLibraryLoadResult>? _store;
    private Action<StyleTemplateLibraryLoadResult>? _changed;
    /// <summary>为指定入口创建仓储；所有本应用写入应使用该入口。</summary>
    public TemplateLibraryRepository(string path) : this(() => path) => _ = Store;
    /// <summary>以可信组合根的延迟选址创建仓储；首次成功后固定路径，失败可重试，订阅事件不触发磁盘读取。</summary>
    public TemplateLibraryRepository(Func<string> resolvePath)
    { ArgumentNullException.ThrowIfNull(resolvePath); _resolvePath = resolvePath; }
    private LibraryRepository<StyleTemplate, StyleTemplateLibraryLoadResult> Store
    {
        get
        {
            lock (_gate)
            {
                if (_store is not null) return _store;
                var store = new LibraryRepository<StyleTemplate, StyleTemplateLibraryLoadResult>(_resolvePath(), p => StyleTemplateLibrary.Load(p), s => s.Revision,
                    s => s.VersionMismatch || s.Issues.Count != 0,
                    (p, items, expected, token) => new(items.ToImmutableArray(), [])
                    { Revision = StyleTemplateLibrary.Save(p, items, expected, cancellationToken: token), SchemaVersion = StyleTemplateLibrary.CurrentSchemaVersion });
                store.Changed += _changed;
                return _store = store;
            }
        }
    }
    /// <summary>规范库入口。</summary>
    public string Path => Store.Path;
    /// <summary>成功发布不同修订后通知一次；订阅者自行转入所属线程。</summary>
    public event Action<StyleTemplateLibraryLoadResult>? Changed
    {
        add { lock (_gate) { _changed += value; if (_store is not null) _store.Changed += value; } }
        remove { lock (_gate) { _changed -= value; if (_store is not null) _store.Changed -= value; } }
    }
    /// <summary>从同一次索引读取取得内容、修订及完整诊断。</summary>
    public StyleTemplateLibraryLoadResult Read() => Store.Read();
    /// <summary>只读取真实索引字节的修订，不展开模板包。</summary>
    public LibraryIndexRevision ReadRevision() => LibraryIndexRevision.Read(Path, StyleTemplateLibrary.MaximumIndexBytes);
    /// <summary>立即尝试保存，锁忙或修订冲突保留原库；返回已提交的固定快照。</summary>
    public StyleTemplateLibraryLoadResult Save(LibraryIndexRevision expected, IEnumerable<StyleTemplate> templates, CancellationToken token = default)
        => Store.Save(expected, templates, token);
    /// <summary>异步等待提交锁并保存；索引发布后迟到取消仍成功。</summary>
    public Task<StyleTemplateLibraryLoadResult> SaveAsync(LibraryIndexRevision expected, IEnumerable<StyleTemplate> templates, CancellationToken token = default)
        => Store.SaveAsync(expected, templates, token);
    /// <summary>在同一提交锁内检查原修订；重复导入／固定分享不写盘或发成功变化通知。</summary>
    public Task<StyleTemplateLibraryLoadResult> CheckCurrentAsync(LibraryIndexRevision expected, CancellationToken token = default)
        => Store.CheckCurrentAsync(expected, token);
}
