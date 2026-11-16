using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MakoKaraGenerator.Application;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Hosting;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.TestSupport;
using SkiaSharp;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

/// <summary>作者草稿整份替换包内图片、字体（替换素材设计 §3）。</summary>
public sealed class BackendAuthorPackageReplacementTests
{
    private const string ImagePath = "assets/spark.png";
    private const string FontPath = "fonts/fixed-cjk.ttc";

    [Fact]
    public async Task 替换图片检查给出尺寸并在保存后生成新修订和新哈希()
    {
        await using Fixture fixture = await Fixture.CreateAsync(withFont: false);
        AuthorPackageFilesResponse before = fixture.Files();
        byte[] replacement = Image(16, 12, SKEncodedImageFormat.Png, SKColors.Red);
        string assetId = await fixture.UploadAsync("new.png", "image/png", replacement);

        BackendOperationResult<AuthorPackageFileReplacementInspectionResponse> inspected = await fixture.InspectAsync(before, ImagePath, assetId);
        Assert.True(inspected.Accepted, inspected.Error?.Message);
        AuthorPackageFileReplacementInspectionResponse inspection = inspected.Response!;
        Assert.Equal(("image", "png", 8, 8), (inspection.Role, inspection.Current.Format, inspection.Current.Width!.Value, inspection.Current.Height!.Value));
        Assert.Equal(("png", 16, 12, Sha(replacement)), (inspection.Replacement.Format, inspection.Replacement.Width!.Value,
            inspection.Replacement.Height!.Value, inspection.Replacement.Sha256));
        Assert.Equal(["imageSizeChanged"], inspection.Warnings.Select(warning => warning.Code));

        BackendOperationResult<AuthorDraftResponse> saved = await fixture.SaveAsync(before, [new(ImagePath, assetId)]);
        Assert.True(saved.Accepted, saved.Error?.Message);
        Assert.NotEqual(before.SourceRevision, saved.Response!.CurrentSourceRevision);
        AuthorPackageFilesResponse after = fixture.Files(saved.Response);
        AuthorPackageFileDto image = after.Files.Single(file => file.Path == ImagePath);
        Assert.Equal((Sha(replacement), (long)replacement.Length, "image"), (image.Sha256, image.SizeBytes, image.Role));
        Assert.Contains(Sha(replacement), after.ManifestJson, StringComparison.Ordinal);
        Assert.Equal(before.Files.Where(file => file.Path != ImagePath).Select(file => (file.Path, file.Sha256)),
            after.Files.Where(file => file.Path != ImagePath).Select(file => (file.Path, file.Sha256)));

        // 同一字节再替换一次：内容相同，不新建修订。
        BackendOperationResult<AuthorDraftResponse> again = await fixture.SaveAsync(after, [new(ImagePath, assetId)]);
        Assert.True(again.Accepted, again.Error?.Message);
        Assert.Equal(saved.Response.DraftRevision, again.Response!.DraftRevision);
    }

    [Fact]
    public async Task 格式不同目标不对或文件坏了都拒绝且不生成修订()
    {
        await using Fixture fixture = await Fixture.CreateAsync(withFont: false);
        AuthorPackageFilesResponse before = fixture.Files();
        string jpeg = await fixture.UploadAsync("new.jpg", "image/jpeg", Image(8, 8, SKEncodedImageFormat.Jpeg, SKColors.Blue));
        string png = await fixture.UploadAsync("new.png", "image/png", Image(8, 8, SKEncodedImageFormat.Png, SKColors.Blue));
        string broken = await fixture.UploadAsync("broken.png", "image/png", [0x89, (byte)'P', (byte)'N', (byte)'G', 1, 2, 3, 4]);

        BackendOperationResult<AuthorPackageFileReplacementInspectionResponse> mismatch = await fixture.InspectAsync(before, ImagePath, jpeg);
        Assert.Equal(("AuthorReplacementFormatMismatch", "assetId"), (mismatch.Error!.FailureCode, mismatch.Error.Field));
        BackendOperationResult<AuthorPackageFileReplacementInspectionResponse> readme = await fixture.InspectAsync(before, "README.md", png);
        Assert.Equal(("AuthorReplacementTargetInvalid", "path"), (readme.Error!.FailureCode, readme.Error.Field));

        (AuthorPackageFileReplacementDto[] Files, string Code, string Field)[] cases =
        [
            ([new(ImagePath, jpeg)], "AuthorReplacementFormatMismatch", "replacedFiles[0].assetId"),
            ([new(ImagePath, broken)], "PackageImageDecodeFailed", "replacedFiles[0].assetId"),
            ([new("README.md", png)], "AuthorReplacementTargetInvalid", "replacedFiles[0].path"),
            ([new("src/PackEffect.cs", png)], "AuthorReplacementTargetInvalid", "replacedFiles[0].path"),
            ([new("missing.png", png)], "AuthorReplacementTargetInvalid", "replacedFiles[0].path"),
            ([new(ImagePath, png), new(ImagePath, png)], "AuthorReplacementTargetInvalid", "replacedFiles[1].path"),
        ];
        foreach ((AuthorPackageFileReplacementDto[] files, string code, string field) in cases)
        {
            BackendOperationResult<AuthorDraftResponse> rejected = await fixture.SaveAsync(before, files);
            Assert.False(rejected.Accepted);
            Assert.Equal((BackendErrorCode.InvalidRequest, code, field), (rejected.Error!.Code, rejected.Error.FailureCode, rejected.Error.Field));
        }
        BackendOperationResult<AuthorDraftResponse> unknown = await fixture.SaveAsync(before, [new(ImagePath, "asset-" + new string('0', 32))]);
        Assert.Equal("replacedFiles[0].assetId", unknown.Error!.Field);
        Assert.Equal(before.DraftRevision, fixture.Registry.ReadAuthorDraft("client", fixture.Draft.DraftId).Response!.DraftRevision);
    }

    [Fact]
    public async Task 按基准修订保存时不可编辑文件取自所看的修订()
    {
        await using Fixture fixture = await Fixture.CreateAsync(withFont: false);
        AuthorPackageFilesResponse original = fixture.Files();
        byte[] originalImage = Image(8, 8, SKEncodedImageFormat.Png, SKColors.White);
        string assetId = await fixture.UploadAsync("new.png", "image/png", Image(8, 8, SKEncodedImageFormat.Png, SKColors.Green));
        BackendOperationResult<AuthorDraftResponse> replaced = await fixture.SaveAsync(original, [new(ImagePath, assetId)]);
        Assert.True(replaced.Accepted, replaced.Error?.Message);

        // 查看原修订、改文本再保存：不带基准时按最新修订核对清单哈希，会被拒绝。
        AuthorPackageFilesResponse viewed = fixture.Files(replaced.Response!, original.SourceRevision);
        AuthorPackageTextFileDto[] edited = [.. viewed.Files.Where(file => file.Editable).Select(file => new AuthorPackageTextFileDto(file.Path, file.Text + "\n// old"))];
        BackendOperationResult<AuthorDraftResponse> withoutBase = await fixture.Registry.SaveAuthorPackageFilesAsync("client", fixture.Draft.DraftId,
            new(replaced.Response!.DraftRevision, viewed.ManifestJson, edited, Metadata: fixture.Metadata()));
        Assert.Equal("manifestJson.files", withoutBase.Error!.Field);

        BackendOperationResult<AuthorDraftResponse> withBase = await fixture.Registry.SaveAuthorPackageFilesAsync("client", fixture.Draft.DraftId,
            new(replaced.Response.DraftRevision, viewed.ManifestJson, edited, Metadata: fixture.Metadata(), BaseSourceRevision: original.SourceRevision));
        Assert.True(withBase.Accepted, withBase.Error?.Message);
        Assert.Equal(Sha(originalImage), fixture.Files(withBase.Response!).Files.Single(file => file.Path == ImagePath).Sha256);

        BackendOperationResult<AuthorDraftResponse> unknownBase = await fixture.Registry.SaveAuthorPackageFilesAsync("client", fixture.Draft.DraftId,
            new(withBase.Response!.DraftRevision, viewed.ManifestJson, edited, Metadata: fixture.Metadata(), BaseSourceRevision: "sha256:" + new string('a', 64)));
        Assert.Equal((BackendErrorCode.ResourceNotFound, "baseSourceRevision"), (unknownBase.Error!.Code, unknownBase.Error.Field));
        BackendOperationResult<AuthorDraftResponse> malformedBase = await fixture.Registry.SaveAuthorPackageFilesAsync("client", fixture.Draft.DraftId,
            new(withBase.Response.DraftRevision, viewed.ManifestJson, edited, Metadata: fixture.Metadata(), BaseSourceRevision: new string('a', 64)));
        Assert.Equal((BackendErrorCode.InvalidRequest, "baseSourceRevision"), (malformedBase.Error!.Code, malformedBase.Error.Field));
    }

    [Fact]
    public async Task 字体替换检查家族名变化只提醒并且可以保存()
    {
        await using Fixture fixture = await Fixture.CreateAsync(withFont: true);
        AuthorPackageFilesResponse before = fixture.Files();
        (byte[] replacementTtc, string expectedFamily) = ReplacementCollectionFont();
        string assetId = await fixture.UploadAsync("noto.ttc", "application/octet-stream", replacementTtc);

        BackendOperationResult<AuthorPackageFileReplacementInspectionResponse> inspected = await fixture.InspectAsync(before, FontPath, assetId);
        Assert.True(inspected.Accepted, inspected.Error?.Message);
        AuthorPackageFileReplacementInspectionResponse inspection = inspected.Response!;
        Assert.Equal(("font", "collection", "collection"), (inspection.Role, inspection.Current.Format, inspection.Replacement.Format));
        Assert.Contains(PackageFontFixture.Family, inspection.Current.FamilyNames!);
        Assert.Contains(expectedFamily, inspection.Replacement.FamilyNames!);
        Assert.Null(inspection.Replacement.Width);
        Assert.Equal(["fontFamilyChanged"], inspection.Warnings.Select(warning => warning.Code));

        BackendOperationResult<AuthorDraftResponse> saved = await fixture.SaveAsync(before, [new(FontPath, assetId)]);
        Assert.True(saved.Accepted, saved.Error?.Message);
        Assert.Equal(Sha(replacementTtc), fixture.Files(saved.Response!).Files.Single(file => file.Path == FontPath).Sha256);

        string ttfPath = new[] { "/usr/share/fonts", "/usr/local/share/fonts", Environment.GetFolderPath(Environment.SpecialFolder.Fonts) }
            .Where(Directory.Exists)
            .SelectMany(root => Directory.EnumerateFiles(root, "*.ttf", SearchOption.AllDirectories))
            .First();
        string ttf = await fixture.UploadAsync("plain.ttf", "application/octet-stream", File.ReadAllBytes(ttfPath));
        BackendOperationResult<AuthorPackageFileReplacementInspectionResponse> mismatch = await fixture.InspectAsync(fixture.Files(saved.Response), FontPath, ttf);
        Assert.Equal("AuthorReplacementFormatMismatch", mismatch.Error!.FailureCode);
        string garbage = await fixture.UploadAsync("bad.ttc", "application/octet-stream", Encoding.ASCII.GetBytes("ttcf-not-really-a-font"));
        BackendOperationResult<AuthorPackageFileReplacementInspectionResponse> invalid = await fixture.InspectAsync(fixture.Files(saved.Response), FontPath, garbage);
        Assert.Equal("AuthorReplacementFontInvalid", invalid.Error!.FailureCode);
    }

    private static (byte[] Bytes, string Family) ReplacementCollectionFont()
    {
        const string linuxPath = "/usr/share/fonts/noto-cjk/NotoSansCJK-Regular.ttc";
        if (OperatingSystem.IsLinux() || File.Exists(linuxPath))
            return (File.ReadAllBytes(linuxPath), "Noto Sans CJK SC");

        string fontsDir = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        string msyh = Path.Combine(fontsDir, "msyh.ttc");
        if (File.Exists(msyh))
            return (File.ReadAllBytes(msyh), "Microsoft YaHei");

        string simsun = Path.Combine(fontsDir, "simsun.ttc");
        return (File.ReadAllBytes(simsun), "SimSun");
    }

    private static byte[] Image(int width, int height, SKEncodedImageFormat format, SKColor color)
    {
        using SKBitmap bitmap = new(width, height);
        bitmap.Erase(color);
        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData data = image.Encode(format, 90);
        return data.ToArray();
    }

    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly TemporaryDirectory _directory;
        private readonly ResourceStore _resources;
        internal ProjectSessionRegistry Registry { get; }
        internal AuthorDraftResponse Draft { get; }
        private Fixture(TemporaryDirectory directory, ResourceStore resources, ProjectSessionRegistry registry, AuthorDraftResponse draft)
        { _directory = directory; _resources = resources; Registry = registry; Draft = draft; }

        internal RequestMetadataDto Metadata() => new(Guid.NewGuid().ToString("N"), Registry.HostInstanceId, Registry.ServerTimeUtc.AddMinutes(1));

        internal AuthorPackageFilesResponse Files(AuthorDraftResponse? draft = null, string? sourceRevision = null)
        {
            AuthorDraftResponse current = draft ?? Registry.ReadAuthorDraft("client", Draft.DraftId).Response!;
            BackendOperationResult<AuthorPackageFilesResponse> files = Registry.ReadAuthorPackageFiles("client", current.DraftId,
                current.DraftRevision, sourceRevision ?? current.CurrentSourceRevision);
            Assert.True(files.Accepted, files.Error?.Message);
            return files.Response!;
        }

        internal async Task<string> UploadAsync(string name, string contentType, byte[] bytes)
        {
            using MemoryStream stream = new(bytes);
            return (await _resources.UploadAsync("client", name, contentType, stream)).AssetId;
        }

        internal Task<BackendOperationResult<AuthorPackageFileReplacementInspectionResponse>> InspectAsync(
            AuthorPackageFilesResponse files, string path, string assetId)
            => Registry.InspectAuthorPackageFileReplacementAsync("client", Draft.DraftId, new(files.DraftRevision, files.SourceRevision, path, assetId));

        internal Task<BackendOperationResult<AuthorDraftResponse>> SaveAsync(AuthorPackageFilesResponse files, AuthorPackageFileReplacementDto[] replaced)
            => Registry.SaveAuthorPackageFilesAsync("client", Draft.DraftId, new(files.DraftRevision, files.ManifestJson,
                [.. files.Files.Where(file => file.Editable).Select(file => new AuthorPackageTextFileDto(file.Path, file.Text!))],
                Metadata: Metadata(), ReplacedFiles: replaced, BaseSourceRevision: files.SourceRevision));

        internal static async Task<Fixture> CreateAsync(bool withFont)
        {
            Dictionary<string, byte[]> files = EffectPackageFixture.Files();
            files.Add(ImagePath, Image(8, 8, SKEncodedImageFormat.Png, SKColors.White));
            if (withFont) files.Add(FontPath, PackageFontFixture.FontBytes());
            JsonObject manifest = EffectPackageFixture.Manifest(files);
            foreach (JsonNode? entry in manifest["files"]!.AsArray())
            {
                string path = entry!["path"]!.GetValue<string>();
                if (path == ImagePath) entry["role"] = "image";
                else if (path == FontPath) entry["role"] = "font";
            }
            EffectPackageContents package = EffectPackageContents.Create(EffectPackageFixture.Bytes(manifest),
                EffectPackageFixture.Inputs(files), EffectPackageFixture.Profile);
            TemporaryDirectory directory = new();
            ResourceStore resources = new(new() { ResourceRoot = directory.FilePath("resources") });
            ProjectSessionRegistry registry = new("author-package-replacement", resources, NewProjectPreparationService.CreateDefault());
            registry.ConfigurePersistence(new() { StateRoot = directory.FilePath("state") });
            registry.ConfigureAuthorWorkflows(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"));
            try
            {
                string archive = directory.FilePath("package.mkfx");
                await EffectPackageTransfer.WriteArchiveAsync(archive, package);
                await using FileStream stream = File.OpenRead(archive);
                StoredAsset asset = await resources.UploadAsync("client", "package.mkfx", "application/octet-stream", stream);
                BackendOperationResult<AuthorDraftResponse> created = await registry.CreateAuthorDraftAsync("client",
                    new(asset.AssetId, "scale", "author", new(Guid.NewGuid().ToString("N"), registry.HostInstanceId, registry.ServerTimeUtc.AddMinutes(1))));
                Assert.True(created.Accepted, created.Error?.Message);
                return new(directory, resources, registry, created.Response!);
            }
            catch { await registry.DisposeAsync(); directory.Dispose(); throw; }
        }

        public async ValueTask DisposeAsync() { await Registry.DisposeAsync(); _directory.Dispose(); }
    }
}
