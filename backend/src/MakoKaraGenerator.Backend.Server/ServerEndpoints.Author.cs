using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Hosting;

namespace MakoKaraGenerator.Backend.Server;

internal static partial class ServerEndpoints
{
    private static void ConfigureAuthorEndpoints(WebApplication app)
    {
        app.MapPost("/api/v1/drafts", CreateAuthorDraftAsync);
        app.MapGet("/api/v1/drafts", ReadAuthorDrafts);
        app.MapGet("/api/v1/drafts/{id}", ReadAuthorDraft);
        app.MapGet("/api/v1/drafts/{id}/sources", ReadAuthorSources);
        app.MapPut("/api/v1/drafts/{id}/configuration", SaveAuthorConfigurationAsync);
        app.MapGet("/api/v1/drafts/{id}/compilation", ReadAuthorCompilation);
        app.MapPost("/api/v1/drafts/{id}/source-revision-jobs", SaveAuthorSourceRevisionAsync);
        app.MapPost("/api/v1/drafts/{id}/compilation-jobs", CompileAuthorDraftAsync);
        app.MapGet("/api/v1/jobs/{id}/compilation-result", ReadAuthorCompilationResult);
        app.MapPost("/api/v1/drafts/{id}/validation-jobs", StartAuthorValidationAsync);
        app.MapPost("/api/v1/drafts/{id}/adopt-source-revision", AdoptAuthorSourceRevisionAsync);
        app.MapDelete("/api/v1/drafts/{id}", DeleteAuthorDraftAsync);
    }

    private static async Task<IResult> CreateAuthorDraftAsync(HttpRequest request,
        ProjectSessionRegistry registry, AcceptedOperationTracker tracker)
    {
        (AuthorDraftCreateRequest? input, IResult? error) = await ReadJsonAsync<AuthorDraftCreateRequest>(request);
        if (error is not null) return error;
        return Result(await tracker.RunAsync(() => registry.CreateAuthorDraftAsync(Client(request), input!, CancellationToken.None)), 201);
    }

    private static IResult ReadAuthorDraft(string id, HttpRequest request, ProjectSessionRegistry registry) =>
        Result(registry.ReadAuthorDraft(Client(request), id));

    private static IResult ReadAuthorDrafts(HttpRequest request, ProjectSessionRegistry registry) =>
        Result(registry.ReadAuthorDrafts(Client(request)));

    private static IResult ReadAuthorSources(string id, HttpRequest request, ProjectSessionRegistry registry)
    {
        if (!request.Query.TryGetValue("expectedDraftRevision", out var expected) || string.IsNullOrWhiteSpace(expected))
            return Error(new(BackendErrorCode.InvalidRequest, "必须提供expectedDraftRevision查询参数。", "expectedDraftRevision"));
        if (!request.Query.TryGetValue("sourceRevision", out var source) || string.IsNullOrWhiteSpace(source))
            return Error(new(BackendErrorCode.InvalidRequest, "必须提供sourceRevision查询参数。", "sourceRevision"));
        return Result(registry.ReadAuthorSources(Client(request), id, expected.ToString(), source.ToString()));
    }

    private static async Task<IResult> SaveAuthorConfigurationAsync(string id, HttpRequest request,
        ProjectSessionRegistry registry, AcceptedOperationTracker tracker)
    {
        (AuthorDraftConfigurationRequest? input, IResult? error) = await ReadJsonAsync<AuthorDraftConfigurationRequest>(request);
        if (error is not null) return error;
        return Result(await tracker.RunAsync(() => registry.SaveAuthorConfigurationAsync(Client(request), id, input!, CancellationToken.None)));
    }

    private static IResult ReadAuthorCompilation(string id, HttpRequest request, ProjectSessionRegistry registry) =>
        Result(registry.ReadAuthorCompilation(Client(request), id));

    private static async Task<IResult> StartAuthorValidationAsync(string id, HttpRequest request,
        ProjectSessionRegistry registry, AcceptedOperationTracker tracker)
    {
        (AuthorValidationJobRequest? input, IResult? error) = await ReadJsonAsync<AuthorValidationJobRequest>(request);
        if (error is not null) return error;
        return Result(await tracker.RunAsync(() => registry.StartAuthorValidationAsync(Client(request), id, input!, CancellationToken.None)), 202);
    }

    private static IResult ReadAuthorCompilationResult(string id, HttpRequest request, ProjectSessionRegistry registry) =>
        Result(registry.ReadAuthorCompilationResult(Client(request), id));

    private static async Task<IResult> SaveAuthorSourceRevisionAsync(string id, HttpRequest request,
        ProjectSessionRegistry registry, AcceptedOperationTracker tracker)
    {
        (AuthorSourceRevisionRequest? input, IResult? error) = await ReadJsonAsync<AuthorSourceRevisionRequest>(request);
        if (error is not null) return error;
        return Result(await tracker.RunAsync(() => registry.SaveAuthorSourceRevisionAsync(Client(request), id, input!, CancellationToken.None)));
    }

    private static async Task<IResult> CompileAuthorDraftAsync(string id, HttpRequest request,
        ProjectSessionRegistry registry, AcceptedOperationTracker tracker)
    {
        (AuthorCompilationRequest? input, IResult? error) = await ReadJsonAsync<AuthorCompilationRequest>(request);
        if (error is not null) return error;
        if (input!.ExecutionMode is not null && input.ExecutionMode is not ("sync" or "async"))
            return Error(new(BackendErrorCode.InvalidRequest, "executionMode只能是sync或async。", "executionMode"));
        if (string.Equals(input.ExecutionMode, "async", StringComparison.Ordinal))
            return Result(await tracker.RunAsync(() => registry.StartAuthorCompilationAsync(Client(request), id, input, CancellationToken.None)), 202);
        return Result(await tracker.RunAsync(() => registry.CompileAuthorDraftAsync(Client(request), id, input, CancellationToken.None)));
    }

    private static async Task<IResult> AdoptAuthorSourceRevisionAsync(string id, HttpRequest request,
        ProjectSessionRegistry registry, AcceptedOperationTracker tracker)
    {
        (AuthorAdoptSourceRevisionRequest? input, IResult? error) = await ReadJsonAsync<AuthorAdoptSourceRevisionRequest>(request);
        if (error is not null) return error;
        return Result(await tracker.RunAsync(() => registry.AdoptAuthorSourceRevisionAsync(Client(request), id, input!, CancellationToken.None)));
    }

    private static async Task<IResult> DeleteAuthorDraftAsync(string id, HttpRequest request,
        ProjectSessionRegistry registry, AcceptedOperationTracker tracker)
    {
        (AuthorDraftDeleteRequest? input, IResult? error) = await ReadJsonAsync<AuthorDraftDeleteRequest>(request);
        if (error is not null) return error;
        return Result(await tracker.RunAsync(() => registry.DeleteAuthorDraftAsync(Client(request), id, input!, CancellationToken.None)));
    }
}
