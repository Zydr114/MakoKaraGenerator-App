using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Rendering;

namespace MakoKaraGenerator.Backend.Hosting;

public sealed partial class ProjectSessionRegistry
{
    private static BackendErrorResponse? ScriptBackendError(ProjectSnapshot snapshot, string? backend)
        => backend != "gpu" && ScriptBackendRequirement.RequiresVulkan(snapshot)
            ? new(BackendErrorCode.CommandNotSupported, "SDK v2 效果要求 GPU 渲染后端。", "renderBackend",
                FailureCode: "PackageBackendUnsupported") : null;
}
