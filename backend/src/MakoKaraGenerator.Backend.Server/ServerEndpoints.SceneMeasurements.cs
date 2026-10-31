using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Hosting;

namespace MakoKaraGenerator.Backend.Server;

internal static partial class ServerEndpoints
{
    private static void ConfigureSceneMeasurementEndpoints(WebApplication app) =>
        app.MapPost("/api/v1/project-sessions/{id}/orchestration/measurements", MeasureSceneAsync);

    private static async Task<IResult> MeasureSceneAsync(string id, HttpRequest request, PreviewBackendService service,
        AcceptedOperationTracker tracker)
    {
        if (request.Query.Count != 0) return Error(new(BackendErrorCode.InvalidRequest, "测量不接受query字段。"));
        var (input, error) = await ReadJsonAsync<SceneMeasurementRequest>(request);
        if (error is not null) return error;
        return Result(await tracker.RunAsync(() => service.MeasureSceneAsync(Client(request), id, input!, request.HttpContext.RequestAborted)));
    }
}
