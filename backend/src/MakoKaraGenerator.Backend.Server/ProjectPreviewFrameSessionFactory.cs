using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Hosting;
using MakoKaraGenerator.Backend.Rendering;
using MakoKaraGenerator.Rendering;

namespace MakoKaraGenerator.Backend.Server;

/// <summary>只用于公共预览的工厂组合；B4及测量继续直接使用原静态工厂。</summary>
internal sealed class ProjectPreviewFrameSessionFactory(IRenderFrameSessionFactory staticFactory,
    IRenderFrameSessionFactory packageFactory) : IRenderFrameSessionFactory, IBackendPreviewProfileCapability,
    IBackendRuntimePreviewFactory, IBackendPreviewRenderBackendFactory
{
    public string RenderBackend => staticFactory is IBackendPreviewRenderBackendFactory selected ? selected.RenderBackend : "cpu";

    public IRenderFrameSessionFactory WithRenderBackend(string renderBackend) => new ProjectPreviewFrameSessionFactory(
        Select(staticFactory, renderBackend), Select(packageFactory, renderBackend));

    private static IRenderFrameSessionFactory Select(IRenderFrameSessionFactory factory, string renderBackend) =>
        factory is IBackendPreviewRenderBackendFactory selectable ? selectable.WithRenderBackend(renderBackend)
        : renderBackend == "cpu" ? factory
        : throw new SnapshotContractException("WorkerRenderBackendUnavailable", "当前预览工厂未提供显式设备选择。");
    public ValueTask<IRenderFrameSession> CreateAsync(ProjectSnapshot snapshot, CancellationToken token = default) =>
        (ProjectSnapshotSourceProfile.IsPackage(ProjectPreviewSnapshotProfile.Select(snapshot)) ? packageFactory : staticFactory)
            .CreateAsync(snapshot, token);

    public ValueTask<BackendErrorResponse?> EnsureProfileAsync(string profile, CancellationToken token = default)
    {
        IRenderFrameSessionFactory? factory = ProjectSnapshotSourceProfile.IsPackage(profile) || profile == ProjectRuntimeInputWorkerContract.Profile ? packageFactory
            : BackendSnapshotProfiles.IsSupported(profile) ? staticFactory : null;
        return factory is IBackendPreviewProfileCapability capability ? capability.EnsureProfileAsync(profile, token)
            : ValueTask.FromResult<BackendErrorResponse?>(new(BackendErrorCode.PreviewUnavailable,
                "当前预览工厂未提供所选来源能力握手。", "worker", FailureCode: profile == PackageProjectSnapshotCodec.Profile
                    ? "WorkerPackageProfileMismatch" : "WorkerStaticProfileMismatch"));
    }

    public IRenderFrameSessionFactory Bind(BackendPreparedProjectRuntimeInput input) =>
        packageFactory is IBackendRuntimePreviewFactory runtimeFactory ? runtimeFactory.Bind(input)
            : throw new SnapshotContractException("WorkerRuntimeInputProfileMismatch", "当前预览工厂未提供固定工程输入能力。");
}
