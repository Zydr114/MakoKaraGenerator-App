using System.Reflection;
using MakoKaraGenerator.Application.Editing;

namespace MakoKaraGenerator.Application.Tests;

public sealed class EditingBackendDependencyTests
{
    [Fact]
    public void 共享歌词时间用例的编译后传递依赖不含GUI或可执行App()
    {
        HashSet<string> visited = new(StringComparer.Ordinal);
        Visit(typeof(LyricTimingEditUseCase).Assembly);
        Visit(typeof(LyricContentEditUseCase).Assembly);
        Visit(typeof(LyricsEditIdAllocator).Assembly);
        Visit(typeof(LyricsImportEditUseCase).Assembly);
        Visit(typeof(ProjectSettingsEditUseCase).Assembly);
        Visit(typeof(LyricBranchEditUseCase).Assembly);
        Assert.DoesNotContain(AppDomain.CurrentDomain.GetAssemblies(), assembly => IsGui(assembly.GetName().Name!));

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

    private static bool IsGui(string name) => name.StartsWith("Avalonia", StringComparison.Ordinal)
        || name.StartsWith("SukiUI", StringComparison.Ordinal)
        || name.StartsWith("Semi.Avalonia", StringComparison.Ordinal)
        || name.StartsWith("MakoKaraGenerator.Spike", StringComparison.Ordinal);
}
