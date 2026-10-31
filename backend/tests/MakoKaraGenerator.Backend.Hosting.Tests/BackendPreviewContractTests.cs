using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using MakoKaraGenerator.Backend.Contracts;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

public sealed class BackendPreviewContractTests
{
    private static readonly JsonSerializerOptions Json = CreateJson();

    private static JsonSerializerOptions CreateJson()
    {
        JsonSerializerOptions options = new(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    [Fact]
    public void 预览请求保留代次序号时间与尺寸透明语义()
    {
        PreviewFrameRequest request = new("generation-3", "26", "180000000", "playback",
            new("request-26", "host-1", DateTimeOffset.Parse("2026-10-12T00:00:00Z", CultureInfo.InvariantCulture)));
        string json = JsonSerializer.Serialize(request, Json);
        PreviewFrameRequest roundTrip = JsonSerializer.Deserialize<PreviewFrameRequest>(json, Json)!;

        Assert.Equal(request, roundTrip);
        Assert.Contains("previewGeneration", json);
        Assert.Contains("timeUs", json);
        Assert.DoesNotContain("180000000.0", json);
    }

    [Fact]
    public void 帧句柄携带固定来源修订哈希与过期时间()
    {
        PreviewFrameMetadataDto frame = new(
            "frame-1", "preview-1", "generation-3", "project-42", "26", "180000000",
            960, 540, PreviewAlphaMode.Straight,
            new("handle-1", "image/png", 12, "abc", DateTimeOffset.UtcNow.AddMinutes(1), "/api/v1/preview-frames/frame-1/content"));

        string json = JsonSerializer.Serialize(frame, Json);

        Assert.Contains("sourceRevision", json);
        Assert.Contains("expiresAtUtc", json);
        Assert.Contains("\"straight\"", json);
        Assert.Equal(PreviewAlphaMode.Straight, frame.AlphaMode);
        Assert.Equal(960, frame.Width);
    }

    [Fact]
    public void 诊断页表达固定代次游标与是否还有后续()
    {
        PreviewDiagnosticsPageDto page = new(
            "preview-1", "generation-3", "project-42",
            [new BackendDiagnosticDto("MissingFont", "warning", "font missing", "line-1")],
            "diag-2", true, "diag-1");

        Assert.True(page.HasMore);
        Assert.Equal("diag-2", page.NextCursor);
        Assert.Equal("generation-3", page.PreviewGeneration);
        Assert.Single(page.Diagnostics);
    }
}
