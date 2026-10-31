using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MakoKaraGenerator.Backend.Contracts;

namespace MakoKaraGenerator.Backend.Server.Tests;

public sealed partial class PreviewTransportTests
{
    [Theory]
    [InlineData(BackendAudioInputProfiles.Mp3)]
    [InlineData(BackendAudioInputProfiles.Flac)]
    public async Task 压缩分析HTTP公开实际格式未知时长完整结果读取回执与显式采用(string profile)
    {
        await using Fixture f = await Fixture.Create(analysisFixture: true, analysisBytes: CompressedCandidate(profile), compressedAnalysis: true);
        AnalysisTaskCapabilitiesDto cap = (await f.Http.GetFromJsonAsync<AnalysisTaskCapabilitiesDto>("/api/v1/analysis-capabilities", Json))!;
        Assert.True(cap.Enabled); Assert.Equal(BackendAudioInputProfiles.LegacyAnalysisSource, cap.SourceProfile);
        Assert.Contains(profile, cap.AudioProfiles); Assert.Contains(BackendAudioInputProfiles.CompressedAnalysisSource, cap.SourceProfiles!);
        string route = $"/api/v1/project-sessions/{f.Project.ProjectSessionId}/analysis-jobs"; AnalysisJobRequest input = new("0", f.Metadata());
        using HttpResponseMessage accepted = await f.Http.PostAsJsonAsync(route, input, Json); Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        using JsonDocument wire = JsonDocument.Parse(await accepted.Content.ReadAsByteArrayAsync());
        Assert.False(wire.RootElement.GetProperty("analysisInput").TryGetProperty("sourceDurationUs", out _));
        JobResponse job = (await accepted.Content.ReadFromJsonAsync<JobResponse>(Json))!;
        Assert.Null(job.AnalysisInput!.SourceDurationUs); Assert.Equal(profile, job.AnalysisInput.Audio.AudioProfile);
        Assert.Equal(BackendAudioInputProfiles.CompressedAnalysisSource, job.AnalysisInput.SourceProfile);
        using HttpResponseMessage retry = await f.Http.PostAsJsonAsync(route, input, Json); Assert.Equal(job.JobId, (await retry.Content.ReadFromJsonAsync<JobResponse>(Json))!.JobId);
        await f.Analyzer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(8)); Assert.Equal(profile, f.Analyzer.AudioProfile); f.Analyzer.Release.TrySetResult();
        await WaitUntil(() => f.Projects.ReadJob(f.Client.ClientId, job.JobId).Response!.State == "succeeded");
        string reportRoute = $"/api/v1/jobs/{job.JobId}/analysis-result";
        using HttpResponseMessage result = await f.Http.GetAsync(reportRoute); Assert.Equal(HttpStatusCode.OK, result.StatusCode); Assert.Equal(f.Analyzer.Canonical, await result.Content.ReadAsByteArrayAsync());
        using HttpRequestMessage rangeInput = new(HttpMethod.Get, reportRoute); rangeInput.Headers.Range = new RangeHeaderValue(1, 8);
        using HttpResponseMessage range = await f.Http.SendAsync(rangeInput); Assert.Equal(HttpStatusCode.PartialContent, range.StatusCode); Assert.Equal(f.Analyzer.Canonical![1..9], await range.Content.ReadAsByteArrayAsync());
        string commandRoute = $"/api/v1/project-sessions/{f.Project.ProjectSessionId}/commands";
        using HttpResponseMessage apply = await f.Http.PostAsJsonAsync(commandRoute, AnalysisCommand(f, "0", new("audio.analysis.apply", AnalysisJobId: job.JobId)), Json);
        Assert.Equal(HttpStatusCode.OK, apply.StatusCode); Assert.True((await apply.Content.ReadFromJsonAsync<ProjectCommandResponse>(Json))!.Changed);
        ProjectSessionSummaryResponse summary = (await f.Http.GetFromJsonAsync<ProjectSessionSummaryResponse>($"/api/v1/project-sessions/{f.Project.ProjectSessionId}", Json))!;
        Assert.Equal(job.AnalysisInput.Audio.Sha256, summary.AudioAnalysis!.SourceHash); Assert.Equal("1", summary.ProjectRevision);
        using HttpResponseMessage clear = await f.Http.PostAsJsonAsync(commandRoute, AnalysisCommand(f, "1", new("audio.analysis.clear")), Json); Assert.Equal(HttpStatusCode.OK, clear.StatusCode);
        using HttpResponseMessage undo = await f.Http.PostAsJsonAsync(commandRoute, AnalysisCommand(f, "2", new("history.undo")), Json); Assert.Equal(HttpStatusCode.OK, undo.StatusCode);
        Assert.Equal(summary.AudioAnalysis, (await f.Http.GetFromJsonAsync<ProjectSessionSummaryResponse>($"/api/v1/project-sessions/{f.Project.ProjectSessionId}", Json))!.AudioAnalysis);
        Assert.Equal(1, f.Analyzer.Calls);
    }

    [Fact]
    public async Task 旧受控WAV能力拒绝压缩候选且HTTP能力不虚报新格式()
    {
        await using Fixture f = await Fixture.Create(analysisFixture: true, analysisBytes: CompressedCandidate(BackendAudioInputProfiles.Mp3));
        AnalysisTaskCapabilitiesDto cap = (await f.Http.GetFromJsonAsync<AnalysisTaskCapabilitiesDto>("/api/v1/analysis-capabilities", Json))!;
        Assert.Equal([BackendAudioInputProfiles.Pcm16Wave], cap.AudioProfiles); Assert.Equal([BackendAudioInputProfiles.LegacyAnalysisSource], cap.SourceProfiles!);
        using HttpResponseMessage rejected = await f.Http.PostAsJsonAsync($"/api/v1/project-sessions/{f.Project.ProjectSessionId}/analysis-jobs", new AnalysisJobRequest("0", f.Metadata()), Json);
        await Error(rejected, HttpStatusCode.ServiceUnavailable, BackendErrorCode.TaskUnavailable); Assert.Equal(0, f.Analyzer.Calls);
    }

    private static byte[] CompressedCandidate(string profile)
    {
        if (profile == BackendAudioInputProfiles.Mp3)
        { byte[] frame = new byte[417]; frame[0] = 255; frame[1] = 251; frame[2] = 144; return frame; }
        byte[] flac = new byte[43]; "fLaC"u8.CopyTo(flac); flac[4] = 128; flac[7] = 34;
        BinaryPrimitives.WriteUInt16BigEndian(flac.AsSpan(8), 16); BinaryPrimitives.WriteUInt16BigEndian(flac.AsSpan(10), 4096);
        BinaryPrimitives.WriteUInt64BigEndian(flac.AsSpan(18), 48000UL << 44 | 1UL << 41 | 15UL << 36); return flac;
    }
}
