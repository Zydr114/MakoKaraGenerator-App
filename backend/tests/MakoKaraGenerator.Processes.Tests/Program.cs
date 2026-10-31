using System.Diagnostics;

namespace MakoKaraGenerator.Processes.Tests;

internal static class Program
{
    private static readonly string[] FfmpegArguments = ["-hide_banner", "-loglevel", "error", "-nostdin", "-re", "-f", "lavfi",
        "-i", "color=c=black:s=16x16:r=1", "-t", "3", "-f", "null", "-"];
    internal static async Task<int> Main(string[] args)
    {
        if (args.Length < 2 || args[0] != "--owned-child") return 64;
        switch (args[1])
        {
            case "file-size" when args.Length == 3:
                const long maximum = 64 * 1024 * 1024;
                if (!LinuxProcessGuards.InitializeCurrentProcessGroup() || !LinuxProcessGuards.ApplyCurrentFileSizeLimit(maximum)) return 65;
                try
                {
                    using FileStream output = new(args[2], FileMode.CreateNew, FileAccess.Write);
                    byte[] buffer = new byte[1024 * 1024];
                    for (int index = 0; index < 65; index++) output.Write(buffer);
                    return 66;
                }
                catch (IOException)
                {
                    Console.WriteLine("guard-rejected-write");
                    return 0;
                }
            case "group-parent" when args.Length == 2:
                if (!LinuxProcessGuards.InitializeCurrentProcessGroup()) return 65;
                using (Process child = StartFfmpeg())
                {
                    Console.WriteLine(child.Id);
                    // The test stops this actual private FFmpeg and asks the
                    // leader to exit, reproducing a root-gone live descendant.
                    if (await Console.In.ReadLineAsync() != "exit") return 64;
                    return 0;
                }
            case "independent" when args.Length == 2:
                await Task.Delay(1000);
                return 7;
            default:
                return 64;
        }
    }

    private static Process StartFfmpeg()
    {
        ProcessStartInfo info = new("ffmpeg") { UseShellExecute = false };
        foreach (string argument in FfmpegArguments) info.ArgumentList.Add(argument);
        return Process.Start(info) ?? throw new IOException("Owned FFmpeg failed to start.");
    }
}
