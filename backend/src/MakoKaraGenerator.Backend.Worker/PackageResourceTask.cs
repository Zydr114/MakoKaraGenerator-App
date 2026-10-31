using System.Security.Cryptography;
using System.Text;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Rendering;
using MakoKaraGenerator.Media;
using MakoKaraGenerator.Rendering.Abstractions;
using MakoKaraGenerator.Rendering.Skia;
using SkiaSharp;
using SkiaSharp.HarfBuzz;

namespace MakoKaraGenerator.Backend.Worker;

internal static class PackageResourceTask
{
    public static PackageResourceWorkerCapabilities Capabilities()
    {
        return new(StaticProjectSnapshotCodec.EngineIdentity, PackageResourceWorkerContract.Profile,
            [PackageResourceWorkerContract.ImageDecodeProfile],
            [PackageResourceWorkerContract.VideoMetadataProfile, PackageResourceWorkerContract.VideoFrameProfile],
            [PackageResourceWorkerContract.FontShapeProfile], PackageResourceWorkerContract.FontProviderIdentity);
    }

    public static PackageResourceInspection Validate(PackageResourceWorkerGrant grant, CancellationToken token)
    {
        PackageResourceWorkerContract.ValidateGrant(grant);
        token.ThrowIfCancellationRequested();
        string full = Path.GetFullPath(grant.FilePath);
        if (!File.Exists(full) || Directory.Exists(full) || HasReparsePoint(full))
            throw new WorkerProtocolException("Worker资源授权文件不可用。", "WorkerResourceGrantUnavailable");
        FileInfo info = new(full);
        if (info.Length != grant.Size || info.Length is <= 0 or > PackageResourceWorkerContract.MaximumGrantBytes)
            throw new WorkerProtocolException("Worker资源大小与授权不一致。", "WorkerResourceGrantChanged");
        string sha;
        using (FileStream input = new(full, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan))
            sha = Convert.ToHexStringLower(SHA256.HashData(input));
        if (sha != grant.Sha256) throw new WorkerProtocolException("Worker资源内容与授权不一致。", "WorkerResourceGrantChanged");

        return grant.Role switch
        {
            "image" => Image(grant, full, token),
            "video" => Video(grant, full, token),
            "font" => Font(grant, full, token),
            _ => throw new WorkerProtocolException("Worker资源 role 未接入。", "WorkerResourceRoleUnsupported"),
        };
    }

    private static PackageResourceInspection Image(PackageResourceWorkerGrant grant, string path, CancellationToken token)
    {
        byte[] bytes = File.ReadAllBytes(path);
        PackageDecodedImageInfo info = new PackageImageValidator().Inspect(bytes, grant.PackagePath, token);
        return new(grant.GrantId, grant.Role, grant.Size, grant.Sha256,
            new(info.Width, info.Height, info.EncodedFormat, info.DecodedBytes, info.HasAlpha));
    }

    private static PackageResourceInspection Video(PackageResourceWorkerGrant grant, string path, CancellationToken token)
    {
        SelfContainedVideoInspection media;
        try { media = SelfContainedVideoInput.Inspect(path, token); }
        catch (OperationCanceledException) { throw; }
        catch (WorkerResourceMediaException error)
        { throw new WorkerProtocolException(error.Message, error.Code); }
        catch (IOException)
        { throw new WorkerProtocolException("视频资源无法完成受限容器检查或首帧解码。", "WorkerResourceVideoInvalid"); }

        if (media.Width <= 0 || media.Height <= 0 || media.DurationUs <= 0
            || media.DurationUs > PackageResourceWorkerContract.MaximumVideoDurationUs)
            throw new WorkerProtocolException("视频资源没有可用的有限元数据。", "WorkerResourceVideoInvalid");
        long frameBytes = checked((long)media.Width * media.Height * 4);
        if (frameBytes > PackageResourceWorkerContract.MaximumVideoFrameBytes)
            throw new WorkerProtocolException("视频首帧超过固定解码预算。", "WorkerResourceBudgetExceeded");
        int decodedBytes = checked(media.FramePixels.Length);
        string frameSha = Convert.ToHexStringLower(SHA256.HashData(media.FramePixels));
        return new(grant.GrantId, grant.Role, grant.Size, grant.Sha256,
            Video: new(media.DurationUs, media.Width, media.Height, media.Codec, media.HasAudio,
                decodedBytes, frameSha, media.FrameTimeUs));
    }

    private static PackageResourceInspection Font(PackageResourceWorkerGrant grant, string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        byte[] bytes = File.ReadAllBytes(path);
        try
        {
            using SKData data = SKData.CreateCopy(bytes.AsSpan());
            using SKTypeface? typeface = SKTypeface.FromData(data);
            if (typeface is null) throw new InvalidDataException("字体文件无法建立 Skia 字体对象。");
            using SKFont font = new(typeface, 32f);
            using SKShaper shaper = new(typeface);
            SKShaper.Result shaped = shaper.Shape("MakoKara", font);
            int glyphCount = shaped.Codepoints.Length;
            if (glyphCount <= 0 || !float.IsFinite(shaped.Width) || shaped.Width <= 0)
                throw new InvalidDataException("字体文件无法完成固定文本整形。");
            token.ThrowIfCancellationRequested();
            string shapingInput = string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"{string.Join(',', shaped.Codepoints)}|{string.Join(',', shaped.Clusters)}|{shaped.Width:R}");
            string shapingFingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(shapingInput)));
            return new(grant.GrantId, grant.Role, grant.Size, grant.Sha256,
                Font: new([typeface.FamilyName], glyphCount, PackageResourceWorkerContract.FontShapeProfile, shaped.Width,
                    shapingFingerprint));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is InvalidDataException or ArgumentException or InvalidOperationException)
        { throw new WorkerProtocolException("字体资源无法建立受控 provider 或完成整形。", "WorkerResourceFontInvalid"); }
    }

    private static bool HasReparsePoint(string path)
    {
        FileInfo file = new(path);
        if (file.Attributes.HasFlag(FileAttributes.ReparsePoint)) return true;
        DirectoryInfo? current = file.Directory;
        while (current is not null)
        {
            if (current.Exists && current.Attributes.HasFlag(FileAttributes.ReparsePoint)) return true;
            current = current.Parent;
        }
        return false;
    }

}
