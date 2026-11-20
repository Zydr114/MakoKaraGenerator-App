using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using MakoKaraGenerator.Processes.MediaEnvelope;

namespace MakoKaraGenerator.Processes.Tests;

internal static partial class Program
{
    internal const string BinaryArgument = "closure mode \"\u5A92\u4F53\" \\";
    internal const string PrivateEnvironment = "MAKO_MEDIA_TEST_PRIVATE_PARENT";
    internal static string DotnetExecutable => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(typeof(object).Assembly.Location)!,
        "..", "..", "..", "dotnet.exe"));

    [SupportedOSPlatform("windows")]
    private static async Task<int> RunMediaChildAsync(string[] args)
    {
        switch (args)
        {
            case ["--closure", BinaryArgument, var sentinel]:
                if (Environment.GetEnvironmentVariable(PrivateEnvironment) is not null || Environment.GetEnvironmentVariable("PATH") is not null
                    || !nuint.TryParse(Environment.GetEnvironmentVariable("MAKO_MEDIA_METADATA_HANDLE"), NumberStyles.None,
                        CultureInfo.InvariantCulture, out nuint metadataValue) || !nuint.TryParse(sentinel, out nuint sentinelValue)) return 64;
                if (MediaChildNative.SetEvent(unchecked((nint)sentinelValue))) return 65;
                using (var metadata = new FileStream(new SafeFileHandle(unchecked((nint)metadataValue), ownsHandle: true), FileAccess.Write))
                {
                    metadata.Write([0, 0x1a, 0xff]);
                    Console.OpenStandardError().Write([0xff, 0, 0x1a]);
                    byte[] input = new byte[24_000];
                    await Console.OpenStandardInput().ReadExactlyAsync(input);
                    await Console.OpenStandardOutput().WriteAsync(input);
                }
                return 0;
            case ["corrupt"]:
                if (!TryCommit(88)) return 70;
                Console.OpenStandardOutput().WriteByte(0xfb);
                return unchecked((int)0xc0000017);
            case ["process-limit"]:
                Console.OpenStandardOutput().WriteByte(0xfa);
                return await ProbeCommitAsync(129);
            case ["job-limit"]:
                if (!TryCommit(68)) return 70;
                Console.OpenStandardOutput().WriteByte(0xfa);
                using (Process child = StartMediaChild("allocate"))
                {
                    await child.WaitForExitAsync();
                    return child.ExitCode;
                }
            case ["allocate"]:
                return await ProbeCommitAsync(68);
            case ["sleep"]:
                await Task.Delay(TimeSpan.FromSeconds(30));
                return 0;
            case ["nested"]:
                if (!WindowsJobObjectGuard.IsCurrentProcessGuarded()) return 64;
                const long limit = 128 * 1024 * 1024;
                const long managed = DecodeOperationBudget.TransportReservationPerProducer;
                var budget = new DecodeOperationBudget(limit + managed, managed, [limit], 1, new NestedSource());
                var quarantine = new EnvelopeQuarantineStore();
                var runner = new OwnedNativeEnvelope(new WindowsJobEnvelopeBackend(), quarantine, new(TimeSpan.FromSeconds(5),
                    TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(15)));
                var completion = await runner.RunAsync(budget.BeginNative(0), new(DotnetExecutable, DotnetExecutable, MediaChildArguments(["tails"])),
                    (_, _) => Task.CompletedTask, CancellationToken.None);
                await budget.ReleaseSourceAsync();
                return completion.ExitCode != 0 || quarantine.Count != 0 ? 65 : 0;
            case ["orphan"]:
                using (Process child = StartMediaChild("sleep"))
                {
                    Console.OpenStandardOutput().Write(BitConverter.GetBytes(child.Id));
                    _ = Console.OpenStandardInput().ReadByte();
                    return 0;
                }
            case ["tails"]:
                using (var metadata = new FileStream(new SafeFileHandle(unchecked((nint)nuint.Parse(
                    Environment.GetEnvironmentVariable("MAKO_MEDIA_METADATA_HANDLE")!, CultureInfo.InvariantCulture)), ownsHandle: true), FileAccess.Write))
                {
                    byte[] tail = new byte[32_768];
                    metadata.Write(tail);
                    Console.OpenStandardOutput().Write(tail);
                    Console.OpenStandardError().Write(tail);
                    return 0;
                }
            default:
                return 64;
        }
    }

    private static Process StartMediaChild(string mode)
    {
        var info = new ProcessStartInfo(DotnetExecutable) { UseShellExecute = false, CreateNoWindow = true };
        foreach (string argument in MediaChildArguments([mode])) info.ArgumentList.Add(argument);
        return Process.Start(info) ?? throw new IOException("Media regression child did not start.");
    }

    internal static string[] MediaChildArguments(string[] args) => [Assembly.GetExecutingAssembly().Location, "--owned-child", "win-media", .. args];

    private static bool TryCommit(int mib) => MediaChildNative.VirtualAlloc(0, (nuint)mib * 1024 * 1024, 0x3000, 0x04) != 0;

    private static async Task<int> ProbeCommitAsync(int mib)
    {
        bool committed = TryCommit(mib);
        await Task.Delay(100);
        return committed ? 71 : 72;
    }

    private sealed class NestedSource : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    internal static partial class MediaChildNative
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct SecurityAttributes
        {
            internal int Length;
            internal nint Descriptor;
            internal int Inherit;
        }

        [LibraryImport("kernel32.dll")]
        internal static partial nint VirtualAlloc(nint address, nuint size, uint allocationType, uint protection);

        [LibraryImport("kernel32.dll", EntryPoint = "CreateEventW", SetLastError = true)]
        internal static partial SafeWaitHandle CreateEvent(ref SecurityAttributes attributes, int manualReset, int initialState, nint name);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool SetEvent(nint handle);
    }
}
