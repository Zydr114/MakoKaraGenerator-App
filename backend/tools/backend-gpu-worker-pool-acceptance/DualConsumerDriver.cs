using System.Text.Json;
using MakoKaraGenerator.Application;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Hosting;
using MakoKaraGenerator.Backend.Rendering;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Scripting.Compilation;

// Trusted scratch stdio driver. Claim results contain private broker capability
// data and travel only over the owned pipe to the same Electron parent, never logs.
static class DualConsumerDriver
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static async Task RunAsync(string[] args)
    {
        string Option(string key) => args[Array.IndexOf(args, key) + 1];
        string worker = Path.GetFullPath(Option("--worker")), output = Path.GetFullPath(Option("--output"));
        if (!output.Contains("/.local/", StringComparison.Ordinal) ||
            !worker.StartsWith(Path.Combine(output, "dist") + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new ArgumentException("Dual driver requires output/dist isolated Worker.");
        int mainPid = int.Parse(Option("--main-pid"), System.Globalization.CultureInfo.InvariantCulture);
        var snapshot = args.Contains("--snapshot", StringComparer.Ordinal)
            ? ProjectSnapshotSourceProfile.Decode(await File.ReadAllBytesAsync(Option("--snapshot")))
            : ProjectSnapshot.FromProject(ProjectWorkspace.CreateNew("M04 dual consumer").Project);
        using var capture = BackendProjectRuntimeInputLease.CreateDetached(snapshot);
        var prepared = await capture.PrepareAsync();
        if (!prepared.Accepted) throw new InvalidOperationException("Dual input preparation failed.");
        using var input = prepared.Response!;
        var options = new WorkerProcessOptions("dotnet", [worker]) { RenderBackend = "gpu", EnableGpuPreview = true,
            ScriptCompilationService = new ScriptCompiler(Path.Combine(Path.GetDirectoryName(worker)!, "script-refs", "net10.0"),
                Path.Combine(output, "dual-compiler-artifacts")) };
        await using var pool = new GpuWorkerPool(options);
        var registry = new GpuPreviewStreamRegistry();
        var active = new Dictionary<string, (string Client, GpuPreviewStreamState State, BackendPreviewPinnedInput Input, GpuStreamWorkerSession Worker)>();
        string host = Guid.NewGuid().ToString("N");
        await ReplyAsync(new { ready = true, hostInstanceId = host, sourceSha256 = WorkerProtocol.ComputeSha256(input.SourceBytes.Span),
            deploymentClosure = new { pool.DeploymentClosureSize.Files, pool.DeploymentClosureSize.Bytes } });
        try
        {
            string? line;
            while ((line = await Console.In.ReadLineAsync()) is not null)
            {
                using var document = JsonDocument.Parse(line);
                var command = document.RootElement;
                long id = command.GetProperty("id").GetInt64();
                string operation = command.GetProperty("op").GetString()!;
                try
                {
                    object? result = operation switch
                    {
                        "open" => await OpenAsync(command),
                        "claim" => Claim(command),
                        "close" => await CloseAsync(command.GetProperty("streamId").GetString()!),
                        "read" => new { pool = pool.Read(), workers = pool.ReadPrivateDiagnostics(), streams = active.Values.Select(x => x.State).ToArray() },
                        "verify" => await pool.VerifyDeviceAsync(),
                        "shutdown" => null,
                        _ => throw new ArgumentException("Unknown dual driver operation."),
                    };
                    await ReplyAsync(new { id, ok = true, data = result });
                    if (operation == "shutdown") break;
                }
                catch (Exception error)
                { await ReplyAsync(new { id, ok = false, errorType = error.GetType().Name, code = (error as WorkerProtocolException)?.Code }); }
            }
        }
        finally
        {
            foreach (string stream in active.Keys.ToArray()) await CloseAsync(stream);
            await pool.DisposeAsync();
        }

        async Task<object> OpenAsync(JsonElement command)
        {
            string consumer = command.GetProperty("consumerId").GetString()!;
            string client = command.GetProperty("clientId").GetString()!;
            using var reservation = registry.TryReserve(client, consumer) ?? throw new InvalidOperationException("Dual stream quota exhausted.");
            string stream = Guid.NewGuid().ToString("N");
            var retention = input.AcquireReference();
            var pinned = new BackendPreviewPinnedInput(new("project", client, "0"), input.Snapshot, new NeverFactory(), retention)
            { PreparedInput = input, VerifiedProfile = input.SourceProfile };
            GpuStreamWorkerSession? session = null;
            try
            {
                session = await GpuStreamWorkerSession.OpenAsync(pool, input, stream, host, "1", 1280, 720, false, mainPid);
                var state = new GpuPreviewStreamState(stream, "1", "1", "ready", pinned.Source, new(1280, 720),
                    PreviewAlphaMode.Opaque, "vulkan", session.Opened.ProducerEpoch, session.Opened.PoolGeneration,
                    session.Opened.SlotCount, checked((long)session.Opened.AllocationBytes));
                if (!registry.Publish(reservation, state, pinned, session)) throw new InvalidOperationException("Dual registry publication rejected.");
                active.Add(stream, (client, state, pinned, session));
                var owned = pool.ReadPrivateDiagnostics().Single(w => w.ProcessId == session.Opened.WorkerPid);
                return new { state, opened = session.Opened, process = owned.OwnedIdentity, preparation = owned.Preparation,
                    admission = session.WorkerAdmission, hostInstanceId = host };
            }
            catch { if (session is not null) await session.DisposeAsync(); retention.Dispose(); throw; }
        }

        object Claim(JsonElement command)
        {
            if (command.GetProperty("hostInstanceId").GetString() != host) throw new ArgumentException("Host mismatch.");
            return registry.Claim(command.GetProperty("consumerId").GetString()!, command.GetProperty("streamId").GetString()!,
                command.GetProperty("expectedInputGeneration").GetString()!) ?? throw new InvalidOperationException("Exact one-time registry claim rejected.");
        }

        async Task<object> CloseAsync(string stream)
        {
            var item = active[stream];
            if (!registry.TryBeginChange(item.Client, stream, item.State.StreamRevision, out bool found) || !found)
                throw new InvalidOperationException("Exact registry close rejected.");
            await item.Worker.DisposeAsync();
            item.Input.RetentionLease?.Dispose();
            if (!registry.CompleteClose(item.Client, stream, item.Worker)) throw new InvalidOperationException("Registry close completion rejected.");
            active.Remove(stream);
            return new { streamId = stream, item.Worker.TerminationConfirmed, pool = pool.Read(), workers = pool.ReadPrivateDiagnostics() };
        }
    }

    private static async Task ReplyAsync(object value)
    { await Console.Out.WriteLineAsync(JsonSerializer.Serialize(value, Json)); await Console.Out.FlushAsync(); }

    private sealed class NeverFactory : IRenderFrameSessionFactory
    {
        public ValueTask<IRenderFrameSession> CreateAsync(ProjectSnapshot snapshot, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
    }
}
