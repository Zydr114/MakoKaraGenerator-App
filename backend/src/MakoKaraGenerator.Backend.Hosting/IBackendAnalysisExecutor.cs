using MakoKaraGenerator.Backend.Contracts;

namespace MakoKaraGenerator.Backend.Hosting;

/// <summary>仅由宿主创建的授权音频输入；不接受HTTP路径。</summary>
public sealed record BackendAudioAnalysisGrant(string Path, long Size, string Sha256,
    string AudioProfile = BackendAudioInputProfiles.Pcm16Wave);
public sealed record BackendAnalysisCapabilities(bool GuardAvailable, bool ToolStartupVerified, string? FailureCode = null,
    string[]? AudioProfiles = null, string[]? SourceProfiles = null);

/// <summary>返回原完整规范分析文件；返回或抛出之前必须排水全部私有进程。</summary>
public interface IBackendAnalysisExecutor
{
    void ValidateInput(BackendAudioAnalysisGrant grant);
    Task<BackendAnalysisCapabilities> ProbeAsync(CancellationToken cancellationToken = default);
    Task<byte[]> ExecuteAsync(BackendAudioAnalysisGrant grant, IProgress<BackendAnalysisProgressDto>? progress = null,
        CancellationToken cancellationToken = default);
}
