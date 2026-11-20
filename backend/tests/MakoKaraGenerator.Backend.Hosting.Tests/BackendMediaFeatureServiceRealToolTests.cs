using System.Buffers.Binary;
using System.IO.Compression;
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
        if (!MediaPresentationDeploymentDiscovery.IsInstalled(tools)) return;
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
                completionWait: Timeout.InfiniteTimeSpan, timeout.Token);
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

    [Fact]
    public async Task WindowsNativeMemoryDenialIsNotAnOrdinaryCorruptImage()
    {
        string tools = Path.Combine(AppContext.BaseDirectory, "media-tools");
        if (!OperatingSystem.IsWindows() || !MediaPresentationDeploymentDiscovery.IsInstalled(tools)) return;
        string root = Directory.CreateTempSubdirectory("mkg-media-service-").FullName;
        try
        {
            string unicode = Directory.CreateDirectory(Path.Combine(root, "媒体样例 space")).FullName;
            await using BackendMediaFeatureService service = BackendMediaFeatureService.TryCreate(Path.Combine(unicode, "state"), tools)!;
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            BackendMediaFeatureResolution damaged = await AnalyzeAsync("损坏.png", TestPng(32, corrupt: true));
            Assert.Null(damaged.Source);
            Assert.Null(damaged.Artifact);
            Assert.Null(damaged.Failure);

            BackendMediaFeatureResolution overLimit = await AnalyzeAsync("容量.png", TestPng(4096, corrupt: false));
            Assert.Null(overLimit.Source);
            Assert.Null(overLimit.Artifact);
            Assert.Equal("media-capacityexceeded", overLimit.Failure?.Code);

            async Task<BackendMediaFeatureResolution> AnalyzeAsync(string name, byte[] bytes)
            {
                string path = Path.Combine(unicode, name);
                await File.WriteAllBytesAsync(path, bytes, timeout.Token);
                var content = new MediaFileContent(new(Convert.ToHexStringLower(SHA256.HashData(bytes))), bytes.LongLength);
                return await service.ResolveAsync(path, content, MediaSourceKind.Still, Timeout.InfiniteTimeSpan, timeout.Token);
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static byte[] TestPng(int side, bool corrupt)
    {
        using var data = new MemoryStream();
        if (!corrupt)
        {
            using var compressed = new ZLibStream(data, CompressionLevel.Fastest, leaveOpen: true);
            byte[] row = new byte[side * 8 + 1];
            for (int index = 0; index < side; index++) compressed.Write(row);
        }
        else data.Write([1, 2, 3, 4]);

        using var png = new MemoryStream();
        png.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        byte[] header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, side);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), side);
        header[8] = 16;
        header[9] = 6;
        WriteChunk("IHDR"u8, header);
        WriteChunk("sRGB"u8, [0]);
        WriteChunk("IDAT"u8, data.ToArray());
        WriteChunk("IEND"u8, []);
        return png.ToArray();

        void WriteChunk(ReadOnlySpan<byte> kind, ReadOnlySpan<byte> bytes)
        {
            Span<byte> word = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(word, bytes.Length);
            png.Write(word);
            png.Write(kind);
            png.Write(bytes);
            uint crc = Crc(Crc(uint.MaxValue, kind), bytes) ^ uint.MaxValue;
            BinaryPrimitives.WriteUInt32BigEndian(word, crc);
            png.Write(word);
        }

        static uint Crc(uint crc, ReadOnlySpan<byte> bytes)
        {
            foreach (byte value in bytes)
            {
                crc ^= value;
                for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0xEDB88320u);
            }
            return crc;
        }
    }
}
