using System.Text.Json;
using System.Text.Json.Serialization;
using MakoKaraGenerator.Backend.Contracts;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

public sealed class SceneMeasurementContractTests
{
    private static readonly JsonSerializerOptions OmitNull = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    [Theory]
    [InlineData("{\"expectedRevision\":\"0\",\"mode\":\"layout\",\"timeUs\":null,\"width\":64,\"height\":36,\"alphaMode\":\"transparent\",\"extra\":1}")]
    [InlineData("{\"expectedRevision\":\"0\",\"mode\":\"layout\",\"timeUs\":null,\"width\":64,\"width\":64,\"height\":36,\"alphaMode\":\"transparent\"}")]
    [InlineData("{\"expectedRevision\":\"0\",\"mode\":\"layout\",\"width\":64,\"height\":36,\"alphaMode\":\"transparent\"}")]
    [InlineData("{\"expectedRevision\":\"00\",\"mode\":\"layout\",\"timeUs\":null,\"width\":64,\"height\":36,\"alphaMode\":\"transparent\"}")]
    [InlineData("{\"expectedRevision\":\"0\",\"mode\":\"time\",\"timeUs\":null,\"width\":64,\"height\":36,\"alphaMode\":\"transparent\"}")]
    [InlineData("{\"expectedRevision\":\"0\",\"mode\":\"layout\",\"timeUs\":\"0\",\"width\":64,\"height\":36,\"alphaMode\":\"transparent\"}")]
    [InlineData("{\"expectedRevision\":\"0\",\"mode\":\"time\",\"timeUs\":\"+1\",\"width\":64,\"height\":36,\"alphaMode\":\"transparent\"}")]
    [InlineData("{\"expectedRevision\":\"0\",\"mode\":\"time\",\"timeUs\":\"0\",\"width\":1281,\"height\":36,\"alphaMode\":\"transparent\"}")]
    [InlineData("{\"expectedRevision\":\"0\",\"mode\":\"time\",\"timeUs\":\"0\",\"width\":64,\"height\":36,\"alphaMode\":\"transparent\",\"previewId\":\"p\"}")]
    [InlineData("{\"expectedRevision\":\"0\",\"mode\":\"time\",\"timeUs\":\"0\",\"width\":64,\"height\":36,\"alphaMode\":\"transparent\",\"previewId\":\"p\",\"expectedPreviewGeneration\":\"1\",\"candidateRevision\":\"candidate\",\"extra\":1}")]
    [InlineData("{\"expectedRevision\":\"0\",\"mode\":\"time\",\"timeUs\":\"0\",\"width\":64,\"height\":36,\"alphaMode\":\"transparent\",\"previewId\":\"p\",\"expectedPreviewGeneration\":\"01\",\"candidateRevision\":\"candidate\"}")]
    [InlineData("{\"expectedRevision\":\"0\",\"mode\":\"time\",\"timeUs\":\"0\",\"width\":64,\"height\":36,\"alphaMode\":\"transparent\",\"previewId\":null,\"expectedPreviewGeneration\":null,\"candidateRevision\":null}")]
    public void 严格只读请求拒绝重复未知缺失字段与非法union(string json) =>
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<SceneMeasurementRequest>(json));

    [Fact]
    public void 候选测量字段可选且序列化时保持候选身份()
    {
        const string json = "{\"expectedRevision\":\"0\",\"mode\":\"time\",\"timeUs\":\"0\",\"width\":64,\"height\":36,\"alphaMode\":\"transparent\",\"previewId\":\"preview-1\",\"expectedPreviewGeneration\":\"3\",\"candidateRevision\":\"candidate-1\"}";
        SceneMeasurementRequest request = JsonSerializer.Deserialize<SceneMeasurementRequest>(json)!;
        Assert.Equal("preview-1", request.PreviewId);
        Assert.Equal("3", request.ExpectedPreviewGeneration);
        Assert.Equal("candidate-1", request.CandidateRevision);
        string roundTrip = JsonSerializer.Serialize(request, OmitNull);
        Assert.Contains("\"previewId\":\"preview-1\"", roundTrip);
        Assert.Contains("\"candidateRevision\":\"candidate-1\"", roundTrip);
        SceneMeasurementRequest current = JsonSerializer.Deserialize<SceneMeasurementRequest>("{\"expectedRevision\":\"0\",\"mode\":\"time\",\"timeUs\":\"0\",\"width\":64,\"height\":36,\"alphaMode\":\"transparent\"}")!;
        Assert.Null(current.PreviewId);
    }

    [Fact]
    public void 渲染后端可独立省略且会参与测量输入指纹()
    {
        byte[] source = "scene-source"u8.ToArray();
        SceneMeasurementRequest legacy = new("0", "time", "0", 64, 36, "transparent");
        SceneMeasurementRequest cpu = legacy with { RenderBackend = "cpu" };
        string legacyJson = JsonSerializer.Serialize(legacy, OmitNull);
        Assert.DoesNotContain("renderBackend", legacyJson, StringComparison.Ordinal);
        Assert.Contains("\"renderBackend\":\"cpu\"", JsonSerializer.Serialize(cpu, OmitNull), StringComparison.Ordinal);
        WorkerSceneMeasurementInput legacyInput = new(legacy, WorkerProtocol.ComputeSha256(source), "");
        WorkerSceneMeasurementInput cpuInput = new(cpu, WorkerProtocol.ComputeSha256(source), "");
        Assert.NotEqual(WorkerSceneMeasurementContract.Fingerprint(source, legacyInput),
            WorkerSceneMeasurementContract.Fingerprint(source, cpuInput));
    }

    [Fact]
    public void 布局时间和不可见几何明确null且完整响应预算整批失败()
    {
        var element = new SceneMeasurementElementDto("node",null,"line",null,"line",null,null,null,null,false,false,0,"notVisible",null,null,[],null,null);
        var data = new SceneMeasurementDataDto(new string('a',64),new string('b',64),"static-lyrics-scene-v1","layout-reference-v1","layout",null,
            64,36,64,36,"transparent",[element],new string('c',64),[]);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new SceneMeasurementResponse("session","0",data),OmitNull));
        Assert.Equal(JsonValueKind.Null,document.RootElement.GetProperty("measurement").GetProperty("timeUs").ValueKind);
        var actual = document.RootElement.GetProperty("measurement").GetProperty("elements")[0];
        foreach (string name in new[] { "worldMatrix","pivot","fixedReference","editReference" }) Assert.Equal(JsonValueKind.Null,actual.GetProperty(name).ValueKind);
        var error = Assert.Throws<WorkerProtocolException>(() => SceneMeasurementContract.EncodeBounded(data with { Png = new byte[SceneMeasurementContract.MaximumResponseBytes] }));
        Assert.Equal("SceneMeasurementBudgetExceeded",error.Code);
    }
}
