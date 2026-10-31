using MakoKaraGenerator.Media;

namespace MakoKaraGenerator.Backend.Hosting;

/// <summary>Host已授权且固定完整字节身份的准备输入，不授予描述路径访问能力。</summary>
public sealed record BackendAudioPreparationGrant(string Path, long Size, string Sha256);

/// <summary>Worker真实握手和native工具启动验证结果。</summary>
public sealed record BackendAudioPreparationCapabilities(bool GuardAvailable, bool ToolStartupVerified,
    string? FailureCode = null);

/// <summary>独立完成完整波形准备，在返回或抛出前排水全部拥有进程。</summary>
public interface IBackendAudioPreparationExecutor
{
    /// <summary>在执行前核固定授权与私有输入预算。</summary>
    void ValidateInput(BackendAudioPreparationGrant grant);
    /// <summary>准备原源采样率与完整波形；无音频流时返回null。</summary>
    Task<LoadedWaveform?> ExecuteAsync(BackendAudioPreparationGrant grant, CancellationToken cancellationToken = default);
}
