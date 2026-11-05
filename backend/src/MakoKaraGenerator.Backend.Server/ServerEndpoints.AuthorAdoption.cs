using System.Text.Json;
using Microsoft.Extensions.Options;
using MakoKaraGenerator.Backend.Contracts;

namespace MakoKaraGenerator.Backend.Server;

internal static partial class ServerEndpoints
{
    // Only the new non-null configuration requires an explicit complete snapshot.
    // Deserialize historical requests with the same HTTP JSON options as before.
    private static async Task<(AuthorAdoptSourceRevisionRequest? Input, IResult? Error)> ReadAuthorAdoptionRequestAsync(HttpRequest request)
    {
        if (!request.HasJsonContentType())
            return (null, Error(new(BackendErrorCode.UnsupportedMediaType, "请求必须使用JSON媒体类型。", "contentType"), 415));
        try
        {
            // Preserve the existing HTTP JSON reader's charset transcoding and reader options.
            using JsonDocument? json = await request.ReadFromJsonAsync<JsonDocument>(cancellationToken: request.HttpContext.RequestAborted);
            if (json is null) return (null, Error(new(BackendErrorCode.InvalidRequest, "请求体不能为空。")));
            JsonSerializerOptions options = request.HttpContext.RequestServices
                .GetRequiredService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>().Value.SerializerOptions;
            if (json.RootElement.ValueKind == JsonValueKind.Object)
            {
                // Match the existing HTTP serializer's property-name policy, including case-insensitive compatibility.
                JsonElement? configuration = null;
                foreach (JsonProperty property in json.RootElement.EnumerateObject())
                    if (string.Equals(property.Name, "configuration", options.PropertyNameCaseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    {
                        if (configuration is not null)
                            return (null, Error(new(BackendErrorCode.InvalidRequest, "配置字段不得重复。", "configuration")));
                        configuration = property.Value;
                    }
                if (configuration is JsonElement input && input.ValueKind != JsonValueKind.Null)
                {
                    if (input.ValueKind != JsonValueKind.Object)
                        return (null, Error(new(BackendErrorCode.InvalidRequest, "配置必须为完整对象或null。", "configuration")));
                    StringComparer comparer = options.PropertyNameCaseInsensitive ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
                    HashSet<string> names = new(comparer);
                    foreach (JsonProperty property in input.EnumerateObject())
                        if (!names.Add(property.Name))
                            return (null, Error(new(BackendErrorCode.InvalidRequest, "配置字段不得重复。", "configuration." + property.Name)));
                    foreach (string field in new[] { "name", "exportId", "parametersJson", "relativeTarget", "readScope", "branchInput", "fixedSeed", "overlayLayer" })
                        if (!names.Contains(field))
                            return (null, Error(new(BackendErrorCode.InvalidRequest, "必须提供完整目标配置字段。", "configuration." + field)));
                }
            }
            AuthorAdoptSourceRevisionRequest? requestInput = json.RootElement.Deserialize<AuthorAdoptSourceRevisionRequest>(options);
            return requestInput is null ? (null, Error(new(BackendErrorCode.InvalidRequest, "请求体不能为空。"))) : (requestInput, null);
        }
        catch (JsonException) { return (null, Error(new(BackendErrorCode.InvalidRequest, "请求JSON无效。"))); }
    }
}
