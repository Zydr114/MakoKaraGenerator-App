using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MakoKaraGenerator.Backend.Contracts;

/// <summary>统一固定 Runtime 输入的正式验证能力；旧 Worker 不得推断为支持。</summary>
public static class RuntimeValidationWorkerContract
{
    public const string Profile = "runtime-project-input-v1";
    public const string CapabilitiesKind = "runtime-validation-capabilities-v1";
    public const string CapabilitiesResultKind = "runtime-validation-capabilities-result-v1";
    public const string RequestKind = "task-runtime-validation-v1";
    public const string ProgressKind = "task-runtime-validation-progress-v1";
    public const string ResultKind = "task-runtime-validation-result-v1";
    public const int MaximumInputBytes = WorkerValidationContract.MaximumSnapshotBytes;
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true, MaxDepth = 8 };

    public static byte[] EncodeCapabilities(RuntimeValidationWorkerCapabilities value)
    { Validate(value); return JsonSerializer.SerializeToUtf8Bytes(value, Options); }

    public static RuntimeValidationWorkerCapabilities DecodeCapabilities(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is <= 0 or > 8192) throw Invalid();
        try
        {
            using var document = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = 8 });
            Duplicates(document.RootElement);
            var value = JsonSerializer.Deserialize<RuntimeValidationWorkerCapabilities>(bytes, Options) ?? throw Invalid();
            Validate(value); return value;
        }
        catch (JsonException) { throw Invalid(); }
    }

    private static void Validate(RuntimeValidationWorkerCapabilities value)
    {
        if (value.Profile != Profile || value.MaximumInputBytes != MaximumInputBytes
            || value.MaximumReportBytes != WorkerValidationContract.MaximumReportBytes || value.PackageCapabilities is null)
            throw Invalid();
        PackageRenderWorkerContract.ValidateCapabilities(value.PackageCapabilities);
    }

    private static void Duplicates(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
            foreach (var property in value.EnumerateObject())
            { if (!names.Add(property.Name)) throw Invalid(); Duplicates(property.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) Duplicates(item);
    }

    public static byte[] EncodeInput(Guid taskId, int framesPerOutput, ReadOnlySpan<byte> source)
    {
        WorkerValidationContract.ValidatePlan(framesPerOutput);
        if (taskId == Guid.Empty || source.Length is <= 0 or > MaximumInputBytes) throw InputInvalid();
        byte[] bytes = new byte[WorkerValidationContract.HeaderBytes + source.Length];
        "KTR1"u8.CopyTo(bytes); bytes[4] = 1;
        taskId.TryWriteBytes(bytes.AsSpan(8, 16));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(24, 4), framesPerOutput);
        source.CopyTo(bytes.AsSpan(WorkerValidationContract.HeaderBytes)); return bytes;
    }

    public static WorkerValidationInput DecodeInput(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length <= WorkerValidationContract.HeaderBytes || bytes.Length > WorkerProtocol.MaxPayloadBytes
            || !bytes[..4].SequenceEqual("KTR1"u8) || bytes[4] != 1 || bytes[5] != 0 || bytes[6] != 0 || bytes[7] != 0
            || BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(28, 4)) != 0) throw InputInvalid();
        Guid taskId = new(bytes.Slice(8, 16));
        int frames = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(24, 4));
        WorkerValidationContract.ValidatePlan(frames);
        if (taskId == Guid.Empty) throw InputInvalid();
        byte[] source = bytes[WorkerValidationContract.HeaderBytes..].ToArray();
        return new(taskId, frames, source, WorkerProtocol.ComputeSha256(source), Profile);
    }
    private static WorkerProtocolException Invalid() => new("Worker 未提供统一 Runtime 正式验证能力。", "WorkerRuntimeValidationUnavailable");
    private static WorkerProtocolException InputInvalid() => new("固定 Runtime 验证输入无效。", "WorkerRuntimeValidationInputRejected");
}

public sealed record RuntimeValidationWorkerCapabilities(string Profile, int MaximumInputBytes, int MaximumReportBytes,
    PackageRenderWorkerCapabilities PackageCapabilities);
