using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Hosting;
using MakoKaraGenerator.Backend.Rendering;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Scripting;
using MakoKaraGenerator.Processes;
using Xunit.Abstractions;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

public sealed class WorkerProjectImageValidationCapabilityTests(ITestOutputHelper output)
{
    [Fact]
    public async Task 独立图片验证探测通过当前引擎与本地实际refs并在停止后排水()
    {
        Assert.True(ProcessGuards.TryEnableChildReaping());
        using Probe probe = await Probe.CreateAsync("current");
        await using WorkerProcessProxy proxy = new(probe.Options());
        var value = await proxy.GetImageValidationCapabilitiesAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(StaticProjectSnapshotCodec.EngineIdentity, value.EngineIdentity);
        Assert.Equal(ProjectImageValidationWorkerContract.ValidationProfile, value.CodecProfile);
        Assert.Equal(probe.PackageCapabilities.ScriptRefsIdentity, value.PackageCapabilities.ScriptRefsIdentity);
        Assert.Equal(probe.PackageCapabilities.ScriptRefsCount, value.PackageCapabilities.ScriptRefsCount);
        Assert.NotNull(proxy.ProcessId);
        await proxy.StopAsync();
        Assert.Null(proxy.ProcessId);
        probe.AssertDrained(output);
    }

    [Theory]
    [InlineData("preview-only")]
    [InlineData("package-only")]
    public async Task 旧图片预览或包验证能力即使真实探测成功也不能代替图片验证能力(string mode)
    {
        Assert.True(ProcessGuards.TryEnableChildReaping());
        using Probe probe = await Probe.CreateAsync(mode);
        await using WorkerProcessProxy proxy = new(probe.Options());
        if (mode == "preview-only")
        {
            var old = await proxy.GetImagePreviewCapabilitiesAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(BackendSnapshotProfiles.ProjectImagePreview, old.CodecProfile);
        }
        else
        {
            var old = await proxy.GetPackageValidationCapabilitiesAsync(TimeSpan.FromSeconds(5));
            Assert.Contains(PackageRenderWorkerContract.ValidationProfile, old.ValidationProfiles!);
        }
        int previousPid = Assert.IsType<int>(proxy.ProcessId);
        WorkerProtocolException failure = await Assert.ThrowsAsync<WorkerProtocolException>(
            () => proxy.GetImageValidationCapabilitiesAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("WorkerImageValidationUnavailable", failure.Code);
        Assert.Null(proxy.ProcessId);
        Assert.Contains(ProjectImageValidationWorkerContract.CapabilitiesKind, probe.Requests());
        Assert.DoesNotContain(ProjectImageValidationWorkerContract.RequestKind, probe.Requests());
        Assert.Equal(previousPid, probe.Pid());
        probe.AssertDrained(output);
    }

    [Theory]
    [InlineData("engine")]
    [InlineData("package-version")]
    [InlineData("sdk")]
    [InlineData("shader")]
    [InlineData("refs-identity")]
    [InlineData("refs-count")]
    [InlineData("missing-package-profiles")]
    [InlineData("only-v1")]
    [InlineData("missing-local-refs")]
    public async Task 图片验证结构合法但与当前部署引擎包版本或实际refs不匹配拒绝并回收Worker(string mode)
    {
        Assert.True(ProcessGuards.TryEnableChildReaping());
        using Probe probe = await Probe.CreateAsync(mode);
        await using WorkerProcessProxy proxy = new(probe.Options());
        WorkerProtocolException failure = await Assert.ThrowsAsync<WorkerProtocolException>(
            () => proxy.GetImageValidationCapabilitiesAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("WorkerImageValidationUnavailable", failure.Code);
        Assert.Null(proxy.ProcessId);
        Assert.Contains(ProjectImageValidationWorkerContract.CapabilitiesKind, probe.Requests());
        Assert.DoesNotContain(ProjectImageValidationWorkerContract.RequestKind, probe.Requests());
        probe.AssertDrained(output);
    }

    private sealed class Probe : IDisposable
    {
        private readonly TemporaryDirectory _temp = new();
        private readonly string _mode;
        private Probe(string mode) { _mode = mode; }
        internal PackageRenderWorkerCapabilities PackageCapabilities { get; private set; } = null!;

        internal static async Task<Probe> CreateAsync(string mode)
        {
            Probe probe = new(mode);
            string executable = probe._temp.FilePath(OperatingSystem.IsWindows() ? "worker.py" : "worker");
            await File.WriteAllTextAsync(executable, "#!/usr/bin/python3\n" + Source);
            if (OperatingSystem.IsLinux())
                File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            string refs = Path.Combine(probe._temp.Path, "script-refs", "net10.0");
            Directory.CreateDirectory(refs);
            string reference = Path.Combine(refs, "System.Runtime.dll");
            // The protocol probe hashes these synthetic bytes; no fake assembly is loaded or executed.
            await File.WriteAllBytesAsync(reference, [1, 3, 5, 7]);
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach (string path in new[] { reference, typeof(TokenId).Assembly.Location })
            {
                Append(hash, Encoding.UTF8.GetBytes(Path.GetFileName(path)));
                Append(hash, SHA256.HashData(await File.ReadAllBytesAsync(path)));
            }
            EffectPackageProfile profile = ProjectEffectsJson.PackageProfile;
            probe.PackageCapabilities = new(StaticProjectSnapshotCodec.EngineIdentity, BackendSnapshotProfiles.PackageLyricsScene,
                profile.PackageVersion, profile.SdkVersion, profile.ShaderProfile,
                Convert.ToHexStringLower(hash.GetHashAndReset()), 2,
                ValidationProfiles: [PackageRenderWorkerContract.ValidationProfile],
                SupportedPackageProfiles: WorkerPackageProfileAdmission.Current);
            ProjectImageValidationWorkerCapabilities image = new(StaticProjectSnapshotCodec.EngineIdentity,
                ProjectImageValidationWorkerContract.ValidationProfile,
                [BackendSnapshotProfiles.StaticLyricsScene, BackendSnapshotProfiles.StaticLyricsSceneAnalysis, BackendSnapshotProfiles.PackageLyricsScene],
                ProjectImageValidationWorkerContract.ImageDecoderProfile, ProjectImageValidationWorkerContract.MaximumInputBytes,
                ProjectImageValidationWorkerContract.MaximumImageBytes, ProjectImageValidationWorkerContract.MaximumDimension,
                ProjectImageValidationWorkerContract.MaximumDecodedBytes, ProjectImageValidationWorkerContract.MaximumBaseSnapshotBytes,
                ProjectImageValidationWorkerContract.MaximumSourceValues, WorkerValidationContract.MaximumReportBytes,
                WorkerPackageDiagnosticsContract.Version, probe.PackageCapabilities);
            image = mode switch
            {
                "missing-package-profiles" => image with { PackageCapabilities = image.PackageCapabilities with { SupportedPackageProfiles = null } },
                "only-v1" => image with { PackageCapabilities = image.PackageCapabilities with { SupportedPackageProfiles = [new(2, "1.0.0", ShaderProgramDefinition.RasterProfile)] } },
                "engine" => image with { EngineIdentity = "other-engine", PackageCapabilities = image.PackageCapabilities with { EngineIdentity = "other-engine" } },
                "package-version" => image with { PackageCapabilities = image.PackageCapabilities with { PackageVersion = profile.PackageVersion + 1 } },
                "sdk" => image with { PackageCapabilities = image.PackageCapabilities with { SdkVersion = profile.SdkVersion + "-other" } },
                "shader" => image with { PackageCapabilities = image.PackageCapabilities with { ShaderProfile = profile.ShaderProfile + "-other" } },
                "refs-identity" => image with { PackageCapabilities = image.PackageCapabilities with { ScriptRefsIdentity = new string('f', 64) } },
                "refs-count" => image with { PackageCapabilities = image.PackageCapabilities with { ScriptRefsCount = 3 } },
                _ => image,
            };
            await File.WriteAllBytesAsync(probe._temp.FilePath("image.json"), ProjectImageValidationWorkerContract.EncodeCapabilities(image));
            await File.WriteAllBytesAsync(probe._temp.FilePath("package.json"), PackageRenderWorkerContract.EncodeCapabilities(probe.PackageCapabilities));
            ProjectImagePreviewWorkerCapabilities preview = new(StaticProjectSnapshotCodec.EngineIdentity, BackendSnapshotProfiles.ProjectImagePreview,
                image.BaseProfiles, ProjectImagePreviewWorkerContract.ImageDecoderProfile, ProjectImagePreviewWorkerContract.MaximumImageBytes,
                ProjectImagePreviewWorkerContract.MaximumDimension, ProjectImagePreviewWorkerContract.MaximumDecodedBytes,
                ProjectImagePreviewWorkerContract.MaximumSnapshotBytes, ProjectImagePreviewWorkerContract.MaximumBaseSnapshotBytes,
                ProjectImagePreviewWorkerContract.MaximumSourceValues, WorkerPackageDiagnosticsContract.Version, probe.PackageCapabilities);
            await File.WriteAllBytesAsync(probe._temp.FilePath("preview.json"), ProjectImagePreviewWorkerContract.EncodeCapabilities(preview));
            if (mode == "missing-local-refs") File.Delete(reference);
            return probe;
        }

        internal WorkerProcessOptions Options() => OperatingSystem.IsWindows()
            ? HostingTestPlatformHelpers.PythonOptions(_temp.FilePath("worker.py"), [_mode, _temp.Path], TimeSpan.FromMilliseconds(100))
            : new(_temp.FilePath("worker"), [_mode, _temp.Path], TimeSpan.FromMilliseconds(100));
        internal string[] Requests() => File.ReadAllLines(_temp.FilePath("requests"));
        internal int Pid() => int.Parse(File.ReadAllText(_temp.FilePath("pid")), CultureInfo.InvariantCulture);
        internal void AssertDrained(ITestOutputHelper testOutput)
        {
            int pid = Pid();
            if (OperatingSystem.IsLinux())
                Assert.False(Directory.Exists("/proc/" + pid.ToString(CultureInfo.InvariantCulture)));
            else
                Assert.False(HostingTestPlatformHelpers.IsSameProcess(pid, AliveStartIdentity(pid)));
            testOutput.WriteLine($"Image validation capability mode={_mode}, Worker PID={pid}, gone=true.");
        }
        private static long AliveStartIdentity(int pid)
        {
            try { using var p = System.Diagnostics.Process.GetProcessById(pid); return p.StartTime.ToFileTimeUtc(); }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException) { return -1L; }
        }
        public void Dispose() => _temp.Dispose();
        private static void Append(IncrementalHash hash, byte[] bytes)
        {
            Span<byte> length = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
            hash.AppendData(length); hash.AppendData(bytes);
        }

        private const string Source = """
            import base64, hashlib, json, os, struct, sys
            mode, root = sys.argv[1:]
            if hasattr(os, 'setsid'): os.setsid()
            with open(os.path.join(root,'pid'),'w') as target: target.write(str(os.getpid()))
            def read_exact(length):
                result=b''
                while len(result)<length:
                    value=sys.stdin.buffer.read(length-len(result))
                    if not value: return None
                    result+=value
                return result
            def send(value):
                body=json.dumps(dict(protocolVersion='worker-1',delayMilliseconds=0,cancelled=False,**value)).encode()
                sys.stdout.buffer.write(struct.pack('<i',len(body))+body); sys.stdout.buffer.flush()
            while True:
                prefix=read_exact(4)
                if prefix is None: break
                body=read_exact(struct.unpack('<i',prefix)[0])
                if body is None: break
                request=json.loads(body); kind=request['kind']
                with open(os.path.join(root,'requests'),'a') as target: target.write(kind+'\n')
                if kind=='hello':
                    send(dict(kind='hello',requestId=request['requestId'],processGroupId=os.getpgrp() if hasattr(os, 'getpgrp') else os.getpid())); continue
                if kind=='shutdown': break
                if kind=='cancel': continue
                if kind=='project-image-preview-capabilities-v1' or (kind=='project-image-validation-capabilities-v1' and mode=='preview-only'):
                    name='preview'; result_kind='project-image-preview-capabilities-result-v1'
                elif kind=='package-validation-capabilities-v1' or (kind=='project-image-validation-capabilities-v1' and mode=='package-only'):
                    name='package'; result_kind='package-validation-capabilities-result-v1'
                else:
                    name='image'; result_kind='project-image-validation-capabilities-result-v1'
                with open(os.path.join(root,name+'.json'),'rb') as source: payload=source.read()
                send(dict(kind=result_kind,requestId=request['requestId'],inputSha256=request['inputSha256'],
                    payload=base64.b64encode(payload).decode(),outputSha256=hashlib.sha256(payload).hexdigest()))
            """;
    }
}
