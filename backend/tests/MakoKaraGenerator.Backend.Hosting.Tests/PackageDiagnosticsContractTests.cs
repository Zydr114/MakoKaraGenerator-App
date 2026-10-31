using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MakoKaraGenerator.Backend.Contracts;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

public sealed class PackageDiagnosticsContractTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static WorkerPackageSourceDiagnosticDto Source(bool error = false) => new("包🎵", "sha256:" + new string('a', 64), null, null,
        "C0001", "完整 Unicode 警告🎵", error, null, 0, 0);
    private static WorkerMessage Message(WorkerPackageDiagnosticsDto value) => new("error", "compile", error: "编译失败", errorCode: "WorkerPackageCompilationFailed",
        sessionId: Guid.NewGuid().ToString("N"), sourceSha256: new string('b', 64), frameSequence: 0, packageDiagnostics: value);

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("true")]
    [InlineData("\"text\"")]
    public async Task 共享Worker消息非对象JSON根保持具名协议失败(string json)
    {
        var exception = await Assert.ThrowsAsync<WorkerProtocolException>(() => ReadJson(json));
        Assert.Equal("WorkerProtocolInvalid", exception.Code);
    }

    [Fact]
    public async Task 源诊断完整保留false零位置空来源重复条目和顺序()
    {
        var dto = new WorkerPackageDiagnosticsDto(1, [Source(), Source(true), Source()], [], []);
        await using MemoryStream stream = new();
        await WorkerProtocol.WriteAsync(stream, Message(dto)); stream.Position = 0;
        var read = await WorkerProtocol.ReadAsync(stream);
        Assert.Equal(dto.Source, read!.PackageDiagnostics!.Source);
        Assert.False(read.PackageDiagnostics.Source[0].IsError);
        Assert.Equal(0, read.PackageDiagnostics.Source[0].Line);
        Assert.Null(read.PackageDiagnostics.Source[0].Path);
    }

    [Theory]
    [InlineData("version")]
    [InlineData("source")]
    [InlineData("image")]
    [InlineData("material")]
    [InlineData("isError")]
    [InlineData("line")]
    [InlineData("column")]
    [InlineData("path")]
    [InlineData("instanceId")]
    [InlineData("exportId")]
    public async Task 所有字段必须显式存在包括合法默认值和null(string field)
    {
        var root = JsonSerializer.SerializeToNode(Message(new(1, [Source()], [], [])), Json)!.AsObject();
        var package = root["packageDiagnostics"]!.AsObject();
        if (!package.Remove(field)) package["source"]![0]!.AsObject().Remove(field);
        await Assert.ThrowsAsync<WorkerProtocolException>(() => ReadJson(root.ToJsonString()));
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    [InlineData("duplicate-container")]
    [InlineData("case-container")]
    [InlineData("wrong-version")]
    public async Task 未知重复字段及旧诊断版本稳定拒绝(string kind)
    {
        string json = JsonSerializer.Serialize(Message(new(1, [Source()], [], [])), Json);
        json = kind switch
        {
            "unknown" => json.Replace("\"version\":1", "\"version\":1,\"extra\":0", StringComparison.Ordinal),
            "duplicate" => json.Replace("\"isError\":false", "\"isError\":false,\"IsError\":true", StringComparison.Ordinal),
            "duplicate-container" => json[..^1] + ",\"packageDiagnostics\":null}",
            "case-container" => json[..^1] + ",\"PackageDiagnostics\":null}",
            _ => json.Replace("\"version\":1", "\"version\":0", StringComparison.Ordinal),
        };
        await Assert.ThrowsAsync<WorkerProtocolException>(() => ReadJson(json));
    }

    [Fact]
    public void 单类预算包含实际UTF8序列化结构且边界不截断()
    {
        var empty = Source() with { Message = "" };
        int overhead = JsonSerializer.SerializeToUtf8Bytes(new[] { empty }, Json).Length;
        var exact = empty with { Message = new string('x', WorkerPackageDiagnosticsContract.MaximumBytesPerKind - overhead) };
        var dto = new WorkerPackageDiagnosticsDto(1, [exact], [], []);
        WorkerPackageDiagnosticsContract.Validate(dto);
        var exception = Assert.Throws<WorkerProtocolException>(() => WorkerPackageDiagnosticsContract.Validate(dto with { Source = [exact with { Message = exact.Message + "🎵" }] }));
        Assert.Equal("WorkerPackageDiagnosticsBudgetExceeded", exception.Code);
        Assert.Equal(exact.Message.Length, dto.Source[0].Message.Length);
    }

    [Fact]
    public void 单类条数与合计条数字节均有独立上限()
    {
        var source = Source() with { PackageId = "p", Code = "c", Message = "", Path = null };
        var tooMany = new WorkerPackageDiagnosticsDto(1, Enumerable.Repeat(source, 257).ToArray(), [], []);
        Assert.Equal("WorkerPackageDiagnosticsBudgetExceeded", Assert.Throws<WorkerProtocolException>(() => WorkerPackageDiagnosticsContract.Validate(tooMany)).Code);
        var resource = new WorkerPackageResourceDto("p", source.Revision, "x");
        var image = new WorkerPackageImageDiagnosticDto("i", new string('a', 64), resource, "b", "c");
        var material = new WorkerPackageMaterialDiagnosticDto(new("script", "i", new string('a', 64), null, null, null, null, null, null), resource, "b", "c");
        var combined = new WorkerPackageDiagnosticsDto(1, Enumerable.Repeat(source, 200).ToArray(), Enumerable.Repeat(image, 200).ToArray(), Enumerable.Repeat(material, 113).ToArray());
        Assert.Equal("WorkerPackageDiagnosticsBudgetExceeded", Assert.Throws<WorkerProtocolException>(() => WorkerPackageDiagnosticsContract.Validate(combined)).Code);
        Assert.Equal(512, WorkerPackageDiagnosticsContract.MaximumItems);
        Assert.Equal(128 * 1024, WorkerPackageDiagnosticsContract.MaximumBytes);
    }

    [Theory]
    [InlineData("source")]
    [InlineData("image")]
    [InlineData("material")]
    public void 各类数组独立限制256条及64KiB(string kind)
    {
        var resource = new WorkerPackageResourceDto("p", "sha256:" + new string('a', 64), "x");
        var image = new WorkerPackageImageDiagnosticDto("i", new string('a', 64), resource, "b", "c");
        var material = new WorkerPackageMaterialDiagnosticDto(new("script", "i", new string('a', 64), null, null, null, null, null, null), resource, "b", "c");
        var dto = kind switch
        {
            "source" => new WorkerPackageDiagnosticsDto(1, Enumerable.Repeat(Source(), 257).ToArray(), [], []),
            "image" => new WorkerPackageDiagnosticsDto(1, [], Enumerable.Repeat(image, 257).ToArray(), []),
            _ => new WorkerPackageDiagnosticsDto(1, [], [], Enumerable.Repeat(material, 257).ToArray()),
        };
        Assert.Equal("WorkerPackageDiagnosticsBudgetExceeded", Assert.Throws<WorkerProtocolException>(() => WorkerPackageDiagnosticsContract.Validate(dto)).Code);
        dto = kind switch
        {
            "source" => new(1, [Source() with { Message = new string('x', 65536) }], [], []),
            "image" => new(1, [], [image with { InputId = new string('x', 65536) }], []),
            _ => new(1, [], [], [material with { InputId = new string('x', 65536) }]),
        };
        Assert.Equal("WorkerPackageDiagnosticsBudgetExceeded", Assert.Throws<WorkerProtocolException>(() => WorkerPackageDiagnosticsContract.Validate(dto)).Code);
    }

    [Fact]
    public void 合计128KiB预算在各类单独合法时独立拒绝()
    {
        var resource = new WorkerPackageResourceDto("p", "sha256:" + new string('a', 64), "x");
        var dto = new WorkerPackageDiagnosticsDto(1, [Source() with { Message = new string('x', 45000) }],
            [new("i", new string('a', 64), resource, new string('x', 45000), "c")],
            [new(new("script", "i", new string('a', 64), null, null, null, null, null, null), resource, new string('x', 45000), "c")]);
        WorkerPackageDiagnosticsContract.Validate(dto with { Image = [], Material = [] });
        WorkerPackageDiagnosticsContract.Validate(dto with { Source = [], Material = [] });
        WorkerPackageDiagnosticsContract.Validate(dto with { Source = [], Image = [] });
        Assert.Equal("WorkerPackageDiagnosticsBudgetExceeded", Assert.Throws<WorkerProtocolException>(() => WorkerPackageDiagnosticsContract.Validate(dto)).Code);
    }

    [Fact]
    public async Task 接收预算计算实际wire空白字节而非只算规范重编码()
    {
        string json = JsonSerializer.Serialize(Message(WorkerPackageDiagnosticsContract.Empty), Json);
        json = json.Replace("\"source\":[]", "\"source\":[" + new string(' ', 65536) + "]", StringComparison.Ordinal);
        var exception = await Assert.ThrowsAsync<WorkerProtocolException>(() => ReadJson(json));
        Assert.Equal("WorkerPackageDiagnosticsBudgetExceeded", exception.Code);
    }

    [Theory]
    [InlineData("result")]
    [InlineData(StaticRenderWorkerContract.FrameResultKind)]
    [InlineData(PackageRenderWorkerContract.OpenKind)]
    [InlineData(WorkerExportContract.ResultKind)]
    public void 包诊断禁止混入非包成功及请求(string kind)
    {
        var message = Message(WorkerPackageDiagnosticsContract.Empty) with { Kind = kind };
        Assert.Throws<WorkerProtocolException>(() => WorkerProtocol.Validate(message));
    }
    private static async Task ReadJson(string json)
    {
        byte[] body = Encoding.UTF8.GetBytes(json); byte[] prefix = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, body.Length);
        using MemoryStream stream = new([.. prefix, .. body]);
        await WorkerProtocol.ReadAsync(stream);
    }
}
