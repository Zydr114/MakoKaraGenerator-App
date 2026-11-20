using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using MakoKaraGenerator.Core.Media;
using MakoKaraGenerator.Processes;

namespace MakoKaraGenerator.Media;

/// <summary>Builds the pinned presentation deployment from the shipped helper and its platform loader closure.</summary>
/// <remarks>
/// Linux resolves the <c>dl_iterate_phdr</c> image set through <c>ldd</c> and canonical realpaths.
/// Windows measures its shipped helper's loaded images under the same hard Job used for decoding.
/// </remarks>
public static partial class MediaPresentationDeploymentDiscovery
{
    /// <summary>Gate executable file name inside the tool directory.</summary>
    public const string GateFileName = "mkg-media-exec-gate";
    /// <summary>Presentation helper file name inside the tool directory.</summary>
    public const string HelperFileName = "mkg-presentation";
    /// <summary>Presentation helper executable inside the Windows tool directory.</summary>
    public const string WindowsHelperFileName = "mkg-presentation.exe";
    /// <summary>Envelope bridge library file name inside the tool directory.</summary>
    public const string BridgeFileName = "libmkg_media_envelope.so";

    /// <summary>Whether the directory carries the complete shipped native media tools.</summary>
    public static bool IsInstalled(string toolDirectory) => WindowsJobObjectGuard.IsSupported
        ? File.Exists(Path.Combine(toolDirectory, WindowsHelperFileName))
        : OperatingSystem.IsLinux() && File.Exists(Path.Combine(toolDirectory, GateFileName))
            && File.Exists(Path.Combine(toolDirectory, HelperFileName)) && File.Exists(Path.Combine(toolDirectory, BridgeFileName));

    /// <summary>Resolves, hashes and pins the gate, helper and every helper loader dependency.</summary>
    public static async Task<MediaPresentationDeployment> DiscoverAsync(string toolDirectory, CancellationToken cancellationToken)
    {
        if (WindowsJobObjectGuard.IsSupported)
            return await DiscoverWindowsAsync(toolDirectory, cancellationToken).ConfigureAwait(false);
        if (!OperatingSystem.IsLinux()) throw Unsupported("Controlled media decoding requires Linux or Windows x64.");
        string root = RealPath(Path.GetFullPath(toolDirectory));
        string gate = RealPath(Path.Combine(root, GateFileName)), helper = RealPath(Path.Combine(root, HelperFileName));
        string[] dependencies = (await ReadLoaderClosureAsync(helper, cancellationToken).ConfigureAwait(false))
            .Select(RealPath).Where(path => path != helper && path != gate).Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal).ToArray();
        if (dependencies.Length is 0 or > MediaAbCapacityV1.MaximumToolManifestEntries - 2)
            throw Unsupported("Presentation helper loader closure is empty or exceeds the manifest capacity.");
        MediaLoadedToolFile[] executables =
        [
            await PinAsync(gate, "exec-gate", cancellationToken).ConfigureAwait(false),
            await PinAsync(helper, "presentation-helper", cancellationToken).ConfigureAwait(false),
        ];
        var loaded = new MediaLoadedToolFile[dependencies.Length];
        for (int index = 0; index < dependencies.Length; index++)
            loaded[index] = await PinAsync(dependencies[index], "loaded-" + index.ToString("D4", CultureInfo.InvariantCulture),
                cancellationToken).ConfigureAwait(false);
        return new MediaPresentationDeployment(gate, helper, executables, loaded);
    }

    private static async Task<string[]> ReadLoaderClosureAsync(string helper, CancellationToken token)
    {
        var start = new ProcessStartInfo("ldd") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(helper);
        start.Environment["LC_ALL"] = "C";
        using var process = Process.Start(start) ?? throw Unsupported("Loader closure inspection could not start.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            Task<string> output = process.StandardOutput.ReadToEndAsync(deadline.Token);
            Task<string> errors = process.StandardError.ReadToEndAsync(deadline.Token);
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            string text = await output.ConfigureAwait(false); _ = await errors.ConfigureAwait(false);
            if (process.ExitCode != 0) throw Unsupported("Loader closure inspection failed.");
            List<string> paths = [];
            foreach (string raw in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (raw.StartsWith("linux-vdso", StringComparison.Ordinal) || raw.StartsWith("linux-gate", StringComparison.Ordinal)) continue;
                if (raw.Contains("not found", StringComparison.Ordinal)) throw Unsupported("Presentation helper has an unresolved loader dependency.");
                int arrow = raw.IndexOf("=> ", StringComparison.Ordinal);
                string candidate = arrow >= 0 ? raw[(arrow + 3)..] : raw;
                int address = candidate.LastIndexOf(" (", StringComparison.Ordinal);
                if (address > 0) candidate = candidate[..address];
                candidate = candidate.Trim();
                if (candidate.Length == 0 || !Path.IsPathFullyQualified(candidate)) throw Unsupported("Loader closure line is not an absolute library path.");
                paths.Add(candidate);
            }
            return [.. paths];
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            try { process.Kill(); } catch (InvalidOperationException) { }
            throw Unsupported("Loader closure inspection timed out.");
        }
    }

    private static async Task<MediaLoadedToolFile> PinAsync(string path, string logicalId, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        long length = stream.Length;
        if (length <= 0) throw Unsupported("Controlled decoder deployment file is empty.");
        string hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
        if (stream.Length != length) throw Unsupported("Controlled decoder deployment file changed while pinned.");
        return new(path, new(logicalId, length, new(hash)));
    }

    /// <summary>Canonical absolute path with every symbolic link component resolved, matching libc realpath.</summary>
    internal static string RealPath(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw Unsupported("Deployment path must be absolute.");
        var pending = new Stack<string>(path.Split('/', StringSplitOptions.RemoveEmptyEntries).Reverse());
        string current = "/"; int links = 0;
        while (pending.Count > 0)
        {
            string part = pending.Pop();
            if (part == ".") continue;
            if (part == "..") { current = Path.GetDirectoryName(current) ?? "/"; continue; }
            string next = current == "/" ? "/" + part : current + "/" + part;
            FileSystemInfo info = Directory.Exists(next) ? new DirectoryInfo(next) : new FileInfo(next);
            if (!info.Exists) throw Unsupported("Deployment path component does not exist.");
            string? target = info.LinkTarget;
            if (target is null) { current = next; continue; }
            if (++links > 40) throw Unsupported("Deployment path has too many symbolic links.");
            if (target.StartsWith('/')) current = "/";
            foreach (string item in target.Split('/', StringSplitOptions.RemoveEmptyEntries).Reverse()) pending.Push(item);
        }
        return current;
    }

    private static MediaAbException Unsupported(string detail) => new(MediaAbFailure.ProfileUnsupported, detail);
}
