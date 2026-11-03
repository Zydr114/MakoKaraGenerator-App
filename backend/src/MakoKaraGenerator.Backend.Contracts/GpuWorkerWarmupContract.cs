namespace MakoKaraGenerator.Backend.Contracts;

/// <summary>未绑定业务输入的私有准备协议；不创建流、池、consumer 等待或执行作者代码。</summary>
public static class GpuWorkerWarmupContract
{
    public const string RequestKind = "gpu-worker-warmup-v1";
    public const string ResultKind = "gpu-worker-warmed-v1";
    public const string DeviceStateKind = "gpu-worker-device-state-v1";
    public const string DeviceStateResultKind = "gpu-worker-device-state-result-v1";
    public const string FailureCode = "WorkerGpuWarmupInvalid";

    public static byte[] EncodeRequest(GpuWorkerWarmupIdentity identity)
    { Validate(identity); return GpuStreamWorkerContract.Encode(identity); }

    public static GpuWorkerWarmupIdentity DecodeRequest(ReadOnlySpan<byte> bytes)
    {
        var identity = GpuStreamWorkerContract.Decode<GpuWorkerWarmupIdentity>(bytes);
        Validate(identity); return identity;
    }

    public static byte[] EncodeResult(GpuWorkerWarmupResult result)
    { Validate(result, result.Identity); return GpuStreamWorkerContract.Encode(result); }

    public static GpuWorkerWarmupResult DecodeResult(ReadOnlySpan<byte> bytes, GpuWorkerWarmupIdentity expected)
    {
        var result = GpuStreamWorkerContract.Decode<GpuWorkerWarmupResult>(bytes);
        Validate(result, expected); return result;
    }

    private static void Validate(GpuWorkerWarmupIdentity identity)
    {
        if (identity is null || identity.Version != 1 || identity.ReferenceCount is < 1 or > 1024 ||
            !Hash(identity.ReferenceIdentity) || !Hash(identity.SdkIdentity) ||
            string.IsNullOrWhiteSpace(identity.EngineIdentity) || identity.EngineIdentity.Length > 256 ||
            identity.EngineIdentity.Any(char.IsControl)) throw Invalid();
    }

    private static void Validate(GpuWorkerWarmupResult result, GpuWorkerWarmupIdentity expected)
    {
        Validate(expected);
        if (result is null || result.Identity != expected || result.WorkerPid <= 0 ||
            !Guid.TryParseExact(result.PreparationId, "N", out _) || !result.OriginalDeviceRetained ||
            result.ReferenceBytes <= 0 || result.ReferenceBytes > 512L * 1024 * 1024 || result.Api != "vulkan" ||
            !Guid.TryParseExact(result.InitializationId, "N", out _) || result.DeviceInitializations != 1 || result.ContextInitializations != 1 ||
            result.RuntimeAssemblies is not { Length: >= 1 and <= 32 } || result.RuntimeAssemblies.Any(a =>
                a is null || string.IsNullOrWhiteSpace(a.AssemblyIdentity) || a.AssemblyIdentity.Length > 512 ||
                a.AssemblyIdentity.Any(char.IsControl) || !Guid.TryParseExact(a.ModuleVersionId, "N", out _))) throw Invalid();
    }

    private static bool Hash(string? value) => value is { Length: 64 } &&
        value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static WorkerProtocolException Invalid() => new("GPU Worker 预热私有消息无效。", FailureCode);
}

public sealed record GpuWorkerWarmupIdentity(int Version, string ReferenceIdentity, int ReferenceCount,
    string SdkIdentity, string EngineIdentity);

public sealed record GpuWorkerWarmupResult(GpuWorkerWarmupIdentity Identity, string PreparationId,
    int WorkerPid, string Api, bool OriginalDeviceRetained, long ReferenceBytes, GpuWorkerRuntimeAssembly[] RuntimeAssemblies,
    string InitializationId, int DeviceInitializations, int ContextInitializations);

public sealed record GpuWorkerBindingProof(string PreparationId, string InitializationId,
    int DeviceInitializations, int ContextInitializations, bool OriginalDeviceBound);

public sealed record GpuWorkerRuntimeAssembly(string AssemblyIdentity, string ModuleVersionId);
