using System.Buffers.Binary;
using System.Runtime.Versioning;
using System.Text;
using MakoKaraGenerator.Core.Media;
using MakoKaraGenerator.Processes.MediaEnvelope;

namespace MakoKaraGenerator.Media;

public static partial class MediaPresentationDeploymentDiscovery
{
    internal const long WindowsNativeLimitBytes = 128L * 1024 * 1024;
    private const long WindowsClosureScratchBytes = 4L * 1024 * 1024;
    private static readonly EnvelopeQuarantineStore WindowsDiscoveryQuarantine = new();
    private static readonly UTF8Encoding WindowsClosureEncoding = new(false, true);

    [SupportedOSPlatform("windows")]
    private static async Task<MediaPresentationDeployment> DiscoverWindowsAsync(string toolDirectory, CancellationToken token)
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(toolDirectory));
        string helper = Path.Combine(root, WindowsHelperFileName);
        string system = Environment.SystemDirectory;
        const long managed = WindowsClosureScratchBytes + DecodeOperationBudget.TransportReservationPerProducer;
        var operation = new DecodeOperationBudget(WindowsNativeLimitBytes + managed, managed, [WindowsNativeLimitBytes], 1,
            new WindowsDiscoveryLifetime());
        var owner = new OwnedNativeEnvelope(new WindowsJobEnvelopeBackend(), WindowsDiscoveryQuarantine,
            new(TimeSpan.FromSeconds(5), MediaAbCapacityV1.ProbeTimeout, TimeSpan.FromSeconds(2),
                MediaAbCapacityV1.OwnedDrainTimeout, TimeSpan.FromMilliseconds(10)));
        MediaPresentationDeployment? deployment = null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(MediaAbCapacityV1.ProbeTimeout);
        try
        {
            await owner.RunAsync(operation.BeginNative(0), new(helper, helper, ["--closure"]), async (session, ct) =>
            {
                using var scratch = operation.ReserveManaged(WindowsClosureScratchBytes);
                deployment = await ReadWindowsClosureAsync(session.Output, helper, root, system, ct).ConfigureAwait(false);
                await MediaPresentationWire.RequireEofAsync(session.Output, session.Metadata, ct).ConfigureAwait(false);
            }, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw Unsupported("Controlled Windows loader closure inspection timed out."); }
        catch (EnvelopeException error)
        { throw Unsupported("Controlled Windows loader closure could not be verified: " + error.SafeCode); }
        finally { await operation.ReleaseSourceAsync().ConfigureAwait(false); }
        return deployment ?? throw Unsupported("Controlled Windows loader closure was not reported.");
    }

    private static async Task<MediaPresentationDeployment> ReadWindowsClosureAsync(Stream stream, string helper,
        string root, string system, CancellationToken token)
    {
        byte[] buffer = new byte[MediaAbCapacityV1.MaximumMetadataRecordBytes];
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dependencies = new List<MediaLoadedToolFile>();
        MediaLoadedToolFile? executable = null;
        int totalBytes = 0;
        while (true)
        {
            await MediaPresentationWire.ReadExactlyAsync(stream, buffer.AsMemory(0, 16), token).ConfigureAwait(false);
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(8));
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(12));
            if (!buffer.AsSpan(0, 8).SequenceEqual("MKGPRE1\0"u8) || size > buffer.Length
                || (totalBytes += (int)size + 16) > MediaAbCapacityV1.MaximumProbeOutputBytes)
                throw Unsupported("Controlled Windows loader closure exceeds its framing or output capacity.");
            if (type == 5 && size == 0 && executable is not null && dependencies.Count > 0)
                return MediaPresentationDeployment.ForWindows(executable.TrustedPath, executable, dependencies);
            if (type != 4 || size <= 44 || size > 44 + 4096 || paths.Count >= MediaAbCapacityV1.MaximumToolManifestEntries)
                throw Unsupported("Controlled Windows loader closure record is invalid.");
            await MediaPresentationWire.ReadExactlyAsync(stream, buffer.AsMemory(0, (int)size), token).ConfigureAwait(false);
            long length = BinaryPrimitives.ReadInt64LittleEndian(buffer);
            if (length <= 0 || BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(40)) != size - 44)
                throw Unsupported("Controlled Windows loader closure record fields are invalid.");
            string path;
            try { path = WindowsClosureEncoding.GetString(buffer, 44, (int)size - 44); }
            catch (DecoderFallbackException) { throw Unsupported("Controlled Windows module path is not valid UTF-8."); }
            if (path.Contains('\0') || !Path.IsPathFullyQualified(path) || !paths.Add(path) || Path.GetFullPath(path) != path)
                throw Unsupported("Controlled Windows module path is invalid or duplicated.");
            bool isHelper = path.Equals(helper, StringComparison.OrdinalIgnoreCase);
            string? directory = Path.GetDirectoryName(path);
            bool isSystem = string.Equals(directory, system, StringComparison.OrdinalIgnoreCase);
            bool isPackaged = string.Equals(directory, root, StringComparison.OrdinalIgnoreCase);
            if (!isHelper && (!(isSystem || isPackaged) || !Path.GetExtension(path).Equals(".dll", StringComparison.OrdinalIgnoreCase)))
                throw Unsupported("Controlled Windows module is outside the packaged directory and System32.");
            string logicalId = isHelper ? "presentation-helper" : (isSystem ? "system-" : "app-") + Path.GetFileName(path).ToLowerInvariant();
            string hash = Convert.ToHexStringLower(buffer.AsSpan(8, 32));
            MediaLoadedToolFile pinned = await PinAsync(path, logicalId, token).ConfigureAwait(false);
            if (pinned.ManifestEntry.ByteLength != length || pinned.ManifestEntry.ContentSha256.LowerHex != hash)
                throw Unsupported("Controlled Windows module bytes changed during loader closure inspection.");
            if (isHelper) executable = pinned;
            else dependencies.Add(pinned);
        }
    }

    private sealed class WindowsDiscoveryLifetime : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
