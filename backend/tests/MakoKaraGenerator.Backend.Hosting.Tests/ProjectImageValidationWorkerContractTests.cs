using System.Text;
using System.Text.Json.Nodes;
using MakoKaraGenerator.Backend.Contracts;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

public sealed class ProjectImageValidationWorkerContractTests
{
    [Fact]
    public void 独立能力完整往返并固定全部预算与来源()
    {
        var value = Capabilities();
        var decoded = ProjectImageValidationWorkerContract.DecodeCapabilities(ProjectImageValidationWorkerContract.EncodeCapabilities(value));
        Assert.Equal(value.EngineIdentity, decoded.EngineIdentity);
        Assert.Equal(value.CodecProfile, decoded.CodecProfile);
        Assert.Equal(value.BaseProfiles, decoded.BaseProfiles);
        Assert.Equal(value.ImageDecoderProfile, decoded.ImageDecoderProfile);
        Assert.Equal(4_194_272, decoded.MaximumInputBytes);
        Assert.Equal(2_097_152, decoded.MaximumImageBytes);
        Assert.Equal(4096, decoded.MaximumDimension);
        Assert.Equal(16_777_216, decoded.MaximumDecodedBytes);
        Assert.Equal(4_194_304, decoded.MaximumBaseSnapshotBytes);
        Assert.Equal(200_000, decoded.MaximumSourceValues);
        Assert.Equal(262_144, decoded.MaximumReportBytes);
        Assert.Equal(WorkerPackageDiagnosticsContract.Version, decoded.PackageDiagnosticsVersion);
        Assert.Equal(value.PackageCapabilities, decoded.PackageCapabilities);
        Assert.Equal("memory-image-bgra-v1", decoded.ImageDecoderProfile);
        Assert.Empty(ProjectImageValidationWorkerContract.EncodeCapabilitiesRequest());
        Assert.NotEqual(ProjectImagePreviewWorkerContract.CapabilitiesKind, ProjectImageValidationWorkerContract.CapabilitiesKind);
        Assert.NotEqual(PackageRenderWorkerContract.ValidationCapabilitiesKind, ProjectImageValidationWorkerContract.CapabilitiesKind);
        Assert.NotEqual(WorkerValidationContract.RequestKind, ProjectImageValidationWorkerContract.RequestKind);
        Assert.False(BackendSnapshotProfiles.IsSupported(decoded.CodecProfile));
    }

    [Theory]
    [InlineData("engineIdentity", " ")]
    [InlineData("codecProfile", "project-image-preview-v1")]
    [InlineData("codecProfile", "package-lyrics-scene-v1")]
    [InlineData("imageDecoderProfile", "native-image-v1")]
    [InlineData("maximumInputBytes", 4194280)]
    [InlineData("maximumImageBytes", 2097151)]
    [InlineData("maximumDimension", 4095)]
    [InlineData("maximumDecodedBytes", 16777215)]
    [InlineData("maximumBaseSnapshotBytes", 4194303)]
    [InlineData("maximumSourceValues", 199999)]
    [InlineData("maximumReportBytes", 262143)]
    [InlineData("packageDiagnosticsVersion", 0)]
    [InlineData("packageCapabilities.engineIdentity", "other-engine")]
    [InlineData("packageCapabilities.scriptRefsIdentity", "BAD")]
    [InlineData("packageCapabilities.scriptRefsCount", 0)]
    [InlineData("packageCapabilities.packageDiagnosticsVersion", 0)]
    public void 能力预算解码器引擎与实际引用不匹配全部拒绝(string field, object value)
    {
        JsonObject json = CapabilityJson();
        string[] path = field.Split('.');
        JsonObject owner = path.Length == 2 ? json[path[0]]!.AsObject() : json;
        owner[path[^1]] = value is int number ? JsonValue.Create(number) : JsonValue.Create((string)value);
        Assert.Throws<WorkerProtocolException>(() => Decode(json));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("unknown")]
    [InlineData("null-package")]
    [InlineData("duplicate-base")]
    [InlineData("unknown-base")]
    [InlineData("empty-base")]
    public void 能力缺项未知项与基础来源严格拒绝(string mode)
    {
        JsonObject json = CapabilityJson();
        switch (mode)
        {
            case "missing": json.Remove("maximumInputBytes"); break;
            case "unknown": json["imagePath"] = "image.png"; break;
            case "null-package": json["packageCapabilities"] = null; break;
            case "duplicate-base": json["baseProfiles"]![1] = BackendSnapshotProfiles.StaticLyricsScene; break;
            case "unknown-base": json["baseProfiles"]![1] = BackendSnapshotProfiles.StaticLyricsScenePresets; break;
            case "empty-base": json["baseProfiles"] = new JsonArray(); break;
        }
        Assert.Equal("WorkerImageValidationCapabilitiesInvalid", Assert.Throws<WorkerProtocolException>(() => Decode(json)).Code);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("oversized")]
    [InlineData("malformed")]
    [InlineData("duplicate")]
    [InlineData("nested-duplicate")]
    public void 能力完整JSON预算及大小写重复字段拒绝(string mode)
    {
        string json = CapabilityJson().ToJsonString();
        byte[] bytes = mode switch
        {
            "empty" => [],
            "oversized" => new byte[8193],
            "malformed" => "{"u8.ToArray(),
            "duplicate" => Encoding.UTF8.GetBytes(json.Replace("\"engineIdentity\":", "\"EngineIdentity\":\"engine\",\"engineIdentity\":", StringComparison.Ordinal)),
            "nested-duplicate" => Encoding.UTF8.GetBytes(json.Replace("\"scriptRefsCount\":", "\"ScriptRefsCount\":1,\"scriptRefsCount\":", StringComparison.Ordinal)),
            _ => throw new ArgumentException(mode),
        };
        Assert.Equal("WorkerImageValidationCapabilitiesInvalid", Assert.Throws<WorkerProtocolException>(() => ProjectImageValidationWorkerContract.DecodeCapabilities(bytes)).Code);
    }

    [Fact]
    public void KTI1完整预算往返且与旧KTV1静态包任务相互隔离()
    {
        Guid id = Guid.NewGuid();
        byte[] snapshot = new byte[ProjectImageValidationWorkerContract.MaximumInputBytes];
        snapshot[0] = 19; snapshot[^1] = 23;
        byte[] bytes = ProjectImageValidationWorkerContract.EncodeInput(id, 600, snapshot);
        Assert.Equal(WorkerProtocol.MaxPayloadBytes, bytes.Length);
        Assert.Equal("KTI1", Encoding.ASCII.GetString(bytes, 0, 4));
        WorkerValidationInput input = ProjectImageValidationWorkerContract.DecodeInput(bytes);
        Assert.Equal(id, input.TaskId);
        Assert.Equal(600, input.FramesPerOutput);
        Assert.Equal(snapshot, input.SnapshotBytes);
        Assert.Equal(WorkerProtocol.ComputeSha256(snapshot), input.SourceSha256);
        Assert.Equal(BackendSnapshotProfiles.ProjectImageValidation, input.Profile);
        snapshot[0] = 31;
        Assert.Equal(19, input.SnapshotBytes[0]);
        Assert.Throws<WorkerProtocolException>(() => WorkerValidationContract.DecodeInput(bytes));
        foreach (bool package in new[] { false, true })
            Assert.Throws<WorkerProtocolException>(() => ProjectImageValidationWorkerContract.DecodeInput(WorkerValidationContract.EncodeInput(id, 1, [1], package)));
        Assert.Throws<WorkerProtocolException>(() => ProjectImageValidationWorkerContract.EncodeInput(id, 1, new byte[4_194_273]));
        Assert.Throws<WorkerProtocolException>(() => ProjectImageValidationWorkerContract.EncodeInput(id, 1, []));
        Assert.Throws<WorkerProtocolException>(() => ProjectImageValidationWorkerContract.EncodeInput(Guid.Empty, 1, [1]));
    }

    [Theory]
    [InlineData("magic")]
    [InlineData("version")]
    [InlineData("discriminator")]
    [InlineData("reserved6")]
    [InlineData("reserved7")]
    [InlineData("reserved28")]
    [InlineData("zero-id")]
    [InlineData("zero-plan")]
    [InlineData("overflow-plan")]
    [InlineData("short")]
    [InlineData("empty-snapshot")]
    [InlineData("overflow")]
    public void KTI1畸形头计划与完整payload预算拒绝(string mode)
    {
        byte[] bytes = ProjectImageValidationWorkerContract.EncodeInput(Guid.NewGuid(), 1, [1]);
        switch (mode)
        {
            case "magic": bytes[0] = 0; break;
            case "version": bytes[4] = 2; break;
            case "discriminator": bytes[5] = 0; break;
            case "reserved6": bytes[6] = 1; break;
            case "reserved7": bytes[7] = 1; break;
            case "reserved28": bytes[28] = 1; break;
            case "zero-id": bytes.AsSpan(8, 16).Clear(); break;
            case "zero-plan": bytes.AsSpan(24, 4).Clear(); break;
            case "overflow-plan": bytes[25] = 3; break;
            case "short": bytes = [1, 2]; break;
            case "empty-snapshot": bytes = bytes[..32]; break;
            case "overflow": bytes = new byte[WorkerProtocol.MaxPayloadBytes + 1]; break;
        }
        Assert.Throws<WorkerProtocolException>(() => ProjectImageValidationWorkerContract.DecodeInput(bytes));
    }

    private static ProjectImageValidationWorkerCapabilities Capabilities() => new("engine", BackendSnapshotProfiles.ProjectImageValidation,
        [BackendSnapshotProfiles.StaticLyricsScene, BackendSnapshotProfiles.StaticLyricsSceneAnalysis, BackendSnapshotProfiles.PackageLyricsScene],
        ProjectImageValidationWorkerContract.ImageDecoderProfile, ProjectImageValidationWorkerContract.MaximumInputBytes,
        ProjectImageValidationWorkerContract.MaximumImageBytes, ProjectImageValidationWorkerContract.MaximumDimension,
        ProjectImageValidationWorkerContract.MaximumDecodedBytes, ProjectImageValidationWorkerContract.MaximumBaseSnapshotBytes,
        ProjectImageValidationWorkerContract.MaximumSourceValues, WorkerValidationContract.MaximumReportBytes,
        WorkerPackageDiagnosticsContract.Version,
        new("engine", BackendSnapshotProfiles.PackageLyricsScene, 1, "sdk", "shader", new string('a', 64), 1));

    private static JsonObject CapabilityJson() => JsonNode.Parse(ProjectImageValidationWorkerContract.EncodeCapabilities(Capabilities()))!.AsObject();
    private static void Decode(JsonObject json) => ProjectImageValidationWorkerContract.DecodeCapabilities(Encoding.UTF8.GetBytes(json.ToJsonString()));
}
