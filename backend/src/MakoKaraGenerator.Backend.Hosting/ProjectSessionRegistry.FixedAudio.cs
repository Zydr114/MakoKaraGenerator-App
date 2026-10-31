using System.Security.Cryptography;
using MakoKaraGenerator.Backend.Contracts;

namespace MakoKaraGenerator.Backend.Hosting;

public sealed partial class ProjectSessionRegistry
{
    // Resource references protect retention. These before/after checks do not constitute
    // an OS snapshot against arbitrary local concurrent replacement or ABA writes.
    private void VerifyFixedAudioSource(string clientId, StoredAsset asset, CancellationToken cancellationToken = default)
    {
        try
        {
            ResourceReadLease? read = null; Exception? primary = null;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                StringComparison paths = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                if (!Path.IsPathFullyQualified(asset.StoragePath) || !string.Equals(Path.GetFullPath(asset.StoragePath), asset.StoragePath, paths))
                    throw new IOException("固定音频路径不是规范绝对位置。");
                ProjectDirectoryHandles.EnsurePathHasNoLinks(asset.StoragePath);
                read = _resources.AcquireRead(clientId, asset.AssetId);
                if (read.Asset.StoragePath != asset.StoragePath || read.Asset.Size != asset.Size || read.Asset.Sha256 != asset.Sha256)
                    throw new IOException("固定音频资源声明已经改变。");
                VerifyFixedAudioBytes(read.Stream, asset, cancellationToken);
            }
            catch (Exception error) { primary = error; throw; }
            finally
            {
                try { read?.Dispose(); }
                catch (Exception cleanup) when (primary is not null) { primary.Data["FixedAudioReadCleanupFailure"] = cleanup; }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            var failure = new ResourceStoreException(BackendErrorCode.TaskExecutionFailed,
                "固定音频资源不可用、损坏或无法完成读取释放。", "audio");
            failure.Data["FixedAudioVerificationFailure"] = error;
            throw failure;
        }
    }

    private static void VerifyFixedAudioBytes(Stream stream, StoredAsset asset, CancellationToken cancellationToken)
    {
        ProjectDirectoryHandles.EnsurePathHasNoLinks(asset.StoragePath);
        if (asset.Size < 0 || stream.Length != asset.Size) throw new IOException("当前授权音频文件大小改变。");
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[64 * 1024]; long remaining = asset.Size;
        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (count == 0) throw new IOException("当前授权音频文件提前结束。");
            hash.AppendData(buffer, 0, count); remaining -= count;
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (stream.ReadByte() != -1 || stream.Length != asset.Size || Convert.ToHexStringLower(hash.GetHashAndReset()) != asset.Sha256)
            throw new IOException("当前授权音频完整字节不匹配。");
        ProjectDirectoryHandles.EnsurePathHasNoLinks(asset.StoragePath);
    }
}
