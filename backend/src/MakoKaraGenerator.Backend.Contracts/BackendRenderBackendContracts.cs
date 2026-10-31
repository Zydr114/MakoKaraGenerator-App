namespace MakoKaraGenerator.Backend.Contracts;

/// <summary>隔离 Worker 当前实际设备检查的部署快照，不代表后续任务已经产帧。</summary>
public sealed record RenderBackendsResponse(string DefaultBackend, RenderBackendAvailabilityDto[] Backends);

/// <summary>一个可显式选择的渲染后端；GPU 本批使用 Vulkan。</summary>
public sealed record RenderBackendAvailabilityDto(string RenderBackend, bool Available, string Api,
    RenderBackendDeviceInfoDto? Device = null, BackendErrorResponse? UnavailableReason = null);

/// <summary>允许公开的有界设备摘要，不包含路径、进程身份或原始异常。</summary>
public sealed record RenderBackendDeviceInfoDto(string? Vendor, string? Device, string? Driver, string? ApiVersion);
