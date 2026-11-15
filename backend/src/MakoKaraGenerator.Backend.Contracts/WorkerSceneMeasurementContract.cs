using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MakoKaraGenerator.Backend.Contracts;

public sealed record WorkerSceneMeasurementInput(SceneMeasurementRequest Request, string SourceSha256,
    string SampleInputFingerprint, string? EditingLineId = null, string? EditingCueId = null, string? AttachmentContainerId = null);
public sealed record WorkerAttachmentGeometryDto(string ContainerId, string LineId,
    ProjectGeometryReferenceDto Previous, ProjectGeometryReferenceDto FixedParent,
    ProjectGeometryReferenceDto TargetText, ProjectGeometryReferenceDto TargetAboveStart);
public sealed record WorkerSceneMeasurementOutput(SceneMeasurementDataDto Measurement, WorkerAttachmentGeometryDto? Attachment = null);
public sealed record WorkerSceneMeasurementCapabilities(string Profile, string EngineIdentity, string[] SourceProfiles);

/// <summary>独立执行能力；仍消费三个既有静态snapshot源profile。</summary>
public static class WorkerSceneMeasurementContract
{
    public const string CapabilitiesKind = "scene-measurement-capabilities-v1";
    public const string RequestKind = "scene-measurement-v1";
    public static byte[] CapabilitiesRequest() => Encoding.UTF8.GetBytes(SceneMeasurementContract.Profile);
    public static string Fingerprint(byte[] source, WorkerSceneMeasurementInput input)
    {
        JsonObject descriptor = JsonSerializer.SerializeToNode(new { profile = SceneMeasurementContract.Profile,
            sampling = input.Request.Mode == "layout" ? SceneMeasurementContract.LayoutProfile : SceneMeasurementContract.TimedProfile,
            input.Request.Mode, input.Request.TimeUs, input.Request.Width, input.Request.Height, input.Request.AlphaMode,
            input.EditingLineId, input.EditingCueId, input.AttachmentContainerId }, SceneMeasurementContract.JsonOptions)!.AsObject();
        if (input.Request.RenderBackend is not null) descriptor["renderBackend"] = input.Request.RenderBackend;
        return WorkerProtocol.ComputeSha256([.. source, .. JsonSerializer.SerializeToUtf8Bytes(descriptor, SceneMeasurementContract.JsonOptions)]);
    }
    public static byte[] Encode(WorkerSceneMeasurementInput input, byte[] source)
    {
        SceneMeasurementContract.ValidateRequest(input.Request);
        if (WorkerProtocol.ComputeSha256(source) != input.SourceSha256 || Fingerprint(source, input) != input.SampleInputFingerprint)
            throw new WorkerProtocolException("测量输入指纹不匹配。");
        byte[] metadata = JsonSerializer.SerializeToUtf8Bytes(input, SceneMeasurementContract.JsonOptions);
        int length = checked(4 + metadata.Length + source.Length);
        if (length > WorkerProtocol.MaxPayloadBytes) throw new WorkerProtocolException("测量输入含metadata后超过4MiB。", "SceneMeasurementBudgetExceeded");
        byte[] result = new byte[length]; BinaryPrimitives.WriteInt32LittleEndian(result, metadata.Length);
        metadata.CopyTo(result, 4); source.CopyTo(result, 4 + metadata.Length); return result;
    }
    public static (WorkerSceneMeasurementInput Input, byte[] Source) Decode(byte[] payload)
    {
        if (payload.Length < 5 || payload.Length > WorkerProtocol.MaxPayloadBytes) throw new WorkerProtocolException("测量输入长度无效。");
        int length = BinaryPrimitives.ReadInt32LittleEndian(payload);
        if (length is < 1 or > 16384 || length >= payload.Length - 4) throw new WorkerProtocolException("测量metadata长度无效。");
        var input = JsonSerializer.Deserialize<WorkerSceneMeasurementInput>(payload.AsSpan(4, length), SceneMeasurementContract.JsonOptions)
            ?? throw new WorkerProtocolException("测量metadata为空。");
        byte[] source = payload[(4 + length)..]; _ = Encode(input, source); return (input, source);
    }
}
