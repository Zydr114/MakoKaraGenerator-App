namespace MakoKaraGenerator.TestSupport;

/// <summary>
/// 测试用的临时目录，测试结束自动删除。
/// </summary>
internal sealed class TemporaryDirectory : IDisposable
{
    public TemporaryDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "makokara-tests",
            Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string FilePath(string fileName) => System.IO.Path.Combine(Path, fileName);

    public void WriteFile(string fileName, string content) =>
        System.IO.File.WriteAllText(FilePath(fileName), content);

    public string ReadFile(string fileName) => System.IO.File.ReadAllText(FilePath(fileName));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
        catch (IOException)
        {
            // 测试清理失败不应让测试结果变成失败。
        }
    }
}
