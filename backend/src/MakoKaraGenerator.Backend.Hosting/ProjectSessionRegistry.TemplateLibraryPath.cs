using MakoKaraGenerator.Core.Persistence;

namespace MakoKaraGenerator.Backend.Hosting;

public sealed partial class ProjectSessionRegistry
{
    private static string ResolveTemplateLibraryPath(string stateRoot)
    {
        string libraries = Path.Combine(stateRoot, "libraries");
        string current = StyleTemplateLibrary.ResolvePath(libraries);
        string previous = Path.Combine(libraries, "templates", StyleTemplateLibrary.FileName);
        int? version = StyleTemplateLibrary.ReadIndexSchemaVersion(previous);
        if (version is null || version < StyleTemplateLibrary.CurrentSchemaVersion) return current;
        if (version != StyleTemplateLibrary.CurrentSchemaVersion)
            throw new IOException("固定模板入口版本不受支持；原库保留。");
        // Any second entry can contain current user data, including an unreadable or invalid entry.
        if (LibraryIndexRevision.Read(current, StyleTemplateLibrary.MaximumIndexBytes) != LibraryIndexRevision.Missing)
            throw new IOException("存在两个可能为当前格式的模板入口；拒绝隐式选择。");
        return previous;
    }
}
