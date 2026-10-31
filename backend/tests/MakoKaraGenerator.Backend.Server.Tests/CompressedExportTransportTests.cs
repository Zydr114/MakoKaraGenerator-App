using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MakoKaraGenerator.Backend.Contracts;

namespace MakoKaraGenerator.Backend.Server.Tests;

public sealed partial class PreviewTransportTests
{
    [Theory]
    [InlineData(BackendAudioInputProfiles.Mp3)]
    [InlineData(BackendAudioInputProfiles.Flac)]
    public async Task 压缩导出HTTP按真实声明能力授格式并保唯一回执和登记报告(string profile)
    {
        await using Fixture f = await Fixture.Create(exportFixture: true, analysisBytes: CompressedCandidate(profile), compressedExport: true);
        ExportTaskCapabilitiesDto caps = (await f.Http.GetFromJsonAsync<ExportTaskCapabilitiesDto>("/api/v1/export-capabilities", Json))!;
        Assert.True(caps.Enabled); Assert.Equal(WorkerExportContract.ExecutionProfile, caps.ExecutionProfile);
        Assert.Contains(profile, caps.AudioProfiles); Assert.Equal([WorkerExportContract.ExecutionProfile, WorkerExportContract.CompressedExecutionProfile], caps.ExecutionProfiles!);
        Assert.Null(caps.MaxCompressedAudioDurationUs); Assert.Equal(WorkerExportContract.CompressedAudioDurationBasis, caps.CompressedAudioDurationBasis);
        string route = $"/api/v1/project-sessions/{f.Project.ProjectSessionId}/export-jobs";
        ExportJobRequest request = new("0", Metadata: f.Metadata());
        using HttpResponseMessage accepted = await f.Http.PostAsJsonAsync(route, request, Json); Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        JobResponse job = (await accepted.Content.ReadFromJsonAsync<JobResponse>(Json))!;
        Assert.Equal(profile, job.ExportInput!.Audio!.AudioProfile);
        Assert.Equal(WorkerExportContract.CompressedExecutionProfile, job.ExportInput.ExecutionProfile);
        using HttpResponseMessage retry = await f.Http.PostAsJsonAsync(route, request, Json); Assert.Equal(job.JobId, (await retry.Content.ReadFromJsonAsync<JobResponse>(Json))!.JobId);
        await f.Exporter.Entered.Task.WaitAsync(TimeSpan.FromSeconds(8)); Assert.Equal(profile, f.Exporter.Input!.Audio!.AudioProfile);
        f.Exporter.Release.TrySetResult(); await WaitUntil(() => f.Projects.ReadJob(f.Client.ClientId, job.JobId).Response!.State == "succeeded");
        using HttpResponseMessage report = await f.Http.GetAsync($"/api/v1/jobs/{job.JobId}/export-result"); Assert.Equal(HttpStatusCode.OK, report.StatusCode);
        ExportReportDto actual = (await report.Content.ReadFromJsonAsync<ExportReportDto>(Json))!;
        Assert.True(actual.AudioIncluded); Assert.True(actual.StrictlyVerified); Assert.Equal("3000000", actual.AudioDurationUs);
        Assert.Equal(1, f.Exporter.Calls);
    }

    [Fact]
    public async Task 旧HTTP受信接缝不能因强类型默认值虚报或接受压缩能力()
    {
        await using Fixture f = await Fixture.Create(exportFixture: true, analysisBytes: CompressedCandidate(BackendAudioInputProfiles.Mp3));
        ExportTaskCapabilitiesDto cap = (await f.Http.GetFromJsonAsync<ExportTaskCapabilitiesDto>("/api/v1/export-capabilities", Json))!;
        Assert.Equal([BackendAudioInputProfiles.Pcm16Wave], cap.AudioProfiles); Assert.Equal([WorkerExportContract.ExecutionProfile], cap.ExecutionProfiles!);
        using HttpResponseMessage refused = await f.Http.PostAsJsonAsync($"/api/v1/project-sessions/{f.Project.ProjectSessionId}/export-jobs",
            new ExportJobRequest("0", Metadata: f.Metadata()), Json);
        await Error(refused, HttpStatusCode.ServiceUnavailable, BackendErrorCode.TaskUnavailable); Assert.Equal(0, f.Exporter.Calls);
    }
}
