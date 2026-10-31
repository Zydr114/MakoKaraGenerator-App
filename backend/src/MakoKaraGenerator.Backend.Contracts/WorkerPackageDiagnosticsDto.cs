using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MakoKaraGenerator.Backend.Contracts;

/// <summary>完整私有包诊断值；所有字段必须显式提供，空值不等于缺字段。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorkerPackageDiagnosticsDto(
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] int Version,
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] WorkerPackageSourceDiagnosticDto[] Source,
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] WorkerPackageImageDiagnosticDto[] Image,
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] WorkerPackageMaterialDiagnosticDto[] Material);

/// <summary>完整私有包诊断值；所有字段必须显式提供，空值不等于缺字段。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorkerPackageResourceDto(
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] string PackageId,
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] string Revision,
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] string ExportId);

/// <summary>完整私有包诊断值；所有字段必须显式提供，空值不等于缺字段。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorkerPackageSourceDiagnosticDto(
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] string PackageId,
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] string Revision,
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? InstanceId,
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? ExportId,
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] string Code,
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] string Message,
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] bool IsError,
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Path,
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] int Line,
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] int Column);

/// <summary>完整私有包诊断值；所有字段必须显式提供，空值不等于缺字段。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorkerPackageImageDiagnosticDto(
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] string InstanceId,
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] string ArtifactHash,
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] WorkerPackageResourceDto Graph,
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] string InputId,
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] string Code);

/// <summary>完整私有包诊断值；所有字段必须显式提供，空值不等于缺字段。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorkerPackageMaterialDiagnosticDto(
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] WorkerPackageMaterialOwnerDto Owner,
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] WorkerPackageResourceDto Material,
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] string InputId,
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] string Code);

/// <summary>完整私有包诊断值；所有字段必须显式提供，空值不等于缺字段。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorkerPackageMaterialOwnerDto(
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] string Kind,
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? InstanceId,
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? ArtifactHash,
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] WorkerPackageResourceDto? PackageIdentity,
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Path,
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Region,
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? Slot,
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] WorkerPackageResourceDto? SourceMaterial,
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? ParametersJson);

/// <summary>三类完整诊断的独立条数与UTF8序列化预算；不截断。</summary>
public static class WorkerPackageDiagnosticsContract
{
    public const int Version = 1;
    public const int MaximumItemsPerKind = 256;
    public const int MaximumBytesPerKind = 64 * 1024;
    public const int MaximumItems = 512;
    public const int MaximumBytes = 128 * 1024;
    public static WorkerPackageDiagnosticsDto Empty => new(Version, [], [], []);
    public static void Validate(WorkerPackageDiagnosticsDto value)
    {
        if (value is null || value.Version != Version || value.Source is null || value.Image is null || value.Material is null)
            Invalid();
        Check(value!.Source!); Check(value.Image!); Check(value.Material!);
        if ((long)value.Source!.Length + value.Image!.Length + value.Material!.Length > MaximumItems
            || JsonSerializer.SerializeToUtf8Bytes(value, WorkerProtocol.JsonOptions).Length > MaximumBytes) Budget();
        foreach (var d in value.Source)
        {
            if (d is null || !Text(d.PackageId) || !Revision(d.Revision) || !Text(d.Code) || d.Message is null
                || d.Line < 0 || d.Column < 0 || (d.InstanceId is null) != (d.ExportId is null)
                || d.InstanceId is not null && (!Text(d.InstanceId) || !Text(d.ExportId))) Invalid();
        }
        foreach (var d in value.Image)
        { if (d is null || !Text(d.InstanceId) || !Artifact(d.ArtifactHash) || !Text(d.InputId) || !Text(d.Code)) Invalid(); Resource(d!.Graph); }
        foreach (var d in value.Material)
        {
            if (d is null || !Text(d.InputId) || !Text(d.Code) || d.Owner is null) Invalid();
            Resource(d!.Material); var o = d.Owner!;
            if (o.Kind == "script")
            {
                if (!Text(o.InstanceId) || !Artifact(o.ArtifactHash) || o.Path is not null || o.Region is not null
                    || o.Slot is not null || o.SourceMaterial is not null || o.ParametersJson is not null) Invalid();
                if (o.PackageIdentity is not null) Resource(o.PackageIdentity);
            }
            else if (o.Kind == "source")
            {
                if (o.InstanceId is not null || o.ArtifactHash is not null || o.PackageIdentity is not null
                    || !Text(o.Path) || !Text(o.Region) || o.Slot is null || o.ParametersJson is null) Invalid();
                Resource(o.SourceMaterial!);
            }
            else Invalid();
        }
    }
    private static void Check<T>(T[] values)
    {
        if (values.Length > MaximumItemsPerKind || JsonSerializer.SerializeToUtf8Bytes(values, WorkerProtocol.JsonOptions).Length > MaximumBytesPerKind) Budget();
    }
    private static bool Text(string? value) => !string.IsNullOrWhiteSpace(value);
    private static bool Hex(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool Revision(string? value) => value is { Length: 71 } && value.StartsWith("sha256:", StringComparison.Ordinal) && Hex(value[7..]);
    private static bool Artifact(string? value) => Hex(value) || Revision(value);
    private static void Resource(WorkerPackageResourceDto d)
    { if (d is null || !Text(d.PackageId) || !Revision(d.Revision) || !Text(d.ExportId)) Invalid(); }
    private static void Invalid() => throw new WorkerProtocolException("包诊断字段不完整或无效。", "WorkerPackageDiagnosticsInvalid");
    private static void Budget() => throw new WorkerProtocolException("包诊断超过完整传输预算。", "WorkerPackageDiagnosticsBudgetExceeded");
    internal static void ValidateWire(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) Invalid();
        if (Encoding.UTF8.GetByteCount(element.GetRawText()) > MaximumBytes) Budget();
        foreach (var property in element.EnumerateObject())
            if (property.Name.Equals("source", StringComparison.OrdinalIgnoreCase) || property.Name.Equals("image", StringComparison.OrdinalIgnoreCase)
                || property.Name.Equals("material", StringComparison.OrdinalIgnoreCase))
            {
                if (property.Value.ValueKind != JsonValueKind.Array) Invalid();
                if (property.Value.GetArrayLength() > MaximumItemsPerKind || Encoding.UTF8.GetByteCount(property.Value.GetRawText()) > MaximumBytesPerKind) Budget();
            }
        RejectDuplicates(element);
    }
    internal static void RejectDuplicates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            { if (!names.Add(property.Name)) Invalid(); RejectDuplicates(property.Value); }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) RejectDuplicates(item);
    }
}
