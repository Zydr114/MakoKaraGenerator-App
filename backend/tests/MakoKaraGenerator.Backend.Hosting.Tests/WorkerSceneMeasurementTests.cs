using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Rendering;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.TestSupport;
using Xunit.Abstractions;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

public sealed class WorkerSceneMeasurementTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("time", "opaque")]
    [InlineData("time", "transparent")]
    [InlineData("layout", "transparent")]
    public async Task 真实Worker同帧测量返回完整稳定身份与有界PNG并退出(string mode, string alpha)
    {
        var snapshot = Snapshot();
        byte[] source = StaticProjectSnapshotCodec.Encode(snapshot);
        var request = new SceneMeasurementRequest("0",mode,mode == "time" ? "250000" : null,320,180,alpha);
        var input = Input(source,request);
        await using var worker = new WorkerProcessProxy(Options());
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var result = await worker.MeasureSceneAsync(input,source,deadline.Token);
        int pid = worker.ProcessId!.Value;
        Assert.Equal(WorkerProtocol.ComputeSha256(source),result.Measurement.SourceSha256);
        Assert.Contains(result.Measurement.Elements,element => element.Kind == "line" && element.Visible && element.WorldCorners.Length == 4);
        Assert.Contains(result.Measurement.Elements,element => element.FixedReference is not null && element.EditReference is not null);
        Assert.True(SceneMeasurementContract.IsValidPng(result.Measurement.Png,320,180));
        Assert.Equal(mode == "layout" ? null : "250000",result.Measurement.TimeUs);
        Assert.Equal(mode == "layout",result.Measurement.Elements.Any(element => element.Representative));
        await worker.DisposeAsync(); Assert.False(Alive(pid));
        output.WriteLine($"Real measured Worker PID={pid} mode={mode} alpha={alpha}; geometry={result.Measurement.Elements.Length}, PNG={result.Measurement.Png.Length}; exited.");
    }

    [Fact]
    public async Task 默认隐式树和已保存槽位均按投影节点身份分类()
    {
        var snapshot = Snapshot();
        Assert.Empty(snapshot.LyricPresentation.Nodes);
        foreach (bool slots in new[] { false,true })
        {
            var candidate = slots ? snapshot with { LyricPresentation = LyricPresentationDefinition.FromBranches([
                new() { BranchId = snapshot.Lyrics.PrimaryBranchId, SlotSequenceProfile = SlotSequenceAnimationProfile.Default }]) } : snapshot;
            byte[] source = StaticProjectSnapshotCodec.Encode(candidate);
            await using var worker = new WorkerProcessProxy(Options());
            var actual = (await worker.MeasureSceneAsync(Input(source,new("0","layout",null,64,36,"transparent")),source,CancellationToken.None)).Measurement;
            var expected = candidate.LyricPresentation.WithDefaultPrograms(candidate.Lyrics);
            Assert.All(expected.Nodes,node => Assert.Contains(actual.Elements,element => element.Id == node.Id && element.FixedReference is not null));
            string[] slotIds = expected.Nodes.Where(node => node.Layout is not null).SelectMany(node => node.Layout!.SlotIds).ToArray();
            Assert.Equal(slotIds.Length,actual.Elements.Count(element => element.Kind == "slot"));
            Assert.All(slotIds,id => Assert.Contains(actual.Elements,element => element.Id == id && element.Kind == "slot"));
        }
    }

    [Fact]
    public async Task 真实Worker同帧测量支持包与固定预设混合source()
    {
        var snapshot = PackagePresetSnapshot();
        Assert.Equal(PackagePresetProjectSnapshotCodec.Profile, ProjectSnapshotSourceProfile.Select(snapshot));
        byte[] source = ProjectSnapshotSourceProfile.Encode(snapshot);
        var request = new SceneMeasurementRequest("0", "time", "250000", 320, 180, "transparent");
        await using var worker = new WorkerProcessProxy(Options());
        var result = await worker.MeasureSceneAsync(Input(source, request), source, CancellationToken.None);
        Assert.Equal(PackagePresetProjectSnapshotCodec.Profile, result.Measurement.SourceProfile);
        Assert.True(SceneMeasurementContract.IsValidPng(result.Measurement.Png, 320, 180));
        int pid = worker.ProcessId!.Value;
        await worker.DisposeAsync();
        Assert.False(Alive(pid));
    }

    [Theory]
    [InlineData("time")]
    [InlineData("layout")]
    public async Task 大字体Color输入测量使用Host固定快照核对真实Worker完整回执(string mode)
    {
        var package = PackageFontFixture.Contents();
        var original = Snapshot();
        var project = ProjectSnapshotSourceProject.Restore(original) with {
            Effects = new([package]), LyricStyle = original.LyricStyle with { FontFamilyName = PackageFontFixture.Family } };
        var snapshot = ProjectSnapshot.FromProject(project);
        using var lease = BackendProjectRuntimeInputLease.CreateDetached(snapshot);
        var preparation = await lease.PrepareAsync();
        Assert.True(preparation.Accepted, preparation.Error?.Message);
        using var prepared = preparation.Response!;
        Assert.Null(prepared.Grant);
        Assert.Equal(ProjectRuntimeInputWorkerContract.Profile, prepared.SourceProfile);
        byte[] source = prepared.SourceBytes.ToArray();
        Assert.True(source.Length < StaticProjectSnapshotCodec.MaximumBytes);
        var request = new SceneMeasurementRequest("0", mode, mode == "time" ? "250000" : null, 320, 180, "transparent");
        var input = Input(source, request);
        await using var worker = new WorkerProcessProxy(Options());
        var mismatch = await Assert.ThrowsAsync<SnapshotContractException>(() =>
            worker.MeasureSceneAsync(input, source, snapshot with { Name = "changed" }, CancellationToken.None));
        Assert.Equal("ProjectRuntimeSourceMismatch", mismatch.Code);
        Assert.Null(worker.ProcessId);
        var result = await worker.MeasureSceneAsync(input, source, snapshot, CancellationToken.None);
        Assert.Equal(ProjectRuntimeInputWorkerContract.Profile, result.Measurement.SourceProfile);
        Assert.Contains(result.Measurement.Elements, element => element.Kind == "line" && element.Visible && element.WorldCorners.Length == 4);
        Assert.True(SceneMeasurementContract.IsValidPng(result.Measurement.Png, 320, 180));
        int pid = worker.ProcessId!.Value;
        await worker.DisposeAsync();
        Assert.False(Alive(pid));
        output.WriteLine($"Large actual TTC font measured Worker PID={pid}, mode={mode}; fixed Host snapshot validated and Worker exited.");
    }

    [Theory]
    [InlineData("transparent", "TransparentBackgroundDependency")]
    [InlineData("opaque", "BackgroundMediaUnavailable")]
    public async Task 真实Worker同帧测量对缺背景材质照常返回并带去重兜底诊断(string alpha, string code)
    {
        // 源材质按字（token）施加，测量工程需要带字的行。
        var original = Snapshot() with { Lyrics = LyricsDocument.Create([LyricLine.Create("actual-line", "Measured", new TimeRange(0, 2_000_000),
            TimingStatus.Confirmed, [LyricToken.Create("token", "Measured", new TimeRange(0, 2_000_000), TimingStatus.Confirmed)])]) };
        var contents = MixedEffectPackageFixture.Contents(pure: true, background: true);
        var material = new MaterialSpec.PackageMaterial(contents.PackageId, contents.Revision, "ink");
        var project = ProjectSnapshotSourceProject.Restore(original) with {
            Effects = new([contents], []), LyricStyle = original.LyricStyle with { UnsungFill = new() { Material = material } } };
        var snapshot = ProjectSnapshot.FromProject(project);
        using var lease = BackendProjectRuntimeInputLease.CreateDetached(snapshot);
        var preparation = await lease.PrepareAsync();
        Assert.True(preparation.Accepted, preparation.Error?.Message);
        using var prepared = preparation.Response!;
        byte[] source = prepared.SourceBytes.ToArray();
        var request = new SceneMeasurementRequest("0", "time", "250000", 320, 180, alpha);
        await using var worker = new WorkerProcessProxy(Options() with { RenderBackend = "gpu" });
        var result = await worker.MeasureSceneAsync(Input(source, request), source, snapshot, CancellationToken.None);
        Assert.True(SceneMeasurementContract.IsValidPng(result.Measurement.Png, 320, 180));
        Assert.NotEmpty(result.Measurement.FallbackDiagnostics);
        Assert.All(result.Measurement.FallbackDiagnostics, diagnostic =>
        {
            Assert.Equal(("material", "warning", code), (diagnostic.OriginKind, diagnostic.Severity, diagnostic.Code));
            Assert.Equal("source", diagnostic.Material!.Owner.Kind);
        });
        output.WriteLine($"Fallback measurement alpha={alpha}: {result.Measurement.FallbackDiagnostics.Length} diagnostics.");
    }

    [Fact]
    public async Task 真实Worker槽动画进入保持退出输出稳定槽节点并保留完整校验()
    {
        var sourceSnapshot = Snapshot();
        string branchId = sourceSnapshot.Lyrics.PrimaryBranchId;
        var profile = SlotSequenceAnimationProfile.Default with {
            RevealMode = SlotRevealMode.LeadBeforeNextStart, RevealLeadUs = 0, MaxVisibleLines = 2,
            RetentionPolicy = SlotRetentionPolicy.UntilLineEnd, EnterDurationUs = 400_000, ExitDurationUs = 400_000,
            Easing = AnimationEasing.Linear, ExitEasing = AnimationEasing.Linear,
            EnterFrom = new() { Opacity = 0, Scale = .75, RotationDegrees = -12, OffsetXPx = 8, OffsetYPx = -8 },
            Hold = SceneTransform.Neutral,
            ExitTo = new() { Opacity = 0, Scale = .8, RotationDegrees = 16, OffsetXPx = -8, OffsetYPx = 8 } };
        var snapshot = sourceSnapshot with { LyricPresentation = LyricPresentationDefinition.FromBranches([
            new() { BranchId = branchId, SlotSequenceProfile = profile }]) };
        var tree = snapshot.LyricPresentation;
        var node = tree.FindNode(tree.FindProgram(branchId)!.DefaultContainerId)!;
        string slotId = node.Layout!.SlotIds[0];
        string role = tree.Resolve(branchId).SlotSequenceProfile!.Slots[0].Id;
        Assert.NotEqual(slotId,role);
        byte[] source = StaticProjectSnapshotCodec.Encode(snapshot);
        var pngs = new List<byte[]>();
        foreach (var sample in new[] { (Time: "100000", Opacity: .25), (Time: "1000000", Opacity: 1d), (Time: "1900000", Opacity: .25) })
        {
            await using var worker = new WorkerProcessProxy(Options());
            var result = await worker.MeasureSceneAsync(Input(source,new("0","time",sample.Time,320,180,"transparent")),source,CancellationToken.None);
            var line = Assert.Single(result.Measurement.Elements, element => element.Kind == "line" && element.LineId == "actual-line");
            Assert.True(line.Visible); Assert.Equal(slotId,line.SlotId); Assert.Equal(slotId,line.ParentId);
            Assert.InRange(line.Opacity,sample.Opacity - .0001,sample.Opacity + .0001);
            Assert.Equal(4,line.WorldCorners.Length); Assert.NotNull(line.WorldMatrix); Assert.NotNull(line.EditReference);
            Assert.Equal("measuredInk",line.GeometryStatus); pngs.Add(result.Measurement.Png);
            int pid = worker.ProcessId!.Value; await worker.DisposeAsync(); Assert.False(Alive(pid));
            output.WriteLine($"Slot animation real Worker PID={pid}, time={sample.Time}, slot={slotId}, role={role}, opacity={line.Opacity}; exited.");
        }
        Assert.False(pngs[0].AsSpan().SequenceEqual(pngs[1])); Assert.False(pngs[1].AsSpan().SequenceEqual(pngs[2]));
    }

    [Fact]
    public async Task 不支持测量能力的旧Worker明确拒绝且不执行测量()
    {
        using TemporaryDirectory directory = new();
        string script = Path.Combine(directory.Path,"old.py");
        await File.WriteAllTextAsync(script,"""
import sys,json,struct
while True:
 p=sys.stdin.buffer.read(4)
 if not p: break
 m=json.loads(sys.stdin.buffer.read(struct.unpack('<I',p)[0]))
 r={'kind':'hello' if m['kind']=='hello' else 'error','requestId':m['requestId'],'payload':'','inputSha256':m.get('inputSha256')}
 if r['kind']=='error': r['error']='unsupported old worker'; r['errorCode']='SceneMeasurementUnavailable'
 b=json.dumps(r).encode();sys.stdout.buffer.write(struct.pack('<I',len(b))+b);sys.stdout.buffer.flush()
""");
        await using var worker = new WorkerProcessProxy(HostingTestPlatformHelpers.PythonOptions(script,[]));
        byte[] source = StaticProjectSnapshotCodec.Encode(Snapshot());
        var error = await Assert.ThrowsAsync<WorkerProtocolException>(() => worker.MeasureSceneAsync(Input(source,new("0","layout",null,64,36,"transparent")),source,CancellationToken.None));
        Assert.Equal("SceneMeasurementUnavailable",error.Code);
        int? pid = worker.ProcessId; await worker.DisposeAsync(); if (pid is { } actual) Assert.False(Alive(actual));
    }

    [Fact]
    public async Task 受Hash保护但错配或损坏的测量Worker回执完整拒绝()
    {
        using TemporaryDirectory directory = new();
        byte[] source = StaticProjectSnapshotCodec.Encode(Snapshot());
        var input = Input(source,new("0","time","250000",64,36,"transparent"));
        WorkerSceneMeasurementOutput valid;
        await using (var real = new WorkerProcessProxy(Options())) valid = await real.MeasureSceneAsync(input,source,CancellationToken.None);
        string capability = Path.Combine(directory.Path,"cap.json"), forged = Path.Combine(directory.Path,"result.json"), script = Path.Combine(directory.Path,"forged.py");
        await File.WriteAllBytesAsync(capability,SceneMeasurementContract.EncodeBounded(new WorkerSceneMeasurementCapabilities(SceneMeasurementContract.Profile,
            StaticProjectSnapshotCodec.EngineIdentity,[StaticProjectSnapshotCodec.Profile,StaticProjectSnapshotCodec.AnalysisProfile,StaticProjectSnapshotCodec.PresetsProfile])));
        await File.WriteAllTextAsync(script,"""
import sys,json,struct,hashlib,base64
while True:
 p=sys.stdin.buffer.read(4)
 if not p: break
 m=json.loads(sys.stdin.buffer.read(struct.unpack('<I',p)[0]))
 if m['kind']=='hello': r={'kind':'hello','requestId':m['requestId'],'payload':''}
 elif m['kind']=='shutdown': break
 else:
  f=sys.argv[1] if m['kind']=='scene-measurement-capabilities-v1' else sys.argv[2]
  b=open(f,'rb').read();r={'kind':'result','requestId':m['requestId'],'inputSha256':m['inputSha256'],'payload':base64.b64encode(b).decode(),'outputSha256':hashlib.sha256(b).hexdigest()}
 b=json.dumps(r).encode();sys.stdout.buffer.write(struct.pack('<I',len(b))+b);sys.stdout.buffer.flush()
""");
        foreach (string mutation in new[] { "source","profile","time","identity","duplicate","matrix","png","kind","slot-role" })
        {
            var root = JsonSerializer.SerializeToNode(valid,SceneMeasurementContract.JsonOptions)!;
            var data = root["measurement"]!;
            var elements = data["elements"]!.AsArray();
            switch (mutation)
            {
                case "source": data["sourceSha256"] = new string('f',64); break;
                case "profile": data["samplingProfile"] = "unnegotiated"; break;
                case "time": data["timeUs"] = "250001"; break;
                case "identity": elements[0]!["id"] = "not-in-source"; break;
                case "duplicate": elements.Add(elements[0]!.DeepClone()); break;
                case "matrix": elements.First(element => element!["worldMatrix"] is not null)!["worldMatrix"]!["a"] = "NaN"; break;
                case "png": data["png"] = Convert.ToBase64String([1,2,3]); data["pngSha256"] = WorkerProtocol.ComputeSha256([1,2,3]); break;
                case "kind": elements.First(element => element!["kind"]!.GetValue<string>() == "line")!["kind"] = "container"; break;
                case "slot-role": elements.First(element => element!["kind"]!.GetValue<string>() == "line")!["slotId"] = "scheduler-role"; break;
            }
            await File.WriteAllTextAsync(forged,root.ToJsonString(SceneMeasurementContract.JsonOptions));
            await using var worker = new WorkerProcessProxy(HostingTestPlatformHelpers.PythonOptions(script,[capability,forged]));
            await Assert.ThrowsAsync<WorkerProtocolException>(() => worker.MeasureSceneAsync(input,source,CancellationToken.None));
            int? pid = worker.ProcessId; await worker.DisposeAsync(); if (pid is { } actual) Assert.False(Alive(actual));
            output.WriteLine("Rejected complete forged measurement: " + mutation);
        }
    }

    private static WorkerSceneMeasurementInput Input(byte[] source, SceneMeasurementRequest request)
    {
        var input = new WorkerSceneMeasurementInput(request,WorkerProtocol.ComputeSha256(source),"");
        return input with { SampleInputFingerprint = WorkerSceneMeasurementContract.Fingerprint(source,input) };
    }
    private static ProjectSnapshot Snapshot() => new() { Name = "measure",Composition = new() { CanvasWidth = 320,CanvasHeight = 180 },
        Lyrics = LyricsDocument.Create(lines:[new LyricLine { Id = "actual-line",Text = "Measured text",Time = new TimeRange(0,2_000_000), TimingStatus = TimingStatus.Confirmed }]) };

    private static ProjectSnapshot PackagePresetSnapshot()
    {
        var source = Snapshot();
        var package = EffectPackageFixture.Contents();
        var template = StyleTemplate.Create("preset-template", "preset", StyleTemplateKind.Line,
            source.LyricStyle with { Opacity = .8f }, [LyricStyleProperty.Opacity]);
        var project = new Project
        {
            Name = source.Name,
            SchemaVersion = source.SchemaVersion,
            Composition = source.Composition,
            Lyrics = source.Lyrics,
            LyricPresentation = source.LyricPresentation,
            LyricStyle = source.LyricStyle,
            Background = source.Background,
            Effects = new([package]),
            PresetApplications = new([new PresetApplication("preset-instance", new(ProjectEffectBindingKind.Project), template)]),
        };
        return ProjectSnapshot.FromProject(project);
    }
    private static WorkerProcessOptions Options()
    {
        string path = WorkerBinaryLocator.LocateBackendWorkerDll();
        Assert.True(File.Exists(path)); return new("dotnet",[path]);
    }
    private static bool Alive(int pid) { try { using var process = Process.GetProcessById(pid); return !process.HasExited; } catch (ArgumentException) { return false; } }
}
