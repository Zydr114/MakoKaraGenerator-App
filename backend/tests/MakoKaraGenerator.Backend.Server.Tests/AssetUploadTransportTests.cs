using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Hosting;

namespace MakoKaraGenerator.Backend.Server.Tests;

public sealed partial class PreviewTransportTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 上传HTTP默认字节无限额并流式接收超过旧16MiB的原始与分段文件(bool multipart)
    {
        await using Fixture fixture = await Fixture.Create();
        using HttpResponseMessage capabilities = await fixture.Http.GetAsync("/api/v1/capabilities");
        using JsonDocument document = JsonDocument.Parse(await capabilities.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("maxAssetBytes").ValueKind);
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("maxAssetQuotaBytes").ValueKind);
        const int size = 17 * 1024 * 1024 + 13;
        using HttpResponseMessage response = await StreamedAssetUpload(fixture, size, multipart);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        AssetResponse uploaded = (await response.Content.ReadFromJsonAsync<AssetResponse>(Json))!;
        Assert.Equal(size, uploaded.Size);
        StoredAsset stored = fixture.Resources.GetAuthorized(fixture.Client.ClientId, uploaded.AssetId);
        await using FileStream saved = File.OpenRead(stored.StoragePath);
        string actual = Convert.ToHexString(await SHA256.HashDataAsync(saved)).ToLowerInvariant();
        await using PatternUploadStream expected = new(size);
        Assert.Equal(Convert.ToHexString(await SHA256.HashDataAsync(expected)).ToLowerInvariant(), actual);
        Assert.Equal(actual, uploaded.Sha256);
        Assert.Empty(Directory.GetFiles(fixture._directory.Path, "*.upload", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 上传HTTP部署单文件限额对未知长度流逐块生效且拒绝后释放候选(bool multipart)
    {
        await using Fixture fixture = await Fixture.Create(maxAssetBytes: 64, maxAssetQuotaBytes: 128);
        long initial = fixture.Resources.UsedBytes;
        using HttpResponseMessage refused = await StreamedAssetUpload(fixture, 65, multipart);
        await Error(refused, HttpStatusCode.RequestEntityTooLarge, BackendErrorCode.ResourceTooLarge);
        Assert.Equal(initial, fixture.Resources.UsedBytes);
        Assert.Empty(Directory.GetFiles(fixture._directory.Path, "*.upload", SearchOption.AllDirectories));
        using HttpResponseMessage accepted = await StreamedAssetUpload(fixture, 64, multipart);
        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
        Assert.Equal(initial + 64, fixture.Resources.UsedBytes);
        using HttpResponseMessage quota = await StreamedAssetUpload(fixture, 64, multipart);
        await Error(quota, HttpStatusCode.TooManyRequests, BackendErrorCode.ResourceQuotaExceeded);
        Assert.Equal(initial + 64, fixture.Resources.UsedBytes);
        Assert.Empty(Directory.GetFiles(fixture._directory.Path, "*.upload", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task 上传HTTP满字节和数量配额时精确回执重试不重复预留且完整Hash变更拒绝()
    {
        await using Fixture fixture = await Fixture.Create(maxAssetBytes: 128, maxAssetQuotaBytes: 128, maxAssets: 3);
        int size = checked((int)(128 - fixture.Resources.UsedBytes));
        RequestMetadataDto metadata = fixture.Metadata();
        using HttpResponseMessage first = await StreamedAssetUpload(fixture, size, false, metadata);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        AssetResponse asset = (await first.Content.ReadFromJsonAsync<AssetResponse>(Json))!;
        using HttpResponseMessage retry = await StreamedAssetUpload(fixture, size, true, metadata);
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        Assert.Equal(asset, await retry.Content.ReadFromJsonAsync<AssetResponse>(Json));
        using HttpResponseMessage changed = await StreamedAssetUpload(fixture, size, false, metadata, salt: 1);
        await Error(changed, HttpStatusCode.Conflict, BackendErrorCode.RequestIdReuse);
        using HttpResponseMessage full = await StreamedAssetUpload(fixture, 1, false);
        await Error(full, HttpStatusCode.TooManyRequests, BackendErrorCode.ResourceQuotaExceeded);
        Assert.Equal(128, fixture.Resources.UsedBytes);
        Assert.Equal(3, Directory.GetFiles(Path.Combine(fixture._directory.Path, "resources"), "asset-*", SearchOption.AllDirectories).Length);
        Assert.Empty(Directory.GetFiles(fixture._directory.Path, "*.upload", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task 上传HTTP未接受元信息及第二个分段文件均清理已固定候选()
    {
        await using Fixture fixture = await Fixture.Create(maxAssetBytes: 128, maxAssetQuotaBytes: 128, maxAssets: 3);
        long initial = fixture.Resources.UsedBytes;
        using HttpResponseMessage expired = await StreamedAssetUpload(fixture, 64, false,
            fixture.Metadata() with { ValidUntilUtc = fixture.Clock.GetUtcNow().AddSeconds(-1) });
        await Error(expired, HttpStatusCode.Conflict, BackendErrorCode.RequestExpired);
        using HttpResponseMessage multiple = await StreamedAssetUpload(fixture, 32, true, secondFile: true);
        await Error(multiple, HttpStatusCode.BadRequest, BackendErrorCode.InvalidRequest);
        Assert.Equal(initial, fixture.Resources.UsedBytes);
        Assert.Empty(Directory.GetFiles(fixture._directory.Path, "*.upload", SearchOption.AllDirectories));
        using HttpResponseMessage replacement = await StreamedAssetUpload(fixture, 64, false);
        Assert.Equal(HttpStatusCode.Created, replacement.StatusCode);
    }

    private static async Task<HttpResponseMessage> StreamedAssetUpload(Fixture fixture, int size, bool multipart,
        RequestMetadataDto? metadata = null, byte salt = 0, bool secondFile = false)
    {
        metadata ??= fixture.Metadata();
        using HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/assets");
        StreamContent file = new(new PatternUploadStream(size, salt));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        if (multipart)
        {
            MultipartFormDataContent form = new();
            form.Add(new StringContent("streamed"), "description");
            form.Add(file, "content", "stream.bin");
            if (secondFile) form.Add(new ByteArrayContent([1]), "other", "other.bin");
            request.Content = form;
        }
        else request.Content = file;
        request.Headers.Add("X-Mako-Asset-Name", "stream.bin");
        request.Headers.Add("X-Mako-Request-Id", metadata.RequestId);
        request.Headers.Add("X-Mako-Host-Instance-Id", metadata.HostInstanceId);
        request.Headers.Add("X-Mako-Valid-Until-Utc", metadata.ValidUntilUtc!.Value.ToString("O", CultureInfo.InvariantCulture));
        return await fixture.Http.SendAsync(request);
    }

    // Non-seekable and generated per read, so the test request never owns the full payload.
    private sealed class PatternUploadStream(long length, byte salt = 0) : Stream
    {
        private long _position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            int count = (int)Math.Min(buffer.Length, length - _position);
            for (int i = 0; i < count; i++) buffer[i] = (byte)((_position + i + salt) % 251);
            _position += count;
            return count;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Read(buffer.Span));
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
