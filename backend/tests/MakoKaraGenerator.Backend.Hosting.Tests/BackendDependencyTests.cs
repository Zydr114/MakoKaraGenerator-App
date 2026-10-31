using System.Reflection;
using System.Xml.Linq;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

public sealed class BackendDependencyTests
{
    [Fact]
    public void 新建宿主与其单测完整项目图不依赖GUI或可执行项目且Application仍只依赖Core()
    {
        string root = FindRepositoryRoot();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        Visit(Path.Combine(root, "tests", "MakoKaraGenerator.Backend.Hosting.Tests", "MakoKaraGenerator.Backend.Hosting.Tests.csproj"));
        string application = Path.Combine(root, "src", "MakoKaraGenerator.Application", "MakoKaraGenerator.Application.csproj");
        var references = XDocument.Load(application).Descendants("ProjectReference").ToArray();
        Assert.Single(references);
        Assert.EndsWith("MakoKaraGenerator.Core.csproj", references[0].Attribute("Include")!.Value, StringComparison.Ordinal);

        void Visit(string path)
        {
            path = Path.GetFullPath(path);
            if (!visited.Add(path)) return;
            var document = XDocument.Load(path);
            Assert.DoesNotContain(document.Descendants("OutputType"), value => value.Value is "Exe" or "WinExe");
            Assert.DoesNotContain(document.Descendants("PackageReference"), value => IsGui(value.Attribute("Include")!.Value));
            Assert.False(path.Contains("TechnicalSpike", StringComparison.Ordinal));
            foreach (var reference in document.Descendants("ProjectReference"))
                Visit(Path.Combine(Path.GetDirectoryName(path)!, reference.Attribute("Include")!.Value));
        }
    }

    [Fact]
    public void 编译后传递程序集无GUI且准备类型不暴露播放设备()
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        Visit(typeof(NewProjectPreparationService).Assembly);
        Assert.DoesNotContain(AppDomain.CurrentDomain.GetAssemblies(), assembly => IsGui(assembly.GetName().Name!));
        var propertyTypes = typeof(PreparedNewProjectData).GetProperties().Select(property => property.PropertyType.Name);
        Assert.DoesNotContain(propertyTypes, name => name.Contains("Playback", StringComparison.Ordinal));

        void Visit(Assembly assembly)
        {
            if (!visited.Add(assembly.FullName!)) return;
            foreach (AssemblyName name in assembly.GetReferencedAssemblies())
            {
                Assert.False(IsGui(name.Name!), $"{assembly.GetName().Name} references {name.Name}");
                if (name.Name!.StartsWith("MakoKaraGenerator.", StringComparison.Ordinal)) Visit(Assembly.Load(name));
            }
        }
    }

    [Fact]
    public void Contracts传输程序集不依赖CoreApplication或GUI()
    {
        string root = FindRepositoryRoot();
        string path = Path.Combine(root, "src", "MakoKaraGenerator.Backend.Contracts", "MakoKaraGenerator.Backend.Contracts.csproj");
        var document = XDocument.Load(path);
        Assert.Empty(document.Descendants("ProjectReference"));
        Assert.DoesNotContain(document.Descendants("PackageReference"), value => IsGui(value.Attribute("Include")!.Value));
    }

    private static bool IsGui(string name) => name.StartsWith("Avalonia", StringComparison.Ordinal)
        || name.StartsWith("SukiUI", StringComparison.Ordinal) || name.StartsWith("MakoKaraGenerator.Spike", StringComparison.Ordinal)
        || name.StartsWith("Semi.Avalonia", StringComparison.Ordinal);

    private static string FindRepositoryRoot()
    {
        foreach (string start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
                if (File.Exists(Path.Combine(directory.FullName, "MakoKaraGenerator.slnx"))) return directory.FullName;
        throw new DirectoryNotFoundException("Repository source is required for the project dependency check.");
    }
}
