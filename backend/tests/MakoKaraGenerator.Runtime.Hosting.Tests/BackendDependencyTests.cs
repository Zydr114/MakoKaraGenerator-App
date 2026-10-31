using System.Reflection;
using System.Xml.Linq;

namespace MakoKaraGenerator.Runtime.Hosting.Tests;

public sealed class BackendDependencyTests
{
    [Fact]
    public void 后端共享库不依赖GUI或执行体且仅ServerWorker可作为独立组合根()
    {
        string root = FindRepositoryRoot();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var allowedRoots = new HashSet<string>(StringComparer.Ordinal)
        { "MakoKaraGenerator.Backend.Server", "MakoKaraGenerator.Backend.Worker" };
        var actualRoots = new HashSet<string>(StringComparer.Ordinal);
        Visit(Path.Combine(root, "tests", "MakoKaraGenerator.Runtime.Hosting.Tests", "MakoKaraGenerator.Runtime.Hosting.Tests.csproj"));
        foreach (string project in Directory.GetFiles(Path.Combine(root, "src"), "*.csproj", SearchOption.AllDirectories))
        {
            var document = XDocument.Load(project);
            var references = document.Descendants("ProjectReference").Select(value => value.Attribute("Include")!.Value).ToArray();
            string name = Path.GetFileNameWithoutExtension(project);
            // Server/Worker 是真实 Runtime 的组合根；Backend.Hosting 还为 Authoring 调用 Runtime.Hosting。
            // 其余共享层仍不能直接依赖真实渲染宿主。
            if (name is not ("MakoKaraGenerator.Backend.Server" or "MakoKaraGenerator.Backend.Worker"
                or "MakoKaraGenerator.Backend.Hosting"))
                Assert.DoesNotContain(references, value => value.Contains("Runtime.Hosting", StringComparison.Ordinal));
            if (name == "MakoKaraGenerator.Core")
                Assert.DoesNotContain(references, value => value.Contains("Rendering.Skia", StringComparison.Ordinal));
            if (name == "MakoKaraGenerator.Backend.Contracts")
                Assert.Empty(references);
            bool compositionRoot = allowedRoots.Contains(name);
            if (compositionRoot)
            {
                Assert.Contains(document.Descendants("OutputType"), value => value.Value == "Exe");
                actualRoots.Add(name);
            }
            Visit(project, compositionRoot);
        }
        Assert.True(allowedRoots.SetEquals(actualRoots));
        Assert.DoesNotContain(visited, path => path.Contains("TechnicalSpike", StringComparison.Ordinal));

        void Visit(string path, bool allowCompositionRoot = false)
        {
            path = Path.GetFullPath(path);
            var document = XDocument.Load(path);
            string name = Path.GetFileNameWithoutExtension(path);
            bool executable = document.Descendants("OutputType").Any(value => value.Value is "Exe" or "WinExe");
            // Check every incoming edge before deduplication: a previously visited
            // approved root must not become an allowed library dependency.
            Assert.True(!executable || allowCompositionRoot && allowedRoots.Contains(name),
                $"Shared backend dependency cannot reference executable project {name}.");
            Assert.DoesNotContain(document.Descendants("PackageReference"), value => IsGui(value.Attribute("Include")!.Value));
            if (!visited.Add(path)) return;
            foreach (var reference in document.Descendants("ProjectReference"))
                Visit(Path.Combine(Path.GetDirectoryName(path)!, reference.Attribute("Include")!.Value));
        }
    }

    [Fact]
    public void 宿主编译后的传递程序集依赖不加载Avalonia或App()
    {
        var visited = new HashSet<string>(StringComparer.Ordinal); Visit(typeof(ProjectRenderSessionFactory).Assembly);
        Assert.DoesNotContain(AppDomain.CurrentDomain.GetAssemblies(), assembly => IsGui(assembly.GetName().Name!));

        void Visit(Assembly assembly)
        {
            if (!visited.Add(assembly.FullName!)) return;
            foreach (var name in assembly.GetReferencedAssemblies())
            {
                Assert.False(IsGui(name.Name!), $"{assembly.GetName().Name} references {name.Name}");
                if (name.Name!.StartsWith("MakoKaraGenerator.", StringComparison.Ordinal)) Visit(Assembly.Load(name));
            }
        }
    }

    private static bool IsGui(string name) => name.StartsWith("Avalonia", StringComparison.Ordinal)
        || name.StartsWith("SukiUI", StringComparison.Ordinal) || name.StartsWith("Semi.Avalonia", StringComparison.Ordinal)
        || name.StartsWith("MakoKaraGenerator.Spike", StringComparison.Ordinal);

    private static string FindRepositoryRoot()
    {
        foreach (string start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
                if (File.Exists(Path.Combine(directory.FullName, "MakoKaraGenerator.slnx"))) return directory.FullName;
        throw new DirectoryNotFoundException("Repository source is required for the project dependency check.");
    }
}
