using System.Security.Cryptography;
using MakoKaraGenerator.Backend.Rendering;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Media;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Media;
using MakoKaraGenerator.Rendering;
using SkiaSharp;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

/// <summary>真实受控解码：静态图片背景经宿主分析成为可传输的 AvailableFile 输入。</summary>
public sealed class BackendMediaFeatureServiceRealToolTests
{
    [Fact]
    public async Task 静态图片经真实工具生成制品并以私有授权完整往返()
    {
        string tools = Path.Combine(AppContext.BaseDirectory, "media-tools");
        if (!MediaPresentationDeploymentDiscovery.IsInstalled(tools)) return; // 非 Linux 部署没有受控解码。
        string root = Directory.CreateTempSubdirectory("mkg-media-service-").FullName;
        try
        {
            string image = Path.Combine(root, "background.png");
            using (var bitmap = new SKBitmap(64, 48, SKColorType.Rgba8888, SKAlphaType.Unpremul))
            {
                for (int y = 0; y < bitmap.Height; y++)
                    for (int x = 0; x < bitmap.Width; x++)
                        bitmap.SetPixel(x, y, new SKColor((byte)(x * 4), (byte)(y * 5), 160));
                using SKData data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
                await File.WriteAllBytesAsync(image, data.ToArray());
            }
            byte[] bytes = await File.ReadAllBytesAsync(image);
            var content = new MediaFileContent(new(Convert.ToHexStringLower(SHA256.HashData(bytes))), bytes.LongLength);
            await using BackendMediaFeatureService service = BackendMediaFeatureService.TryCreate(Path.Combine(root, "state"), tools)!;
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            BackendMediaFeatureResolution resolution = await service.ResolveAsync(image, content, MediaSourceKind.Still,
                waitForCompletion: true, timeout.Token);
            Assert.Null(resolution.Failure);
            MediaFeatureFileSource source = Assert.IsType<MediaFeatureFileSource>(resolution.Source);
            BackendMediaFeatureArtifactReference artifact = Assert.IsType<BackendMediaFeatureArtifactReference>(resolution.Artifact);
            string path = Assert.IsType<string>(resolution.ArtifactPath);
            Assert.True(File.Exists(path));
            Assert.True(service.Generation >= 2);

            ProjectSnapshot basis = ProjectSnapshot.FromProject(Project.Create("media",
                Composition.Create(64, 48, FrameRate.Fps30, outputDurationUs: 1_000_000), LyricsDocument.Create([])));
            MediaDigest digest = new(new string('d', 64));
            var context = MediaInputContext.Create(source.Profile, MediaMapping.Create(64, 48, BackgroundFit.Cover,
                BackgroundPlaybackMode.Loop, new LinearPremulRgba(0, 0, 0, 1), MediaOutputMode.Normal), digest, digest);
            var input = MediaFeatureInput.CreateAvailableFile(context, source, artifact.Identity);
            ProjectSnapshot snapshot = basis with { MediaFeatureInput = input };
            byte[] runtime = ProjectRuntimeInputCodec.Encode(snapshot, null, null, path);
            Assert.Equal(input.InputRevision, ProjectRuntimeInputCodec.Decode(runtime).Snapshot.MediaFeatureInput!.InputRevision);
            Assert.Equal(path, ProjectRuntimeInputCodec.ReadMediaGrant(runtime)!.ArtifactPath);
            ProjectRuntimeInputCodec.ValidateSnapshot(runtime, basis);

            var codec = new MediaFeatureTrackCodec(new MediaMomentKernel(), new MediaAbValueValidator());
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.Asynchronous | FileOptions.RandomAccess);
            await using IMediaFeatureTrackReader reader = await codec.OpenValidatedAsync(stream, source, artifact.Identity,
                new MediaWorkingSetBudget(MediaAbCapacityV1.MaximumRuntimeCpuCacheBytes), timeout.Token);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
