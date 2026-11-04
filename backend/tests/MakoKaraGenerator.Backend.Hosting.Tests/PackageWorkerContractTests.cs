using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Rendering;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

public sealed class PackageWorkerContractTests
{
    [Theory]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    [InlineData("null-identity")]
    [InlineData("uppercase-identity")]
    [InlineData("empty-refs")]
    [InlineData("missing-diagnostics-version")]
    [InlineData("old-diagnostics-version")]
    public void 能力JSON拒绝未知重复字段空引用与非canonical身份(string damage)
    {
        var value = new PackageRenderWorkerCapabilities("engine", PackageProjectSnapshotCodec.Profile, 1, "sdk", "shader", new string('a', 64), 1);
        byte[] valid = PackageRenderWorkerContract.EncodeCapabilities(value);
        Assert.Equal(value, PackageRenderWorkerContract.DecodeCapabilities(valid));
        string json = Encoding.UTF8.GetString(valid);
        json = damage switch
        {
            "unknown" => json[..^1] + ",\"unexpected\":true}",
            "duplicate" => json[..^1] + ",\"engineIdentity\":\"different-engine\"}",
            "null-identity" => json.Replace("\"" + new string('a', 64) + "\"", "null", StringComparison.Ordinal),
            "uppercase-identity" => json.Replace(new string('a', 64), new string('A', 64), StringComparison.Ordinal),
            "missing-diagnostics-version" => json.Replace(",\"packageDiagnosticsVersion\":1", "", StringComparison.Ordinal),
            "old-diagnostics-version" => json.Replace("\"packageDiagnosticsVersion\":1", "\"packageDiagnosticsVersion\":0", StringComparison.Ordinal),
            "empty-refs" => json.Replace("\"scriptRefsCount\":1", "\"scriptRefsCount\":0", StringComparison.Ordinal),
            _ => throw new ArgumentException(damage),
        };
        Assert.Throws<WorkerProtocolException>(() => PackageRenderWorkerContract.DecodeCapabilities(Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void 包open扣除固定头预算并复制输入且不扩公共静态profile()
    {
        Guid id = Guid.NewGuid();
        byte[] source = [1, 2, 3];
        byte[] payload = PackageRenderWorkerContract.EncodeOpen(id, source);
        source[0] = 9;
        var decoded = PackageRenderWorkerContract.DecodeOpen(payload);
        Assert.Equal(id, decoded.SessionId);
        Assert.Equal([1, 2, 3], decoded.SnapshotBytes);
        Assert.Equal(WorkerProtocol.ComputeSha256([1, 2, 3]), decoded.SourceSha256);
        payload[^1] = 9;
        Assert.Equal([1, 2, 3], decoded.SnapshotBytes);
        Assert.Equal(24, PackageRenderWorkerContract.OpenHeaderBytes);
        Assert.Equal(WorkerProtocol.MaxPayloadBytes - 24, PackageRenderWorkerContract.MaximumSnapshotBytes);
        Assert.Equal(WorkerProtocol.MaxPayloadBytes,
            PackageRenderWorkerContract.EncodeOpen(id, new byte[PackageRenderWorkerContract.MaximumSnapshotBytes]).Length);
        Assert.Throws<WorkerProtocolException>(() => PackageRenderWorkerContract.EncodeOpen(id,
            new byte[PackageRenderWorkerContract.MaximumSnapshotBytes + 1]));
        Assert.Throws<WorkerProtocolException>(() => PackageRenderWorkerContract.EncodeOpen(id, []));
        Assert.False(BackendSnapshotProfiles.IsSupported(PackageProjectSnapshotCodec.Profile));
    }

    [Fact]
    public void 验证能力必须明确声明package_profile()
    {
        var value = new PackageRenderWorkerCapabilities("engine", PackageProjectSnapshotCodec.Profile, 1, "sdk", "shader", new string('a', 64), 1,
            ValidationProfiles: [PackageRenderWorkerContract.ValidationProfile]);
        var decoded = PackageRenderWorkerContract.DecodeCapabilities(PackageRenderWorkerContract.EncodeCapabilities(value));
        Assert.Equal(value with { ValidationProfiles = null }, decoded with { ValidationProfiles = null });
        Assert.Equal(value.ValidationProfiles, decoded.ValidationProfiles);
        Assert.Throws<WorkerProtocolException>(() => PackageRenderWorkerContract.EncodeCapabilities(value with { ValidationProfiles = ["unknown"] }));
        Assert.Throws<WorkerProtocolException>(() => PackageRenderWorkerContract.EncodeCapabilities(value with
        { ValidationProfiles = [PackageRenderWorkerContract.ValidationProfile, PackageRenderWorkerContract.ValidationProfile] }));
    }

    [Fact]
    public void 支持包身份列表往返保留旧主字段且旧JSON仍可结构解码()
    {
        var legacy = new PackageRenderWorkerCapabilities("engine", PackageProjectSnapshotCodec.Profile, 2, "1.0.0", "v1-shader", new string('a', 64), 1);
        var current = legacy with { SupportedPackageProfiles = WorkerPackageProfileAdmission.Current };
        var decoded = PackageRenderWorkerContract.DecodeCapabilities(PackageRenderWorkerContract.EncodeCapabilities(current));
        Assert.Equal(legacy, decoded with { SupportedPackageProfiles = null });
        Assert.Equal(current.SupportedPackageProfiles, decoded.SupportedPackageProfiles);
        Assert.Null(PackageRenderWorkerContract.DecodeCapabilities(PackageRenderWorkerContract.EncodeCapabilities(legacy)).SupportedPackageProfiles);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("duplicate")]
    [InlineData("null-item")]
    [InlineData("missing-version")]
    [InlineData("missing-sdk")]
    [InlineData("empty-shader")]
    [InlineData("unknown-field")]
    [InlineData("duplicate-field")]
    [InlineData("too-many")]
    public void 支持包身份列表拒绝空重复缺失未知及嵌套重复字段(string damage)
    {
        var value = new PackageRenderWorkerCapabilities("engine", PackageProjectSnapshotCodec.Profile, 2, "1.0.0", "v1-shader", new string('a', 64), 1,
            SupportedPackageProfiles: WorkerPackageProfileAdmission.Current);
        var json = JsonNode.Parse(PackageRenderWorkerContract.EncodeCapabilities(value))!.AsObject();
        var profiles = json["supportedPackageProfiles"]!.AsArray();
        switch (damage)
        {
            case "empty": profiles.Clear(); break;
            case "duplicate": profiles.Add(profiles[0]!.DeepClone()); break;
            case "null-item": profiles.Add((JsonNode?)null); break;
            case "missing-version": profiles[0]!.AsObject().Remove("packageVersion"); break;
            case "missing-sdk": profiles[0]!.AsObject().Remove("sdkVersion"); break;
            case "empty-shader": profiles[0]!["shaderProfile"] = ""; break;
            case "unknown-field": profiles[0]!["unexpected"] = true; break;
            case "too-many": for (int i = 0; i < 15; i++) profiles.Add(new JsonObject { ["packageVersion"] = i + 4, ["sdkVersion"] = "future", ["shaderProfile"] = "future" }); break;
        }
        string encoded = json.ToJsonString();
        if (damage == "duplicate-field") encoded = encoded.Replace("\"packageVersion\":3", "\"packageVersion\":3,\"PackageVersion\":3", StringComparison.Ordinal);
        var error = Assert.Throws<WorkerProtocolException>(() => PackageRenderWorkerContract.DecodeCapabilities(Encoding.UTF8.GetBytes(encoded)));
        Assert.Equal("WorkerPackageCapabilitiesInvalid", error.Code);
    }

    [Theory]
    [InlineData("session")]
    [InlineData("version")]
    [InlineData("reserved")]
    [InlineData("size")]
    public void 包二进制拒绝空身份错误版本保留位与额外尾部(string damage)
    {
        var request = Request();
        byte[] frame = PackageRenderWorkerContract.EncodeFrame(request);
        if (damage == "session") frame.AsSpan(8, 16).Clear();
        if (damage == "version") frame[4] = 2;
        if (damage == "reserved") frame[7] = 1;
        if (damage == "size") frame = [.. frame, 0];
        Assert.Throws<WorkerProtocolException>(() => PackageRenderWorkerContract.DecodeFrame(frame));
        Assert.Equal(request, PackageRenderWorkerContract.DecodeFrame(PackageRenderWorkerContract.EncodeFrame(request)));
        var close = PackageRenderWorkerContract.DecodeClose(
            PackageRenderWorkerContract.EncodeClose(request.SessionId, request.SourceSha256, 2));
        Assert.Equal(request.SessionId, close.SessionId);
        Assert.Equal(request.SourceSha256, close.SourceSha256);
        Assert.Equal(2, close.Sequence);
    }

    [Fact]
    public void 帧字节在解码阶段拒绝溢出尺寸非法时间质量透明与序号()
    {
        foreach (Action<byte[]> corrupt in new Action<byte[]>[]
        {
            bytes => BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(56), int.MaxValue),
            bytes => BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(64), -1),
            bytes => BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(72), 0),
            bytes => bytes[5] = 2,
            bytes => bytes[6] = 3,
        })
        {
            byte[] frame = PackageRenderWorkerContract.EncodeFrame(Request());
            corrupt(frame);
            Assert.Throws<WorkerProtocolException>(() => PackageRenderWorkerContract.DecodeFrame(frame));
        }
    }

    [Theory]
    [InlineData(PackageRenderWorkerContract.CapabilitiesKind)]
    [InlineData(PackageRenderWorkerContract.OpenKind)]
    [InlineData(PackageRenderWorkerContract.RenderKind)]
    [InlineData(PackageRenderWorkerContract.CloseKind)]
    public void 所有包操作也进入通用固定输入SHA门禁(string kind)
    {
        Assert.Throws<WorkerProtocolException>(() => WorkerProtocol.Validate(new(kind, "invalid-hash", new string('0', 64), [1])));
    }

    [Theory]
    [InlineData("session")]
    [InlineData("source")]
    [InlineData("sequence")]
    [InlineData("input-sha")]
    [InlineData("output-sha")]
    [InlineData("time")]
    [InlineData("size")]
    [InlineData("transparent")]
    [InlineData("quality")]
    [InlineData("missing-diagnostics")]
    [InlineData("error")]
    [InlineData("unsolicited-cancel")]
    [InlineData("foreign-task")]
    [InlineData("foreign-progress")]
    [InlineData("foreign-export")]
    [InlineData("foreign-group")]
    [InlineData("foreign-media-limit")]
    [InlineData("foreign-error-code")]
    [InlineData("foreign-validation-count")]
    [InlineData("foreign-delay")]
    [InlineData("foreign-kind")]
    public void 包成功像素拒绝错关联缺诊断未请求取消和无关任务字段(string damage)
    {
        var request = Request();
        WorkerMessage valid = Result(request);
        PackageRenderWorkerContract.ValidateFrameResult(request, valid);
        WorkerMessage invalid = damage switch
        {
            "session" => valid with { SessionId = Guid.NewGuid().ToString("N") },
            "source" => valid with { SourceSha256 = new string('b', 64) },
            "sequence" => valid with { FrameSequence = 0 },
            "input-sha" => valid with { InputSha256 = new string('0', 64) },
            "output-sha" => valid with { OutputSha256 = new string('0', 64) },
            "time" => valid with { FrameTimeUs = 43 },
            "size" => valid with { FrameStride = 8 },
            "transparent" => valid with { FrameTransparentBackground = true },
            "quality" => valid with { FrameQuality = 0 },
            "missing-diagnostics" => valid with { Diagnostics = null },
            "error" => valid with { Diagnostics = new([StaticWorkerContractTests.Occurrence(1, "Error")], true) },
            "unsolicited-cancel" => valid with { Cancelled = true },
            "foreign-task" => valid with { TaskId = Guid.NewGuid().ToString("N") },
            "foreign-progress" => valid with { ProgressSequence = 1 },
            "foreign-export" => valid with { ExportFormat = "mp4" },
            "foreign-group" => valid with { ProcessGroupId = 1 },
            "foreign-media-limit" => valid with { MediaFileSizeLimitBytes = 10 },
            "foreign-error-code" => valid with { ErrorCode = "OldFailure" },
            "foreign-validation-count" => valid with { ValidationFramesPerOutput = 3 },
            "foreign-delay" => valid with { DelayMilliseconds = 1 },
            "foreign-kind" => valid with { Kind = StaticRenderWorkerContract.FrameResultKind },
            _ => throw new ArgumentException(damage),
        };
        Assert.Throws<WorkerProtocolException>(() => PackageRenderWorkerContract.ValidateFrameResult(request, invalid));
    }

    internal static StaticWorkerFrameRequest Request() => new(Guid.NewGuid(), new string('a', 64), 1, 1, 42, false, 1);

    private static WorkerMessage Result(StaticWorkerFrameRequest request) => new(PackageRenderWorkerContract.FrameResultKind,
        "package-frame", WorkerProtocol.ComputeSha256(PackageRenderWorkerContract.EncodeFrame(request)), [3, 2, 1, 255],
        outputSha256: WorkerProtocol.ComputeSha256([3, 2, 1, 255]), frameWidth: 1, frameHeight: 1, frameStride: 4,
        frameTimeUs: request.TimeUs, frameFormat: RenderFrameV1WorkerResult.RequiredPixelFormat,
        sessionId: request.SessionId.ToString("N"), sourceSha256: request.SourceSha256, frameSequence: request.Sequence,
        frameTransparentBackground: request.TransparentBackground, diagnostics: new([], false), frameQuality: request.Quality, packageDiagnostics: WorkerPackageDiagnosticsContract.Empty);
}
