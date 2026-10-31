namespace MakoKaraGenerator.Export;

/// <summary>已经完成编码的暂存文件；持有者必须发布或释放，不能把它当作已提交输出。</summary>
/// <remarks>仅供单一导出事务串行使用。编码会话交出此对象后不再清理该文件。</remarks>
public sealed class VideoEncodingArtifact : IDisposable
{
    private bool _disposed;
    private bool _published;

    /// <summary>接管一个编码暂存文件的所有权。</summary>
    /// <param name="temporaryPath">与最终目标位于同一目录的独有暂存文件路径。</param>
    public VideoEncodingArtifact(string temporaryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(temporaryPath);
        TemporaryPath = Path.GetFullPath(temporaryPath);
    }

    /// <summary>供验证探测使用的物理路径，不能作为用户报告中的最终路径。</summary>
    public string TemporaryPath { get; }

    /// <summary>在所有可取消工作和资源释放成功后，发布到最终路径。</summary>
    /// <param name="outputPath">最终输出路径，必须与暂存文件位于同一目录且不是暂存文件自身。</param>
    /// <param name="overwrite">是否允许覆盖；在实际文件发布时再次受检。</param>
    /// <remarks>此方法没有取消入口；成功后的迟到取消不能改变已经提交的事实。</remarks>
    public void Publish(string outputPath, bool overwrite)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_published) throw new InvalidOperationException("编码产物已经发布。");
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        string destination = Path.GetFullPath(outputPath);
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(destination, TemporaryPath, comparison)
            || !string.Equals(Path.GetDirectoryName(destination), Path.GetDirectoryName(TemporaryPath), comparison))
            throw new ArgumentException("暂存文件必须与输出位于同一目录且使用独有路径。", nameof(outputPath));

        File.Move(TemporaryPath, destination, overwrite);
        _published = true;
    }

    /// <summary>只删除本对象尚未发布的暂存文件；重复释放不触及最终输出。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (!_published) File.Delete(TemporaryPath);
    }
}
