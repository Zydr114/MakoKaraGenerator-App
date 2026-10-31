using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using MakoKaraGenerator.Backend.Contracts;

// Export CLR facts only. Endpoint semantics and public reachability are reviewed in freeze.py.
if (args.Length != 1) throw new ArgumentException("Expected an output JSON path.");
NullabilityInfoContext nullability = new();
var result = new SortedDictionary<string, object>(StringComparer.Ordinal);
foreach (Type type in typeof(HealthResponse).Assembly.GetExportedTypes().OrderBy(t => t.Name, StringComparer.Ordinal))
{
    if (type.IsEnum)
    {
        result.Add(type.Name, new { kind = "enum", values = Enum.GetNames(type).Select(JsonNamingPolicy.CamelCase.ConvertName).ToArray() });
        continue;
    }
    if (!type.Name.EndsWith("Dto", StringComparison.Ordinal)
        && !type.Name.EndsWith("Request", StringComparison.Ordinal)
        && !type.Name.EndsWith("Response", StringComparison.Ordinal)
        && type.Name is not ("GpuPreviewStreamState" or "GpuStreamWorkerState" or "GpuStreamDiagnosticSummary" or "GpuStreamDiagnosticItem")) continue;
    var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(property =>
    {
        // Derived positional records inherit properties whose matching parameters belong to
        // the declaring base record's constructor. Keep failing for unmatched properties.
        Type declaringType = property.DeclaringType ?? throw new InvalidOperationException($"Missing declaring type: {type.Name}.{property.Name}");
        IEnumerable<ConstructorInfo> constructors = declaringType.IsAbstract
            ? declaringType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(c => !c.GetParameters().Any(p => p.ParameterType == declaringType))
            : declaringType.GetConstructors();
        ConstructorInfo constructor = constructors.OrderByDescending(c => c.GetParameters().Length).First();
        var parameters = constructor.GetParameters().ToDictionary(p => p.Name!, StringComparer.OrdinalIgnoreCase);
        if (!parameters.TryGetValue(property.Name, out ParameterInfo? parameter))
            throw new InvalidOperationException($"Unreviewed non-record property: {type.Name}.{property.Name}");
        Type valueType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
        object? defaultValue = parameter.HasDefaultValue ? parameter.DefaultValue : null;
        if (defaultValue is not null && valueType.IsEnum)
            defaultValue = JsonNamingPolicy.CamelCase.ConvertName(Enum.GetName(valueType, defaultValue)!);
        return new
        {
            name = property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? JsonNamingPolicy.CamelCase.ConvertName(property.Name),
            declaringType = declaringType.Name,
            type = valueType.Name,
            nullable = nullability.Create(property).ReadState == NullabilityState.Nullable,
            optional = parameter.HasDefaultValue,
            defaultValue,
            omitNull = property.GetCustomAttribute<JsonIgnoreAttribute>()?.Condition == JsonIgnoreCondition.WhenWritingNull,
        };
    }).ToArray();
    result.Add(type.Name, new
    {
        kind = "record",
        baseType = type.BaseType == typeof(object) ? null : type.BaseType?.Name,
        strict = type.GetCustomAttribute<JsonUnmappedMemberHandlingAttribute>()?.UnmappedMemberHandling == JsonUnmappedMemberHandling.Disallow,
        properties,
    });
}
File.WriteAllText(args[0], JsonSerializer.Serialize(result, ExportJson.Options) + "\n");

internal static class ExportJson
{
    internal static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
}
