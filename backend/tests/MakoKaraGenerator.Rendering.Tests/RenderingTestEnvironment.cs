using System.Runtime.CompilerServices;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Rendering.Skia;
using SkiaSharp;

namespace MakoKaraGenerator.Rendering.Tests;

internal static class RenderingTestEnvironment
{
    internal static string FfmpegExecutable { get; private set; } = "ffmpeg";

    [ModuleInitializer]
    internal static void Initialize()
    {
        if (!OperatingSystem.IsWindows())
            return;

        string? resolved = FindWindowsFfmpeg();
        if (resolved is null)
            return;

        FfmpegExecutable = resolved;
        string? directory = Path.GetDirectoryName(resolved);
        if (string.IsNullOrEmpty(directory))
            return;

        string currentPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        bool alreadyPresent = currentPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(entry => string.Equals(entry.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase));
        if (!alreadyPresent)
            Environment.SetEnvironmentVariable("PATH", string.IsNullOrEmpty(currentPath) ? directory : $"{directory}{Path.PathSeparator}{currentPath}");
    }

    private static string? FindWindowsFfmpeg()
    {
        string? overridePath = Environment.GetEnvironmentVariable("MAKO_TEST_FFMPEG_PATH")
            ?? Environment.GetEnvironmentVariable("MAKO_BACKEND_FFMPEG_PATH");
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
            return overridePath;

        foreach (string? rawPath in new[]
        {
            Environment.GetEnvironmentVariable("PATH"),
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User),
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine),
        })
        {
            if (string.IsNullOrWhiteSpace(rawPath))
                continue;
            foreach (string entry in rawPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                string candidate = Path.Combine(entry.Trim().Trim('"'), "ffmpeg.exe");
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(localAppData))
        {
            string wingetLink = Path.Combine(localAppData, "Microsoft", "WinGet", "Links", "ffmpeg.exe");
            if (File.Exists(wingetLink))
                return wingetLink;

            string wingetPackages = Path.Combine(localAppData, "Microsoft", "WinGet", "Packages");
            if (Directory.Exists(wingetPackages))
            {
                foreach (string packageDir in Directory.EnumerateDirectories(wingetPackages, "Gyan.FFmpeg*"))
                {
                    string? found = Directory.EnumerateFiles(packageDir, "ffmpeg.exe", SearchOption.AllDirectories).FirstOrDefault();
                    if (found is not null)
                        return found;
                }
            }
        }

        return null;
    }
}

internal static class TestFonts
{
    internal static string Cjk => SkiaTextLayoutEngine.DefaultFamilyName;
    internal static string Emoji { get; } = OperatingSystem.IsWindows() ? "Segoe UI Emoji" : "Noto Color Emoji";
    internal static string LigatureFfi { get; } = ResolveLigatureFfi();
    internal static string LigatureLatinAndArabic { get; } = OperatingSystem.IsWindows() ? "Calibri" : "DejaVu Sans";

    internal static Project WithCjkFont(Project project) =>
        project with { LyricStyle = project.LyricStyle with { FontFamilyName = Cjk } };

    internal static ProjectSnapshot WithCjkFont(ProjectSnapshot snapshot) =>
        snapshot with { LyricStyle = snapshot.LyricStyle with { FontFamilyName = Cjk } };

    private static string ResolveLigatureFfi()
    {
        if (!OperatingSystem.IsWindows())
            return "DejaVu Sans";

        HashSet<string> families = new(SKFontManager.Default.FontFamilies, StringComparer.OrdinalIgnoreCase);
        if (families.Contains("Noto Sans SC"))
            return "Noto Sans SC";
        if (families.Contains("Calibri"))
            return "Calibri";
        return "DejaVu Sans";
    }
}
