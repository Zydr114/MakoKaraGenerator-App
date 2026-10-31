using System.Text.Json;
using MakoKaraGenerator.Backend.Contracts;

namespace MakoKaraGenerator.Backend.Hosting;

/// <summary>作者异步编译完整回执；文件布局和授权沿用任务 sidecar。</summary>
internal sealed class BackendAuthorCompilationResultStore
{
    private const int MaxBytes = 48 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly BackendJobSidecarStore _files;

    internal BackendAuthorCompilationResultStore(string stateRoot, long quota)
    {
        _files = new(Path.Combine(stateRoot, "author-compilation-results"), "compile-", "AuthorCompilationResult", MaxBytes, quota);
    }

    internal BackendJobReportReferenceDto Publish(string jobId, AuthorCompilationResponse response)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(response, Json);
        if (bytes.Length > MaxBytes) throw new JobSidecarException("AuthorCompilationResultBudgetExceeded");
        return _files.Publish(jobId, bytes);
    }

    internal AuthorCompilationResponse Read(string jobId, BackendJobReportReferenceDto reference)
    {
        try
        {
            byte[] bytes = _files.Read(jobId, reference);
            return JsonSerializer.Deserialize<AuthorCompilationResponse>(bytes, Json)
                ?? throw new IOException("作者编译回执无效。");
        }
        catch (JsonException) { throw new IOException("作者编译回执无效。"); }
    }

    internal void Prune(IEnumerable<string> retainedReportIds) => _files.Prune(retainedReportIds);
}
