using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Rendering;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Scripting.Compilation;
using MakoKaraGenerator.TestSupport;
using Xunit.Abstractions;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

public sealed class PreparedScriptWorkerTests(ITestOutputHelper output)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private static string WorkerPath
    {
        get
        {
            DirectoryInfo? current = new(AppContext.BaseDirectory);
            while (current is not null && !File.Exists(Path.Combine(current.FullName, "MakoKaraGenerator.slnx"))) current = current.Parent;
            return Path.Combine(current!.FullName, "src", "MakoKaraGenerator.Backend.Worker", "bin", "Debug", "net10.0", "MakoKaraGenerator.Backend.Worker.dll");
        }
    }
    private static string References => Path.Combine(Path.GetDirectoryName(WorkerPath)!, "script-refs", "net10.0");
    private static WorkerProcessOptions Options(IScriptCompilationService compiler, string? renderBackend = "gpu") => new("dotnet", [WorkerPath])
    {
        ScriptCompilationService = compiler,
        RenderBackend = renderBackend,
    };
    private static ProjectSnapshot Snapshot(string? controller = null)
    {
        var package = MixedEffectPackageFixture.Contents(controllerSource: controller);
        var project = Project.Create("fixed scripts", Composition.Create(8, 4, FrameRate.Fps30, outputDurationUs: 1_000_000), LyricsDocument.Create([])) with
        { Effects = new([package], [new() { Id = "instance", Name = "instance", PackageId = package.PackageId, Revision = package.Revision,
            ExportId = "controller", Binding = new(ProjectEffectBindingKind.Project) }]) };
        return ProjectSnapshot.FromProject(project);
    }
    private static ProjectSnapshot SnapshotV1(string controller)
    {
        var files = EffectPackageFixture.Files();
        files["src/PackEffect.cs"] = System.Text.Encoding.UTF8.GetBytes(controller);
        JsonObject manifest = EffectPackageFixture.Manifest(files);
        manifest["packageVersion"] = EffectPackageProfiles.V1.PackageVersion;
        manifest["sdkVersion"] = EffectPackageProfiles.V1.SdkVersion;
        manifest["shaderProfile"] = EffectPackageProfiles.V1.ShaderProfile;
        manifest.Remove("requires");
        manifest.Remove("lineExit");
        manifest.Remove("emitters");
        manifest["exports"]![0]!["id"] = "controller";
        manifest["exports"]![0]!["bindingKinds"] = new JsonArray("project");
        manifest["exports"]![0]!["outputs"]![0]!["requires"] = new JsonArray();
        var package = EffectPackageContents.Create(EffectPackageFixture.Bytes(manifest), EffectPackageFixture.Inputs(files), EffectPackageProfiles.V1);
        var project = Project.Create("fixed scripts", Composition.Create(8, 4, FrameRate.Fps30, outputDurationUs: 1_000_000), LyricsDocument.Create([])) with
        { Effects = new([package], [new() { Id = "instance", Name = "instance", PackageId = package.PackageId, Revision = package.Revision,
            ExportId = "controller", Binding = new(ProjectEffectBindingKind.Project) }]) };
        return ProjectSnapshot.FromProject(project);
    }
    private static JsonObject Metadata(StaticWorkerSessionHandle session) => JsonNode.Parse(session.PreparedInput!.Input.Envelope.AsSpan(8))!.AsObject();

    [Fact]
    public async Task 同Host编译器跨两个真实Worker复用并同PID重开原固定目录最终清理()
    {
        using TemporaryDirectory root = new(); var compiler = new ScriptCompiler(References, root.FilePath("cache"));
        string marker = root.FilePath("assemblies.txt");
        string source = $$"""
            using System;
            using System.IO;
            using System.Linq;
            using MakoKaraGenerator.Scripting;
            public sealed class PackEffect : EffectBase
            {
                public override void Evaluate(FrameContext frame, FrameWriter output)
                {
                    File.WriteAllLines({{System.Text.Json.JsonSerializer.Serialize(marker)}},
                        new[] { Environment.ProcessId.ToString(), File.ReadAllText("/proc/self/stat") }.Concat(
                        AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetName().Name + "|" + a.Location + "|" + a.ManifestModule.ModuleVersionId)));
                }
            }
            """;
        byte[] bytes = PackageProjectSnapshotCodec.Encode(SnapshotV1(source));
        int firstPid;
        await using (var first = new WorkerProcessProxy(Options(compiler, renderBackend: null)))
        {
            var handle = await first.OpenPackageSessionAsync(bytes, Timeout); firstPid = first.ProcessId!.Value;
            string inputRoot = Metadata(handle)["Root"]!.GetValue<string>();
            var initial = Directory.GetFiles(inputRoot, "*", SearchOption.AllDirectories).ToDictionary(path => path, File.GetLastWriteTimeUtc);
            await first.RenderPackageSessionAsync(new(handle.SessionId, handle.SourceSha256, 8, 4, 0, false, 1, 1), Timeout);
            string[] evidence = File.ReadAllLines(marker); Assert.Equal(firstPid.ToString(System.Globalization.CultureInfo.InvariantCulture), evidence[0]);
            Assert.Equal(File.ReadAllText($"/proc/{firstPid}/stat").Split(')')[1].Split(' ', StringSplitOptions.RemoveEmptyEntries)[19],
                evidence[1].Split(')')[1].Split(' ', StringSplitOptions.RemoveEmptyEntries)[19]);
            Assert.Contains(evidence, line => line.StartsWith("MakoKaraGenerator.Scripting|", StringComparison.Ordinal));
            Assert.DoesNotContain(evidence, line => line.StartsWith("Microsoft.CodeAnalysis", StringComparison.Ordinal)
                || line.StartsWith("MakoKaraGenerator.Scripting.Compiler|", StringComparison.Ordinal));
            output.WriteLine(string.Join(Environment.NewLine, evidence));
            output.WriteLine($"Loaded-assembly evidence PID={firstPid}; {evidence.Length - 2} assemblies; no Roslyn/compiler assembly.");
            await first.ClosePackageSessionForReopenAsync(handle, 2, Timeout);
            Assert.True(Directory.Exists(inputRoot));
            var reopened = await first.OpenPreparedSessionAsync(handle.PreparedInput!, Timeout);
            Assert.Equal(firstPid, first.ProcessId); Assert.NotEqual(handle.SessionId, reopened.SessionId); Assert.Equal(handle.SourceSha256, reopened.SourceSha256);
            Assert.Same(handle.PreparedInput, reopened.PreparedInput);
            Assert.Equal(initial.OrderBy(pair => pair.Key), Directory.GetFiles(inputRoot, "*", SearchOption.AllDirectories).ToDictionary(path => path, File.GetLastWriteTimeUtc).OrderBy(pair => pair.Key));
            Assert.Equal(1, compiler.CompilationCount);
            await first.ClosePackageSessionAsync(reopened, 1, Timeout); Assert.False(Directory.Exists(inputRoot));
        }
        await using var second = new WorkerProcessProxy(Options(compiler, renderBackend: null));
        var secondHandle = await second.OpenPackageSessionAsync(bytes, Timeout);
        Assert.NotEqual(firstPid, second.ProcessId); Assert.Equal(1, compiler.CompilationCount);
        await second.ClosePackageSessionAsync(secondHandle, 1, Timeout);
    }

    [Theory]
    [InlineData("coordinated")]
    [InlineData("missing")]
    [InlineData("oversize")]
    [InlineData("identity")]
    [InlineData("warning")]
    public async Task 真实Worker拒绝固定目录协同篡改缺失超限和部署错配且不回退编译(string mode)
    {
        using TemporaryDirectory root = new(); var compiler = new ScriptCompiler(References, root.FilePath("cache"));
        await using var worker = new WorkerProcessProxy(Options(compiler));
        var snapshot = Snapshot(mode == "warning" ? "#warning original warning\n" + MixedEffectPackageFixture.Source : null);
        var handle = await worker.OpenPackageSessionAsync(PackageProjectSnapshotCodec.Encode(snapshot), Timeout);
        await worker.ClosePackageSessionForReopenAsync(handle, 1, Timeout);
        JsonObject metadata = Metadata(handle); string inputRoot = metadata["Root"]!.GetValue<string>();
        string directory = Path.Combine(inputRoot, metadata["Artifacts"]![0]!["Path"]!.GetValue<string>());
        string pePath = Path.Combine(directory, "effect.dll"); string manifestPath = Path.Combine(directory, "build.json");
        if (mode == "missing") File.Delete(pePath);
        else if (mode == "oversize") { using var stream = File.OpenWrite(pePath); stream.SetLength(ScriptArtifactBudget.Default.MaximumPeBytes + 1); }
        else
        {
            JsonObject manifest = JsonNode.Parse(File.ReadAllBytes(manifestPath))!.AsObject();
            if (mode == "coordinated")
            {
                byte[] pe = File.ReadAllBytes(pePath); pe[^1] ^= 1; File.WriteAllBytes(pePath, pe);
                manifest["Descriptor"]!["PeHash"] = Convert.ToHexStringLower(SHA256.HashData(pe));
            }
            else if (mode == "identity") manifest["Descriptor"]!["Identity"]!["CompilerVersion"] = "0.0.0.0";
            else manifest["Diagnostics"]![0]!["Message"] = manifest["Diagnostics"]![0]!["Message"]!.GetValue<string>()
                .Replace("original warning", "replaced warning", StringComparison.Ordinal);
            File.WriteAllText(manifestPath, manifest.ToJsonString());
        }
        var failure = await Assert.ThrowsAsync<WorkerProtocolException>(() => worker.OpenPreparedSessionAsync(handle.PreparedInput!, Timeout));
        Assert.NotEqual("WorkerPackageCompilationFailed", failure.Code); Assert.Equal(1, compiler.CompilationCount);
        await worker.DisposeAsync(); Assert.False(Directory.Exists(inputRoot));
        output.WriteLine($"Actual Worker rejected {mode}: {failure.Code}; compile count=1; prepared closure removed after drain.");
    }

    [Theory]
    [InlineData("preview")]
    [InlineData("validation")]
    [InlineData("export")]
    public async Task Host编译阶段deadline保预览取消与验证导出原超时编号(string operation)
    {
        byte[] bytes = PackageProjectSnapshotCodec.Encode(Snapshot());
        var slow = new SlowCompiler(ScriptCompilationIdentity.ForDeployment(References));
        await using var worker = new WorkerProcessProxy(Options(slow));
        if (operation == "preview")
        {
            await worker.GetPackageCapabilitiesAsync(Timeout);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.OpenPackageSessionAsync(bytes, TimeSpan.FromSeconds(1)));
        }
        else if (operation == "validation")
        {
            var failure = await Assert.ThrowsAsync<RenderSessionFailureException>(() => worker.ValidateProjectAsync(bytes, 1, TimeSpan.FromSeconds(1), null, PackageProjectSnapshotCodec.Profile));
            Assert.Equal("WorkerTaskTimedOut", failure.Code);
        }
        else
        {
            using TemporaryDirectory root = new();
            var metadata = new WorkerExportMetadata(WorkerExportContract.ExecutionProfile, PackageProjectSnapshotCodec.Profile,
                "opaqueMp4", false, root.FilePath("candidate.mp4"), null, "/usr/bin/ffmpeg", "/usr/bin/ffprobe");
            byte[] payload = WorkerExportContract.EncodeInput(Guid.NewGuid(), metadata, bytes);
            var input = WorkerExportContract.DecodeInput(payload, false);
            var request = new WorkerMessage(WorkerExportContract.RequestKind, Guid.NewGuid().ToString("N"), WorkerProtocol.ComputeSha256(payload), payload,
                sourceSha256: input.SourceSha256, taskId: input.TaskId.ToString("N"), exportFormat: "opaqueMp4", exportIncludeAudio: false);
            var failure = await Assert.ThrowsAsync<RenderSessionFailureException>(() => worker.InvokeExportTaskAsync(request, 30, TimeSpan.FromSeconds(1), null, CancellationToken.None));
            Assert.Equal("WorkerExportTimedOut", failure.Code);
        }
        Assert.Equal(1, slow.Calls);
    }

    [Fact]
    public async Task Dispose在Host编译阶段取消等待且不启动新Worker()
    {
        var slow = new SlowCompiler(ScriptCompilationIdentity.ForDeployment(References));
        await using var worker = new WorkerProcessProxy(Options(slow));
        await worker.GetPackageCapabilitiesAsync(Timeout);
        var pending = worker.OpenPackageSessionAsync(PackageProjectSnapshotCodec.Encode(Snapshot()), Timeout);
        await slow.Entered.Task.WaitAsync(Timeout);
        await worker.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Null(worker.ProcessId);
    }

    private sealed class SlowCompiler(ScriptCompilationIdentity identity) : IScriptCompilationService
    {
        public ScriptCompilationIdentity Identity => identity;
        public int Calls { get; private set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<ScriptCompilationResult> CompileAsync(IEnumerable<ScriptSource> sources, IEnumerable<string> entryTypes, CancellationToken cancellationToken = default)
        { Calls++; Entered.TrySetResult(); await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken); throw new InvalidOperationException("Unbounded preparation escaped its one-second deadline."); }
    }
}
