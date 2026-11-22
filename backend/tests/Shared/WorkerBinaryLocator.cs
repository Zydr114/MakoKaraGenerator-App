using System.Reflection;
using System.Runtime.Versioning;

namespace MakoKaraGenerator.TestSupport;

/// <summary>
/// 根据当前测试程序集输出目录推导编译配置与目标框架，统一定位同配置构建产物。
/// </summary>
internal static class WorkerBinaryLocator
{
    internal const string WorkerPathEnvironmentVariable = "MAKO_TEST_WORKER_PATH";

    internal static string LocateBackendWorker()
    {
        string? overridePath = Environment.GetEnvironmentVariable(WorkerPathEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            if (!overridePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                if (File.Exists(overridePath))
                    return overridePath;

                throw new FileNotFoundException(
                    $"Configured {WorkerPathEnvironmentVariable} does not exist: '{overridePath}'.",
                    overridePath);
            }

            string? directory = Path.GetDirectoryName(overridePath);
            string apphostName = WorkerExecutableFileName();
            string siblingApphost = string.IsNullOrEmpty(directory)
                ? apphostName
                : Path.Combine(directory, apphostName);
            if (File.Exists(siblingApphost))
                return siblingApphost;

            throw new FileNotFoundException(
                $"Unable to locate {apphostName} beside {WorkerPathEnvironmentVariable} ('{overridePath}') at '{siblingApphost}'.",
                siblingApphost);
        }

        (string configuration, string targetFramework) = ResolveBuildLayout();
        string backendRoot = FindBackendRoot();
        string candidate = Path.Combine(
            backendRoot,
            "src",
            "MakoKaraGenerator.Backend.Worker",
            "bin",
            configuration,
            targetFramework,
            WorkerExecutableFileName());

        if (File.Exists(candidate))
            return candidate;

        throw new FileNotFoundException(
            $"Unable to locate MakoKaraGenerator.Backend.Worker for configuration '{configuration}' and target framework '{targetFramework}' at '{candidate}'. " +
            $"Build the solution for this configuration first (e.g. `dotnet build MakoKaraGenerator.slnx --configuration {configuration}`) or set {WorkerPathEnvironmentVariable}.",
            candidate);
    }

    internal static string LocateBackendWorkerDll()
    {
        string? overridePath = Environment.GetEnvironmentVariable(WorkerPathEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            if (overridePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                if (File.Exists(overridePath))
                    return overridePath;

                throw new FileNotFoundException(
                    $"Configured {WorkerPathEnvironmentVariable} does not exist: '{overridePath}'.",
                    overridePath);
            }

            string? directory = Path.GetDirectoryName(overridePath);
            string siblingDll = string.IsNullOrEmpty(directory)
                ? "MakoKaraGenerator.Backend.Worker.dll"
                : Path.Combine(directory, "MakoKaraGenerator.Backend.Worker.dll");
            if (File.Exists(siblingDll))
                return siblingDll;

            throw new FileNotFoundException(
                $"Unable to locate MakoKaraGenerator.Backend.Worker.dll beside {WorkerPathEnvironmentVariable} ('{overridePath}') at '{siblingDll}'.",
                siblingDll);
        }

        (string configuration, string targetFramework) = ResolveBuildLayout();
        string backendRoot = FindBackendRoot();
        string candidate = Path.Combine(
            backendRoot,
            "src",
            "MakoKaraGenerator.Backend.Worker",
            "bin",
            configuration,
            targetFramework,
            "MakoKaraGenerator.Backend.Worker.dll");

        if (File.Exists(candidate))
            return candidate;

        throw new FileNotFoundException(
            $"Unable to locate MakoKaraGenerator.Backend.Worker.dll for configuration '{configuration}' and target framework '{targetFramework}' at '{candidate}'. " +
            $"Build the solution for this configuration first (e.g. `dotnet build MakoKaraGenerator.slnx --configuration {configuration}`) or set {WorkerPathEnvironmentVariable}.",
            candidate);
    }

    internal static string LocateBackendWorkerScriptReferences()
    {
        string? overridePath = Environment.GetEnvironmentVariable(WorkerPathEnvironmentVariable);
        (string configuration, string targetFramework) = ResolveBuildLayout();

        string workerDirectory;
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            if (!File.Exists(overridePath))
            {
                throw new FileNotFoundException(
                    $"Configured {WorkerPathEnvironmentVariable} does not exist: '{overridePath}'.",
                    overridePath);
            }

            workerDirectory = Path.GetDirectoryName(Path.GetFullPath(overridePath))
                ?? throw new DirectoryNotFoundException($"Unable to determine directory for {WorkerPathEnvironmentVariable} ('{overridePath}').");
        }
        else
        {
            workerDirectory = Path.Combine(
                FindBackendRoot(),
                "src",
                "MakoKaraGenerator.Backend.Worker",
                "bin",
                configuration,
                targetFramework);
        }

        string scriptRefs = Path.Combine(workerDirectory, "script-refs");
        if (Directory.Exists(scriptRefs))
            return scriptRefs;

        throw new DirectoryNotFoundException(
            $"Unable to locate Worker script-refs for configuration '{configuration}' and target framework '{targetFramework}' at '{scriptRefs}'. " +
            $"Build the solution for this configuration first (e.g. `dotnet build MakoKaraGenerator.slnx --configuration {configuration}`) or set {WorkerPathEnvironmentVariable}.");
    }

    internal static string LocateBackendServerDll()
    {
        (string configuration, string targetFramework) = ResolveBuildLayout();
        string backendRoot = FindBackendRoot();
        string candidate = Path.Combine(
            backendRoot,
            "src",
            "MakoKaraGenerator.Backend.Server",
            "bin",
            configuration,
            targetFramework,
            "MakoKaraGenerator.Backend.Server.dll");

        if (File.Exists(candidate))
            return candidate;

        throw new FileNotFoundException(
            $"Unable to locate MakoKaraGenerator.Backend.Server.dll for configuration '{configuration}' and target framework '{targetFramework}' at '{candidate}'. " +
            $"Build the solution for this configuration first (e.g. `dotnet build MakoKaraGenerator.slnx --configuration {configuration}`).",
            candidate);
    }

    internal static string FindBackendRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "MakoKaraGenerator.slnx")))
            current = current.Parent;

        return current?.FullName
            ?? throw new DirectoryNotFoundException(
                $"Unable to locate backend root containing MakoKaraGenerator.slnx from '{AppContext.BaseDirectory}'.");
    }

    internal static (string Configuration, string TargetFramework) ResolveBuildLayout()
    {
        string baseDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory));
        string? configuration = null;
        string? targetFramework = null;

        List<string> segmentsAfterBin = [];
        DirectoryInfo? cursor = new(baseDirectory);
        bool foundBin = false;
        while (cursor is not null)
        {
            if (string.Equals(cursor.Name, "bin", StringComparison.OrdinalIgnoreCase))
            {
                foundBin = true;
                break;
            }

            segmentsAfterBin.Add(cursor.Name);
            if (File.Exists(Path.Combine(cursor.FullName, "MakoKaraGenerator.slnx")))
                break;

            cursor = cursor.Parent;
        }

        if (foundBin)
        {
            segmentsAfterBin.Reverse();
            if (segmentsAfterBin.Count >= 2 && LooksLikeTargetFramework(segmentsAfterBin[1]))
            {
                configuration = segmentsAfterBin[0];
                targetFramework = segmentsAfterBin[1];
            }
            else if (segmentsAfterBin.Count >= 3 && LooksLikeTargetFramework(segmentsAfterBin[2]))
            {
                configuration = segmentsAfterBin[1];
                targetFramework = segmentsAfterBin[2];
            }
            else if (segmentsAfterBin.Count >= 2)
            {
                configuration = segmentsAfterBin[0];
                targetFramework = segmentsAfterBin[1];
            }
        }

        Assembly assembly = typeof(WorkerBinaryLocator).Assembly;
        if (string.IsNullOrWhiteSpace(configuration))
            configuration = assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration;

        if (string.IsNullOrWhiteSpace(targetFramework))
            targetFramework = TryParseTargetFramework(assembly.GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName);

        if (string.IsNullOrWhiteSpace(configuration) || string.IsNullOrWhiteSpace(targetFramework))
        {
            throw new InvalidOperationException(
                $"Unable to determine build configuration and target framework from AppContext.BaseDirectory '{AppContext.BaseDirectory}'.");
        }

        return (configuration, targetFramework);
    }

    private static string WorkerExecutableFileName()
        => "MakoKaraGenerator.Backend.Worker" + (OperatingSystem.IsWindows() ? ".exe" : string.Empty);

    private static bool LooksLikeTargetFramework(string segment)
        => segment.StartsWith("net", StringComparison.OrdinalIgnoreCase) && segment.Contains('.', StringComparison.Ordinal);

    private static string? TryParseTargetFramework(string? frameworkName)
    {
        if (string.IsNullOrWhiteSpace(frameworkName))
            return null;

        const string prefix = ".NETCoreApp,Version=v";
        if (frameworkName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return "net" + frameworkName[prefix.Length..];

        return null;
    }
}
