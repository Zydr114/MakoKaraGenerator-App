using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using MakoKaraGenerator.Application;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Hosting;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.TestSupport;

namespace MakoKaraGenerator.Backend.Server.Tests;

public sealed partial class AuthorWorkflowTransportTests
{
    [Fact]
    public async Task HTTP源码检查省略null与编码计数精确匹配wire且8MiB边界包含等号()
    {
        await using Fixture fixture = await Fixture.CreateAsync(ConfigureInspectionProductionJson);
        Dictionary<string, byte[]> files = EffectPackageFixture.Files();
        JsonObject manifest = EffectPackageFixture.Manifest(files);
        JsonObject parameter = manifest["parameters"]![0]!.AsObject();
        parameter.Remove("unit"); parameter.Remove("min"); parameter.Remove("max");
        parameter["label"] = "缩放 <&>";
        parameter["type"] = "text"; parameter["default"] = "wire";
        EffectPackageContents package = EffectPackageContents.Create(EffectPackageFixture.Bytes(manifest),
            EffectPackageFixture.Inputs(files), EffectPackageFixture.Profile);
        string archive = fixture.Directory.FilePath("inspection-null-fields.mkfx");
        await EffectPackageTransfer.WriteArchiveAsync(archive, package);
        await using FileStream archiveStream = File.OpenRead(archive);
        StoredAsset asset = await fixture.Resources.UploadAsync(fixture.Client.ClientId, "package.mkfx", "application/octet-stream", archiveStream);
        using HttpResponseMessage created = await fixture.Http.PostAsJsonAsync("/api/v1/drafts",
            new AuthorDraftCreateRequest(asset.AssetId, "scale", "inspection wire budget", fixture.Metadata()));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        AuthorDraftResponse draft = (await created.Content.ReadFromJsonAsync<AuthorDraftResponse>())!;
        string query = "expectedDraftRevision=" + Uri.EscapeDataString(draft.DraftRevision)
            + "&sourceRevision=" + Uri.EscapeDataString(draft.CurrentSourceRevision);
        using HttpResponseMessage inspected = await fixture.Http.GetAsync($"/api/v1/drafts/{draft.DraftId}/source-inspection?{query}");
        Assert.Equal(HttpStatusCode.OK, inspected.StatusCode);
        byte[] wire = await inspected.Content.ReadAsByteArrayAsync();
        using (JsonDocument json = JsonDocument.Parse(wire))
        {
            JsonElement schema = json.RootElement.GetProperty("parameters")[0];
            Assert.False(schema.TryGetProperty("unit", out _));
            Assert.False(schema.TryGetProperty("minimumJson", out _));
            Assert.False(schema.TryGetProperty("maximumJson", out _));
            Assert.Equal("缩放 <&>", schema.GetProperty("label").GetString());
        }
        AuthorSourceInspectionResponse response = JsonSerializer.Deserialize<AuthorSourceInspectionResponse>(wire,
            InspectionTestJson)!;
        EffectPackageParameterSchemaDto projected = Assert.Single(response.Parameters);
        Assert.Null(projected.Unit); Assert.Null(projected.MinimumJson); Assert.Null(projected.MaximumJson);

        // The expected bytes originate in Kestrel's actual JSON response, independently of the budget options.
        JsonSerializerOptions budgetOptions = (JsonSerializerOptions)typeof(ProjectSessionRegistry).GetField(
            "AuthorInspectionJson", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        Assert.Equal(wire, JsonSerializer.SerializeToUtf8Bytes(response, budgetOptions));
        using (Stream exact = NewInspectionBudgetStream(wire.Length))
        {
            JsonSerializer.Serialize(exact, response, budgetOptions);
            Assert.Equal((long)wire.Length, exact.Length);
        }
        using (Stream tooSmall = NewInspectionBudgetStream(wire.Length - 1))
            AssertInspectionBudgetExceeded(() => JsonSerializer.Serialize(tooSmall, response, budgetOptions));

        // Pad only an ASCII string: every appended x adds exactly one wire byte.
        // These synthetic DTOs exercise the real stream's 8MiB boundary, without giant package fixtures.
        const int maximumBytes = 8 * 1024 * 1024;
        Assert.Equal((long)maximumBytes, (long)typeof(ProjectSessionRegistry).GetField(
            "MaximumAuthorInspectionResponseBytes", BindingFlags.Static | BindingFlags.NonPublic)!.GetRawConstantValue()!);
        AuthorSourceInspectionResponse atLimit = response with { Name = response.Name + new string('x', maximumBytes - wire.Length) };
        Microsoft.AspNetCore.Http.Json.JsonOptions httpOptions = new();
        ConfigureInspectionProductionJson(httpOptions);
        Assert.Equal(maximumBytes, JsonSerializer.SerializeToUtf8Bytes(atLimit, httpOptions.SerializerOptions).Length);
        using (Stream exactLimit = NewInspectionBudgetStream(maximumBytes))
        {
            JsonSerializer.Serialize(exactLimit, atLimit, budgetOptions);
            Assert.Equal((long)maximumBytes, exactLimit.Length);
        }
        AuthorSourceInspectionResponse overLimit = atLimit with { Name = atLimit.Name + "x" };
        Assert.Equal(maximumBytes + 1, JsonSerializer.SerializeToUtf8Bytes(overLimit, httpOptions.SerializerOptions).Length);
        using (Stream tooLarge = NewInspectionBudgetStream(maximumBytes))
            AssertInspectionBudgetExceeded(() => JsonSerializer.Serialize(tooLarge, overLimit, budgetOptions));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HTTP采用保留UTF16历史请求和显式null配置兼容性(bool explicitNull)
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        AuthorDraftResponse original = await CreateInspectionDraftAsync(fixture);
        JsonObject legacy = JsonSerializer.SerializeToNode(new
        {
            expectedDraftRevision = original.DraftRevision,
            sourceRevision = original.CurrentSourceRevision,
            metadata = fixture.Metadata(),
        }, InspectionTestJson)!.AsObject();
        if (explicitNull) legacy["Configuration"] = null;
        using StringContent body = new(legacy.ToJsonString(), Encoding.Unicode, "application/json");
        Assert.Equal("utf-16", body.Headers.ContentType!.CharSet);
        byte[] transmitted = await body.ReadAsByteArrayAsync();
        Assert.Equal((byte)'{', transmitted[0]);
        Assert.Equal((byte)0, transmitted[1]);
        using HttpResponseMessage adopted = await fixture.Http.PostAsync($"/api/v1/drafts/{original.DraftId}/adopt-source-revision", body);
        Assert.Equal(HttpStatusCode.OK, adopted.StatusCode);
        AuthorAdoptSourceRevisionResponse receipt = (await adopted.Content.ReadFromJsonAsync<AuthorAdoptSourceRevisionResponse>())!;
        AuthorDraftResponse current = fixture.Projects.ReadAuthorDraft(fixture.Client.ClientId, original.DraftId).Response!;
        Assert.Equal(receipt.DraftRevision, current.DraftRevision);
        Assert.Equal(original.CurrentSourceRevision, current.AdoptedSourceRevision);
        Assert.Equal(original.CurrentSourceRevision, current.CurrentSourceRevision);
        Assert.Equal(JsonSerializer.Serialize(original.Configuration), JsonSerializer.Serialize(current.Configuration));
        Assert.Equal(original.Name, current.Name);
        Assert.Equal(original.ExportId, current.ExportId);
    }

    private static void ConfigureInspectionProductionJson(Microsoft.AspNetCore.Http.Json.JsonOptions options)
    {
        options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.SerializerOptions.DictionaryKeyPolicy = JsonNamingPolicy.CamelCase;
        options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
    }

    private static Stream NewInspectionBudgetStream(long maximumBytes)
    {
        Type stream = typeof(ProjectSessionRegistry).GetNestedType("AuthorInspectionBudgetStream", BindingFlags.NonPublic)!;
        return (Stream)Activator.CreateInstance(stream, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null, args: new object[] { maximumBytes, CancellationToken.None }, culture: null)!;
    }

    private static void AssertInspectionBudgetExceeded(Action serialize)
    {
        Exception? failure = Record.Exception(serialize);
        Assert.NotNull(failure);
        Assert.Equal("AuthorInspectionBudgetException", failure.GetType().Name);
    }
}
