using System.Net;
using System.Net.Http.Json;
using MakoKaraGenerator.Backend.Contracts;

namespace MakoKaraGenerator.Backend.Server.Tests;

public sealed partial class PreviewTransportTests
{
    [Fact]
    public async Task Gpu_stream_capability_and_private_claim_are_absent_without_desktop_configuration()
    {
        await using Fixture fixture = await Fixture.Create();
        BackendCapabilitiesResponse capabilities = (await fixture.Http.GetFromJsonAsync<BackendCapabilitiesResponse>("/api/v1/capabilities", Json))!;
        Assert.DoesNotContain("preview.gpu-streams", capabilities.Features);
        Assert.Null(capabilities.GpuPreview);
        using HttpResponseMessage response = await fixture.Http.PostAsJsonAsync("/internal/gpu-preview-streams/test/claim",
            new { consumerId = new string('a', 32), hostInstanceId = "host", expectedInputGeneration = "1" }, Json);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using HttpResponseMessage publicResponse = await fixture.Http.PostAsJsonAsync("/api/v1/gpu-preview-streams", new { }, Json);
        Assert.Equal(HttpStatusCode.NotFound, publicResponse.StatusCode);
    }
}
