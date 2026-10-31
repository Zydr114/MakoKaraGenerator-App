using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Hosting;

namespace MakoKaraGenerator.Backend.Server;

internal static partial class ServerEndpoints
{
    private static void ConfigureScriptConfigurationEndpoints(WebApplication app)
    {
        app.MapGet("/api/v1/script-configurations", (HttpRequest request, ProjectSessionRegistry registry) =>
            Result(registry.ReadScriptConfigurations(request.Query.TryGetValue("expectedRevision", out var value) ? value.ToString() : null)));
        app.MapGet("/api/v1/script-configurations/{id}", (string id, HttpRequest request, ProjectSessionRegistry registry) =>
            !request.Query.TryGetValue("expectedRevision", out var value) || string.IsNullOrWhiteSpace(value)
                ? Error(new BackendErrorResponse(BackendErrorCode.InvalidRequest, "必须提供expectedRevision查询参数。", "expectedRevision"))
                : Result(registry.ReadScriptConfiguration(id, value.ToString())));
        app.MapPost("/api/v1/script-configurations/commands", async (HttpRequest request, ProjectSessionRegistry registry, AcceptedOperationTracker tracker) =>
        {
            var parsed = await ServerEndpoints.ReadJsonAsync<ScriptConfigurationCommandRequest>(request);
            if (parsed.Error is not null) return parsed.Error;
            var result = await tracker.RunAsync(() => registry.ExecuteScriptConfigurationCommandAsync(Client(request), parsed.Input!, CancellationToken.None));
            return Result(result);
        });
    }
}
