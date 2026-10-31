using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Hosting;
using MakoKaraGenerator.Core.Persistence;

namespace MakoKaraGenerator.Backend.Server.Tests;

public sealed partial class PreviewTransportTests
{
    [Fact]
    public async Task 分析HTTP回执断连任务继续且规范数字结果RangeHEAD缓存跨client拒绝()
    {
        await using Fixture f = await Fixture.Create(analysisFixture: true);
        AnalysisTaskCapabilitiesDto cap = (await f.Http.GetFromJsonAsync<AnalysisTaskCapabilitiesDto>("/api/v1/analysis-capabilities", Json))!;
        Assert.True(cap.Enabled); Assert.True(cap.GuardAvailable); Assert.True(cap.ToolStartupVerified); Assert.Equal("14400000", cap.MaxSamples);
        AnalysisJobRequest input = new("0", f.Metadata()); string route = $"/api/v1/project-sessions/{f.Project.ProjectSessionId}/analysis-jobs";
        using CancellationTokenSource disconnected = new(); using HttpResponseMessage accepted = await f.Http.PostAsJsonAsync(route, input, Json, disconnected.Token);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode); JobResponse job = (await accepted.Content.ReadFromJsonAsync<JobResponse>(Json))!;
        disconnected.Cancel(); await f.Analyzer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(8)); Assert.False(f.Analyzer.Token.IsCancellationRequested);
        using HttpResponseMessage repeated = await f.Http.PostAsJsonAsync(route, input, Json); Assert.Equal(job.JobId, (await repeated.Content.ReadFromJsonAsync<JobResponse>(Json))!.JobId);
        f.Analyzer.Release.TrySetResult(); await WaitUntil(() => f.Projects.ReadJob(f.Client.ClientId, job.JobId).Response!.State == "succeeded");
        string content = $"/api/v1/jobs/{job.JobId}/analysis-result";
        using HttpResponseMessage report = await f.Http.GetAsync(content); byte[] bytes = await report.Content.ReadAsByteArrayAsync(); Assert.Equal(HttpStatusCode.OK, report.StatusCode);
        Assert.Equal(f.Analyzer.Canonical, bytes); Assert.Equal("application/json", report.Content.Headers.ContentType!.MediaType); Assert.NotNull(report.Content.Headers.ContentDisposition);
        using JsonDocument canonical = JsonDocument.Parse(bytes); Assert.Equal(JsonValueKind.Number, canonical.RootElement.GetProperty("sampleCount").ValueKind);
        Assert.Equal(384, canonical.RootElement.GetProperty("sampleCount").GetInt64());
        JobResponse done = f.Projects.ReadJob(f.Client.ClientId, job.JobId).Response!; Assert.Equal("384", done.AnalysisResult!.SampleCount); Assert.Null(done.Progress);
        Assert.DoesNotContain(f.Analyzer.Path!, JsonSerializer.Serialize(done, Json), StringComparison.Ordinal);
        using HttpRequestMessage headInput = new(HttpMethod.Head, content); using HttpResponseMessage head = await f.Http.SendAsync(headInput);
        Assert.Equal(HttpStatusCode.OK, head.StatusCode); Assert.Equal(bytes.Length, head.Content.Headers.ContentLength); Assert.Empty(await head.Content.ReadAsByteArrayAsync());
        using HttpRequestMessage rangeInput = new(HttpMethod.Get, content); rangeInput.Headers.Range = new RangeHeaderValue(2, 8); using HttpResponseMessage range = await f.Http.SendAsync(rangeInput);
        Assert.Equal(HttpStatusCode.PartialContent, range.StatusCode); Assert.Equal(bytes[2..9], await range.Content.ReadAsByteArrayAsync());
        using HttpRequestMessage cachedInput = new(HttpMethod.Get, content); cachedInput.Headers.TryAddWithoutValidation("If-None-Match", report.Headers.ETag!.ToString());
        using HttpResponseMessage cached = await f.Http.SendAsync(cachedInput); Assert.Equal(HttpStatusCode.NotModified, cached.StatusCode);
        ClientRegistrationResponse other = f.Clients.Register(f._security.BootstrapToken, new()).Response!;
        using HttpRequestMessage foreignInput = new(HttpMethod.Get, content); foreignInput.Headers.Authorization = new AuthenticationHeaderValue("Bearer", other.AccessToken); foreignInput.Headers.Add("X-Mako-Client-Id", other.ClientId);
        using HttpResponseMessage foreign = await f.Http.SendAsync(foreignInput); await Error(foreign, HttpStatusCode.NotFound, BackendErrorCode.JobNotFound);
        Assert.Equal("0", (await f.Projects.ReadSummaryAsync(f.Client.ClientId, f.Project.ProjectSessionId))!.ProjectRevision); Assert.Equal(1, f.Analyzer.Calls);
    }
    [Fact]
    public async Task 分析HTTP诚实不可用与精确revision拒绝且不接受裸路径()
    {
        await using Fixture disabled = await Fixture.Create();
        AnalysisTaskCapabilitiesDto cap = (await disabled.Http.GetFromJsonAsync<AnalysisTaskCapabilitiesDto>("/api/v1/analysis-capabilities", Json))!; Assert.False(cap.Enabled);
        using HttpResponseMessage unavailable = await disabled.Http.PostAsJsonAsync($"/api/v1/project-sessions/{disabled.Project.ProjectSessionId}/analysis-jobs", new AnalysisJobRequest("0", disabled.Metadata()), Json);
        await Error(unavailable, HttpStatusCode.ServiceUnavailable, BackendErrorCode.TaskUnavailable);
        await using Fixture f = await Fixture.Create(analysisFixture: true); string route = $"/api/v1/project-sessions/{f.Project.ProjectSessionId}/analysis-jobs";
        using HttpResponseMessage stale = await f.Http.PostAsJsonAsync(route, new AnalysisJobRequest("1", f.Metadata()), Json); await Error(stale, HttpStatusCode.Conflict, BackendErrorCode.RevisionConflict);
        using HttpResponseMessage path = await f.Http.PostAsJsonAsync(route, new { expectedRevision = "0", path = "/tmp/audio.wav", metadata = f.Metadata() }, Json); await Error(path, HttpStatusCode.BadRequest, BackendErrorCode.InvalidRequest);
        Assert.Equal(0, f.Analyzer.Calls);
    }
    private static byte[] AnalysisWave()
    {
        using MemoryStream stream = new(); using BinaryWriter writer = new(stream);
        writer.Write("RIFF"u8); writer.Write(164); writer.Write("WAVEfmt "u8); writer.Write(16); writer.Write((short)1); writer.Write((short)1);
        writer.Write(8000); writer.Write(16000); writer.Write((short)2); writer.Write((short)16); writer.Write("data"u8); writer.Write(128); writer.Write(new byte[128]); return stream.ToArray();
    }
    private sealed class AnalysisExecutor : IBackendAnalysisExecutor
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously); internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CancellationToken Token; internal int Calls; internal byte[]? Canonical; internal string? Path; internal string? AudioProfile;
        public void ValidateInput(BackendAudioAnalysisGrant grant) { }
        public Task<BackendAnalysisCapabilities> ProbeAsync(CancellationToken cancellationToken = default) => Task.FromResult(new BackendAnalysisCapabilities(true, true));
        public async Task<byte[]> ExecuteAsync(BackendAudioAnalysisGrant grant, IProgress<BackendAnalysisProgressDto>? progress = null, CancellationToken cancellationToken = default)
        {
            Calls++; Token = cancellationToken; Path = grant.Path; AudioProfile = grant.AudioProfile; Entered.TrySetResult(); progress?.Report(new("decoding", "384", null, "8000")); await Release.Task.WaitAsync(cancellationToken);
            Canonical = AudioAnalysisJson.Encode(new(grant.Sha256, new string('b', 64), 384, [new(0, .1234567890123456, .5, .25, .8)])); return Canonical;
        }
    }
}
