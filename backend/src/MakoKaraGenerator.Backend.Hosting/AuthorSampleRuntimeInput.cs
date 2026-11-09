using System.Security.Cryptography;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Rendering;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Runtime.Hosting.Authoring;

namespace MakoKaraGenerator.Backend.Hosting;

internal static class AuthorSampleRuntimeInput
{
    /// <summary>按试样场景固定作者输入；媒体依赖与工程输入同样经宿主分析，透明输出固定为透明无媒体输入。</summary>
    /// <param name="request">作者试样选区。</param>
    /// <param name="mediaFeatures">宿主背景分析服务；未安装时媒体查询得到无背景贡献输入。</param>
    /// <param name="transparentOutput">消费者以透明背景输出。</param>
    /// <param name="waitForCompletion">正式验证等最终消费者不限时等待分析完成；否则按试样上限等待。</param>
    /// <param name="token">取消准备。</param>
    internal static async Task<BackendPreparedProjectRuntimeInput> PrepareAsync(ScriptSampleRequest request,
        BackendMediaFeatureService? mediaFeatures, bool transparentOutput, bool waitForCompletion, CancellationToken token)
    {
        await using TemplateSampleLease media = new(Path.Combine(Path.GetTempPath(), "makokara-author-samples"));
        TemplateSampleResources resources = await media.LoadAsync(token).ConfigureAwait(false);
        ProjectSnapshot snapshot = await AddMediaIdentityAsync(ScriptSampleBuilder.Build(request, resources), resources, token).ConfigureAwait(false);
        snapshot = ProjectSnapshot.FromProject(ProjectSnapshotSourceProject.Restore(snapshot));
        using BackendProjectRuntimeInputLease lease = BackendProjectRuntimeInputLease.CreateOwnedTemplateSample(snapshot, media.DirectoryPath);
        lease.MediaFeatures = mediaFeatures;
        lease.TransparentOutput = transparentOutput;
        lease.WaitForMediaFeatures = waitForCompletion;
        // 作者草稿预览开启图／材质逐像素检查；正式验证任务在 Worker 内始终检查。
        lease.GraphPixelChecks = true;
        BackendOperationResult<BackendPreparedProjectRuntimeInput> prepared = await lease.PrepareAsync(token).ConfigureAwait(false);
        if (!prepared.Accepted) throw new AuthorSamplePreparationException(prepared.Error!);
        // Prepared input owns the copied media closure; the generated source directory can now be removed.
        return prepared.Response!;
    }

    private static async Task<ProjectSnapshot> AddMediaIdentityAsync(ProjectSnapshot snapshot,
        TemplateSampleResources resources, CancellationToken token)
    {
        static async Task<(string Hash, long Size)> Identity(string path, CancellationToken cancellationToken)
        {
            await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return (Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)), stream.Length);
        }
        var audio = await Identity(resources.AudioPath, token).ConfigureAwait(false);
        var background = await Identity(resources.BackgroundPath, token).ConfigureAwait(false);
        return snapshot with
        {
            AudioMedia = new(audio.Hash, audio.Size, Path.GetFileName(resources.AudioPath)),
            Background = snapshot.Background with { Media = new(background.Hash, background.Size, Path.GetFileName(resources.BackgroundPath)) },
        };
    }
}

internal sealed class AuthorSamplePreparationException(BackendErrorResponse error) : Exception(error.Message)
{
    internal BackendErrorResponse Error { get; } = error;
}
