using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Rendering;

namespace MakoKaraGenerator.Backend.Hosting;

/// <summary>只传递Host已固定的文件能力，HTTP不接受这些路径。</summary>
public sealed record BackendExportAudioGrant(string Path, long Size, string Sha256,
    string AudioProfile = BackendAudioInputProfiles.Pcm16Wave);
/// <summary>一个Host拥有的独有候选输出；不能作为已公开提交的文件。</summary>
public sealed record BackendExportExecutionInput(string Format, bool IncludeAudio, string CandidatePath, BackendExportAudioGrant? Audio, string Quality = "standard", WorkerExportResourcePolicy? ResourcePolicy = null,
    ReadOnlyMemory<byte> RuntimeSource = default);
/// <summary>来自真实独立Worker及固定工具的部署能力，不由HTTP指定。</summary>
public sealed record BackendExportCapabilities(bool GuardAvailable, bool OpaqueMp4, bool TransparentProRes4444, string? FailureCode = null,
    string[]? AudioProfiles = null, string[]? ExecutionProfiles = null, string[]? SourceProfiles = null,
    bool ImageExportAvailable = false, string? ImageExportFailureCode = null, string[]? ImageBaseProfiles = null);
/// <summary>独立Worker导出的中立接口；返回前所有运行资源及进程均已排水。</summary>
public interface IBackendExportExecutor
{
    Task<BackendExportCapabilities> ProbeAsync(CancellationToken cancellationToken = default);
    void ValidateInput(ProjectSnapshot snapshot, BackendExportExecutionInput input);
    Task<ExportReportDto> ExecuteAsync(ProjectSnapshot snapshot, BackendExportExecutionInput input,
        IProgress<BackendJobProgressDto>? progress = null, CancellationToken cancellationToken = default);
}

/// <summary>已保存工程图片导出的独立 Worker 能力；不把图片输入混入静态导出执行器。</summary>
public interface IBackendImageExportExecutor
{
    bool ImageExportAvailable { get; }
    void ValidateImageInput(ProjectSnapshot snapshot, ReadOnlyMemory<byte> imageBytes, BackendExportExecutionInput input);
    Task<ExportReportDto> ExecuteImageAsync(ProjectSnapshot snapshot, ReadOnlyMemory<byte> imageBytes,
        BackendExportExecutionInput input, IProgress<BackendJobProgressDto>? progress = null,
        CancellationToken cancellationToken = default);
}
