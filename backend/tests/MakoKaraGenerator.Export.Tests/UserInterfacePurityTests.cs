using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace MakoKaraGenerator.Export.Tests;

/// <summary>
/// 后端仓库边界守卫测试：确保无窗口后端不再引入已移除的界面框架或桌面播放依赖。
/// </summary>
public sealed class UserInterfacePurityTests
{
    private static readonly string[] BackendSourceDirectories = ["src", "tests"];

    [Fact]
    public void 后端仓库不包含已移除的桌面界面或播放依赖()
    {
        string repositoryRoot = FindRepositoryRoot();
        string solution = File.ReadAllText(Path.Combine(repositoryRoot, "MakoKaraGenerator.slnx"));

        Assert.DoesNotContain("samples/TechnicalSpike.App", solution, StringComparison.Ordinal);
        foreach (string root in BackendSourceDirectories.Select(name => Path.Combine(repositoryRoot, name)))
        {
            foreach (string project in Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories))
            {
                var references = XDocument.Load(project).Descendants()
                    .Where(element => element.Name.LocalName is "PackageReference" or "ProjectReference" or "Reference")
                    .Select(element => (string?)element.Attribute("Include") ?? string.Empty);
                Assert.DoesNotContain(references, reference =>
                    reference.Contains("Avalonia", StringComparison.OrdinalIgnoreCase)
                    || reference.Contains("SukiUI", StringComparison.OrdinalIgnoreCase)
                    || reference.Contains("TechnicalSpike.App", StringComparison.OrdinalIgnoreCase)
                    || reference.Contains("SoundFlow", StringComparison.OrdinalIgnoreCase));
            }

            foreach (string file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
                .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
            {
                string text = File.ReadAllText(file);
                Assert.False(Regex.IsMatch(text, @"^\s*(?:global\s+)?using\s+(?:Avalonia|SukiUI|SoundFlow)(?:\.|;)", RegexOptions.Multiline),
                    $"后端源码引用了已移除的界面或播放命名空间：{Path.GetRelativePath(repositoryRoot, file)}");
            }
        }
    }

    /// <summary>从测试程序集所在目录往上找仓库根。</summary>
    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        StringBuilder visited = new();

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "MakoKaraGenerator.slnx")))
            {
                return directory.FullName;
            }

            visited.Append(directory.FullName).Append(" → ");
            directory = directory.Parent;
        }

        throw new InvalidOperationException($"从测试目录往上找不到仓库根（MakoKaraGenerator.slnx）：{visited}");
    }
}
