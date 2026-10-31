namespace MakoKaraGenerator.Runtime.Hosting.Authoring;

/// <summary>一次作者试样的独立媒体目录；关闭等待生成后清理，只删除自己的目录。</summary>
public sealed class TemplateSampleLease(string root) : IAsyncDisposable
{
    private readonly object _gate = new();
    private Task<TemplateSampleResources>? _loading;
    private Task? _closing;
    /// <summary>本次唯一拥有的目录，不是调用者提供的根目录。</summary>
    public string DirectoryPath { get; } = Path.Combine(root, Guid.NewGuid().ToString("N"));

    /// <summary>最多生成一次固定媒体，重复调用共享同一任务。</summary>
    public Task<TemplateSampleResources> LoadAsync(CancellationToken cancellationToken = default) =>
        LoadAsync(TemplateSampleMedia.CreateAsync, cancellationToken);

    internal Task<TemplateSampleResources> LoadAsync(Func<string, CancellationToken, Task<TemplateSampleResources>> create, CancellationToken token)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closing is not null, this);
            return _loading ??= create(DirectoryPath, token);
        }
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        lock (_gate) return new(_closing ??= CloseAsync(_loading));
    }

    private async Task CloseAsync(Task<TemplateSampleResources>? loading)
    {
        // 创建失败仍要清理；创建任务的调用者已经得到原失败或取消。
        if (loading is not null) try { await loading.ConfigureAwait(false); } catch (Exception) { }
        await Task.Run(() => { if (Directory.Exists(DirectoryPath)) Directory.Delete(DirectoryPath, recursive: true); }).ConfigureAwait(false);
    }
}
