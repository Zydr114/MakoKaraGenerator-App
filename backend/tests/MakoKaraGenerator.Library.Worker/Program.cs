using System.Diagnostics;
using System.Text.Json;
using MakoKaraGenerator.Application;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Library.Worker;

// A separate real process owns its snapshot and OS descriptor. No simulated concurrency.
internal static class Program
{
    private sealed record Payload(string Id, string Name, string? Archive = null, string? Package = null);
    private static async Task<int> Main(string[] args)
    {
        string index = args[0], mode = args[1], ready = args[3], go = args[4], result = args[5];
        Payload payload = JsonSerializer.Deserialize<Payload>(args[2])!;
        try
        {
            if (mode == "hold")
            {
                using var lease = await LibraryCommitLease.EnterAsync(index);
                File.WriteAllText(ready, "locked");
                await Task.Delay(Timeout.InfiniteTimeSpan);
                return 0;
            }
            if (mode.StartsWith("script", StringComparison.Ordinal))
            {
                var repository = new ScriptConfigurationRepository(index); var snapshot = repository.Read();
                File.WriteAllText(ready, snapshot.Revision.Sha256 ?? "missing"); await Wait(go);
                var package = await EffectPackageTransfer.ReadArchiveAsync(payload.Package!, ProjectEffectsJson.PackageProfile);
                var item = ScriptConfiguration.Create(payload.Id, payload.Name, package, "ink");
                var committed = await repository.SaveAsync(snapshot.Revision, snapshot.Configurations.Append(item));
                Report("ok", committed.Revision.Sha256);
            }
            else
            {
                var repository = new TemplateLibraryRepository(index); var snapshot = repository.Read();
                File.WriteAllText(ready, snapshot.Revision.Sha256 ?? "missing"); await Wait(go);
                IEnumerable<StyleTemplate> items = snapshot.Templates;
                if (mode == "delete") items = items.Where(item => item.Id != payload.Id);
                else if (mode == "import")
                {
                    var incoming = await StyleTemplateTransfer.ReadArchiveAsync(payload.Archive!);
                    items = items.Concat(incoming);
                }
                else
                {
                    var item = StyleTemplate.Create(payload.Id, payload.Name, StyleTemplateKind.Line, LyricStyle.Default, [LyricStyleProperty.FontSizePx]);
                    items = mode == "edit" ? items.Select(old => old.Id == payload.Id ? item : old) : items.Append(item);
                }
                var committed = await repository.SaveAsync(snapshot.Revision, items);
                Report("ok", committed.Revision.Sha256);
            }
            return 0;
        }
        catch (Exception error) when (error is IOException or ArgumentException or EffectPackageException or OperationCanceledException)
        { Report(error.GetType().Name, null); return 0; }
        void Report(string status, string? revision) => File.WriteAllText(result, JsonSerializer.Serialize(new { status, revision }));
    }
    private static async Task Wait(string path)
    {
        var timeout = Stopwatch.StartNew();
        while (!File.Exists(path) && timeout.Elapsed < TimeSpan.FromSeconds(15)) await Task.Delay(10);
        if (!File.Exists(path)) throw new IOException("Parent did not release the bounded worker gate.");
    }
}
