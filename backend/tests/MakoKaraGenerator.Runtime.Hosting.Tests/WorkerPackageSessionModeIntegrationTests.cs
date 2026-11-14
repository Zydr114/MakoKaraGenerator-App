using System.Diagnostics;
using MakoKaraGenerator.Scripting.Compilation;
using System.Globalization;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Rendering;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Processes;
using MakoKaraGenerator.Rendering;
using Xunit.Abstractions;

namespace MakoKaraGenerator.Runtime.Hosting.Tests;

public sealed class WorkerPackageSessionModeIntegrationTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 同一真实Worker的静态与包会话双向互斥且拒绝后原会话可用并可关闭切换(bool packageFirst)
    {
        Assert.True(LinuxProcessGuards.IsSupported);
        Assert.True(LinuxProcessGuards.TryEnableChildReaping());
        PackageWorkerRuntimeFixture.AssertFixedFont();
        ProjectSnapshot package = PackageWorkerRuntimeFixture.Create("scale");
        ProjectSnapshot plain = PackageWorkerRuntimeFixture.Fingerprint(package with { Effects = ProjectEffects.Empty });
        using TemporaryDirectory temporary = new();
        using var prepared = await PackageWorkerRuntimeFixture.PrepareScriptsAsync(package, PackageProjectSnapshotCodec.Encode(package), temporary.Path);
        byte[] packageBytes = prepared.Envelope;
        byte[] staticBytes = StaticProjectSnapshotCodec.Encode(plain);
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(90));
        CancellationToken token = deadline.Token;
        using Process process = new()
        {
            StartInfo = new(PackageWorkerRuntimeFixture.WorkerApphost())
            { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false },
        };
        process.StartInfo.Environment.Remove("MAKO_BACKEND_WORKER_MEDIA_LIMIT_BYTES");
        process.StartInfo.Environment["MAKO_RENDER_BACKEND"] = "vulkan";
        Assert.True(process.Start());
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        OwnedProcessGroup? group = null;
        Exception? primary = null;
        ProcessIdentity[] observed = [];
        try
        {
            WorkerMessage hello = new("hello", Guid.NewGuid().ToString("N"));
            WorkerMessage greeting = await ExchangeAsync(process, hello, token);
            group = OwnedProcessGroup.TryCapture(process);
            Assert.NotNull(group);
            Assert.Equal("hello", greeting.Kind);
            Assert.Equal(process.Id, greeting.ProcessGroupId);
            Assert.Equal(process.Id, group.Leader.ProcessGroupId);
            observed = [.. group.ObserveMembers()];
            Assert.Contains(group.Leader, observed);

            byte[] capabilityInput = PackageRenderWorkerContract.EncodeCapabilitiesRequest();
            WorkerMessage capabilities = await ExchangeAsync(process, Request(PackageRenderWorkerContract.CapabilitiesKind, capabilityInput), token);
            Assert.Equal(PackageRenderWorkerContract.CapabilitiesResultKind, capabilities.Kind);
            Assert.Null(capabilities.Error);
            PackageRenderWorkerCapabilities actualCapabilities = PackageRenderWorkerContract.DecodeCapabilities(capabilities.Payload);
            Assert.Equal(PackageProjectSnapshotCodec.Profile, actualCapabilities.CodecProfile);
            Assert.Equal(WorkerProtocol.ComputeSha256(capabilities.Payload), capabilities.OutputSha256);

            Guid firstId = Guid.NewGuid(), secondId = Guid.NewGuid();
            byte[] firstBytes = packageFirst ? packageBytes : staticBytes;
            byte[] secondBytes = packageFirst ? staticBytes : packageBytes;
            string firstHash = SourceHash(firstBytes), secondHash = SourceHash(secondBytes);
            await OpenAsync(process, packageFirst, firstId, firstBytes, token);

            // A proxy stops on an error reply. Raw IPC keeps this exact process
            // alive so rejection cannot be mistaken for restart recovery.
            WorkerMessage rejectedRequest = OpenRequest(!packageFirst, secondId, secondBytes);
            WorkerMessage rejected = await ExchangeAsync(process, rejectedRequest, token);
            Assert.Equal("error", rejected.Kind);
            Assert.Equal("WorkerSessionAlreadyOpen", rejected.ErrorCode);
            Assert.NotNull(rejected.Error);
            Assert.False(rejected.Cancelled);
            Assert.Equal(rejectedRequest.SessionId, rejected.SessionId);
            Assert.Equal(secondHash, rejected.SourceSha256);
            Assert.Equal(0L, rejected.FrameSequence);
            Assert.Empty(rejected.Payload);
            Assert.False(process.HasExited);
            Assert.Contains(group.Leader, group.ObserveMembers());

            await RenderAndCloseAsync(process, packageFirst, firstId, firstHash, token);
            await OpenAsync(process, !packageFirst, secondId, secondBytes, token);
            await RenderAndCloseAsync(process, !packageFirst, secondId, secondHash, token);
            Assert.Contains(group.Leader, group.ObserveMembers());

            WorkerMessage stopped = await ExchangeAsync(process, new("shutdown", Guid.NewGuid().ToString("N")), token);
            Assert.Equal("shutdown", stopped.Kind);
            await process.WaitForExitAsync(token);
            Assert.Equal(0, process.ExitCode);
            Assert.Empty(group.ObserveMembers());
            foreach (ProcessIdentity identity in observed) Assert.False(IdentityStillExists(identity), identity.ToString());
            output.WriteLine($"Actual raw IPC {(packageFirst ? "package/static" : "static/package")} Worker={group.Leader}; second open rejected, original render/close, switched render/close, shutdown exit=0 and observed identities gone.");
        }
        catch (Exception failure) { primary = failure; throw; }
        finally
        {
            try
            {
                // Capture/terminate only the exact child-owned private group.
                group ??= OwnedProcessGroup.TryCapture(process);
                if (group is not null) await group.KillAndDrainAsync(TimeSpan.FromSeconds(3));
                process.StandardInput.Close();
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                string errors = await stderr.WaitAsync(TimeSpan.FromSeconds(5));
                if (errors.Length > 0) output.WriteLine(errors);
                if (group is not null) Assert.Empty(group.ObserveMembers());
                foreach (ProcessIdentity identity in observed) Assert.False(IdentityStillExists(identity), identity.ToString());
            }
            catch (Exception cleanup) when (primary is not null) { primary.Data["AuthorWorkerCleanupFailure"] = cleanup; }
        }
    }

    private static WorkerMessage Request(string kind, byte[] payload, Guid? session = null, string? hash = null, long? sequence = null)
        => new(kind, Guid.NewGuid().ToString("N"), WorkerProtocol.ComputeSha256(payload), payload,
            sessionId: session?.ToString("N"), sourceSha256: hash, frameSequence: sequence);

    private static string SourceHash(byte[] bytes) => WorkerProtocol.ComputeSha256(ScriptPreparedInputCodec.IsPrepared(bytes)
        ? ScriptPreparedInputCodec.Read(bytes, ScriptCompilationIdentity.ForDeployment(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"))).Source : bytes);

    private static WorkerMessage OpenRequest(bool package, Guid session, byte[] snapshot)
        => Request(package ? PackageRenderWorkerContract.OpenKind : StaticRenderWorkerContract.OpenKind,
            package ? PackageRenderWorkerContract.EncodeOpen(session, snapshot) : StaticRenderWorkerContract.EncodeOpen(session, snapshot),
            session, SourceHash(snapshot), 0);

    private static async Task OpenAsync(Process process, bool package, Guid session, byte[] snapshot, CancellationToken token)
    {
        WorkerMessage request = OpenRequest(package, session, snapshot);
        WorkerMessage opened = await ExchangeAsync(process, request, token);
        AssertSessionResult(package, request, opened);
    }

    private static async Task RenderAndCloseAsync(Process process, bool package, Guid session, string hash, CancellationToken token)
    {
        StaticWorkerFrameRequest frame = new(session, hash, 320, 180, 100_000, false, 1);
        byte[] frameInput = package ? PackageRenderWorkerContract.EncodeFrame(frame) : StaticRenderWorkerContract.EncodeFrame(frame);
        WorkerMessage request = Request(package ? PackageRenderWorkerContract.RenderKind : StaticRenderWorkerContract.RenderKind,
            frameInput, session, hash, 1) with { FrameTransparentBackground = false, FrameQuality = frame.Quality };
        WorkerMessage actual = await ExchangeAsync(process, request, token);
        if (package)
        {
            PackageRenderWorkerContract.ValidateFrameResult(frame, actual);
            Assert.False(actual.Diagnostics!.HasError);
            Assert.Empty(actual.Diagnostics.Occurrences);
        }
        else StaticRenderWorkerContract.ValidateFrameResult(frame, actual);
        Assert.Equal(320 * 180 * 4, actual.Payload.Length);
        WorkerMessage close = Request(package ? PackageRenderWorkerContract.CloseKind : StaticRenderWorkerContract.CloseKind,
            package ? PackageRenderWorkerContract.EncodeClose(session, hash, 2) : StaticRenderWorkerContract.EncodeClose(session, hash, 2), session, hash, 2);
        AssertSessionResult(package, close, await ExchangeAsync(process, close, token));
    }

    private static void AssertSessionResult(bool package, WorkerMessage request, WorkerMessage actual)
    {
        Assert.Equal(package ? PackageRenderWorkerContract.SessionResultKind : StaticRenderWorkerContract.SessionResultKind, actual.Kind);
        Assert.Null(actual.Error); Assert.Null(actual.ErrorCode); Assert.False(actual.Cancelled);
        Assert.Equal(request.SessionId, actual.SessionId); Assert.Equal(request.SourceSha256, actual.SourceSha256);
        Assert.Equal(request.FrameSequence, actual.FrameSequence); Assert.Empty(actual.Payload);
    }

    private static async Task<WorkerMessage> ExchangeAsync(Process process, WorkerMessage request, CancellationToken token)
    {
        await WorkerProtocol.WriteAsync(process.StandardInput.BaseStream, request, token);
        WorkerMessage result = Assert.IsType<WorkerMessage>(await WorkerProtocol.ReadAsync(process.StandardOutput.BaseStream, token));
        Assert.Equal(WorkerProtocol.Version, result.ProtocolVersion);
        Assert.Equal(request.RequestId, result.RequestId);
        Assert.Equal(request.InputSha256, result.InputSha256);
        return result;
    }

    private static bool IdentityStillExists(ProcessIdentity identity)
    {
        try
        {
            string stat = File.ReadAllText("/proc/" + identity.ProcessId.ToString(CultureInfo.InvariantCulture) + "/stat");
            string[] fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return ulong.Parse(fields[19], CultureInfo.InvariantCulture) == identity.StartTicks
                && int.Parse(fields[2], CultureInfo.InvariantCulture) == identity.ProcessGroupId;
        }
        catch (IOException) { return false; }
    }
}
