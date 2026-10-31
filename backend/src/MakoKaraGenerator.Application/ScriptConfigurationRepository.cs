using System.Collections.Immutable;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Persistence;

namespace MakoKaraGenerator.Application;

/// <summary>独立脚本配置库采用同一修订／OS锁协议；格式与模板库独立。</summary>
public sealed class ScriptConfigurationRepository
{
    private readonly LibraryRepository<ScriptConfiguration, ScriptConfigurationLibraryLoadResult> _store;
    /// <summary>为指定配置入口创建仓储。</summary>
    public ScriptConfigurationRepository(string path)
    {
        _store = new(path, p => ScriptConfigurationLibrary.Load(p), s => s.Revision,
            s => s.VersionMismatch || s.Issues.Count != 0,
            (p, items, expected, token) => new(items.ToImmutableArray(), [])
            { Revision = ScriptConfigurationLibrary.Save(p, items, expected, cancellationToken: token), SchemaVersion = ScriptConfigurationLibrary.CurrentSchemaVersion });
    }
    /// <summary>规范配置入口。</summary>
    public string Path => _store.Path;
    /// <summary>成功发布不同修订后通知一次；订阅者自行转入所属线程。</summary>
    public event Action<ScriptConfigurationLibraryLoadResult>? Changed { add => _store.Changed += value; remove => _store.Changed -= value; }
    /// <summary>读取固定内容、实际字节修订及完整诊断。</summary>
    public ScriptConfigurationLibraryLoadResult Read() => _store.Read();
    /// <summary>立即尝试保存；锁忙、修订冲突或闭包失败均不覆盖原库。</summary>
    public ScriptConfigurationLibraryLoadResult Save(LibraryIndexRevision expected, IEnumerable<ScriptConfiguration> configurations, CancellationToken token = default)
        => _store.Save(expected, configurations, token);
    /// <summary>异步等待协作锁并保存，提交之后迟到取消如实成功。</summary>
    public Task<ScriptConfigurationLibraryLoadResult> SaveAsync(LibraryIndexRevision expected, IEnumerable<ScriptConfiguration> configurations, CancellationToken token = default)
        => _store.SaveAsync(expected, configurations, token);
}
