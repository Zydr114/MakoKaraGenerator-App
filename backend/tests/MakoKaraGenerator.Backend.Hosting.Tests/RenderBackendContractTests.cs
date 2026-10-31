using System.Text;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Hosting;
using MakoKaraGenerator.Backend.Rendering;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

public sealed class RenderBackendContractTests
{
    [Theory]
    [InlineData("cpu", "raster")]
    [InlineData("gpu", "vulkan")]
    public void Verification_request_and_result_round_trip(string backend, string api)
    {
        byte[] requestPayload = WorkerRenderBackendContract.EncodeRequest(backend);
        WorkerMessage request = new(WorkerRenderBackendContract.RequestKind, "request",
            WorkerProtocol.ComputeSha256(requestPayload), requestPayload);
        RenderBackendDeviceInfoDto device = new("fixture-vendor", "fixture-device", null, api == "vulkan" ? "1.3" : null);
        byte[] resultPayload = WorkerRenderBackendContract.EncodeResult(backend, device);
        WorkerMessage response = new(WorkerRenderBackendContract.ResultKind, request.RequestId, request.InputSha256,
            resultPayload, outputSha256: WorkerProtocol.ComputeSha256(resultPayload));

        Assert.Equal(backend, WorkerRenderBackendContract.DecodeRequest(request));
        Assert.Equal(api, WorkerRenderBackendContract.Api(backend));
        Assert.Equal(device, WorkerRenderBackendContract.DecodeResult(request, response, backend));
    }

    [Fact]
    public void Public_backend_selection_is_case_sensitive_and_rejects_vulkan()
    {
        Assert.True(WorkerRenderBackendContract.IsSupported("cpu"));
        Assert.True(WorkerRenderBackendContract.IsSupported("gpu"));
        Assert.False(WorkerRenderBackendContract.IsSupported("CPU"));
        Assert.False(WorkerRenderBackendContract.IsSupported("vulkan"));
        Assert.False(WorkerRenderBackendContract.IsSupported(""));
        Assert.Throws<ArgumentException>(() => WorkerRenderBackendContract.Api("vulkan"));

        BackendRenderBackendConfiguration configuration = new(null, "vulkan");
        Assert.Equal("gpu", configuration.DefaultBackend);
        Assert.Equal("cpu", configuration.Resolve(null, "cpu"));
        Assert.Throws<ArgumentException>(() => configuration.Resolve("vulkan"));
        Assert.Throws<ArgumentException>(() => configuration.Resolve("CPU"));
        Assert.Throws<ArgumentException>(() => configuration.Resolve(""));
    }

    [Fact]
    public async Task Configuration_without_worker_reports_bounded_unavailable_results()
    {
        BackendRenderBackendConfiguration configuration = new(null, "cpu");
        BackendErrorResponse? verification = await configuration.VerifyAsync("gpu");
        Assert.NotNull(verification);
        Assert.Equal(BackendErrorCode.TaskUnavailable, verification!.Code);
        Assert.Equal("renderBackend", verification.Field);
        Assert.Equal(WorkerRenderBackendContract.UnavailableCode, verification.FailureCode);

        RenderBackendsResponse response = await configuration.ReadAsync();
        Assert.Equal("cpu", response.DefaultBackend);
        Assert.Equal(["cpu", "gpu"], response.Backends.Select(value => value.RenderBackend));
        Assert.All(response.Backends, value =>
        {
            Assert.False(value.Available);
            Assert.Equal("renderBackend", value.UnavailableReason!.Field);
        });
    }

    [Fact]
    public void Nested_duplicate_properties_and_bad_result_correlation_are_rejected()
    {
        byte[] duplicate = Encoding.UTF8.GetBytes("{\"version\":1,\"renderBackend\":\"cpu\",\"device\":{\"device\":\"a\",\"device\":\"b\"}}");
        WorkerMessage request = new(WorkerRenderBackendContract.RequestKind, "request",
            WorkerProtocol.ComputeSha256(duplicate), duplicate);
        Assert.Equal(WorkerRenderBackendContract.InvalidCode,
            Assert.Throws<WorkerProtocolException>(() => WorkerRenderBackendContract.DecodeRequest(request)).Code);

        byte[] requestPayload = WorkerRenderBackendContract.EncodeRequest("cpu");
        WorkerMessage validRequest = new(WorkerRenderBackendContract.RequestKind, "request",
            WorkerProtocol.ComputeSha256(requestPayload), requestPayload);
        byte[] resultPayload = WorkerRenderBackendContract.EncodeResult("cpu", new(null, "fixture", null, null));
        WorkerMessage wrong = new(WorkerRenderBackendContract.ResultKind, validRequest.RequestId, validRequest.InputSha256,
            resultPayload, outputSha256: new string('0', 64));
        Assert.Equal(WorkerRenderBackendContract.InvalidCode,
            Assert.Throws<WorkerProtocolException>(() => WorkerRenderBackendContract.DecodeResult(validRequest, wrong, "cpu")).Code);
    }
}
