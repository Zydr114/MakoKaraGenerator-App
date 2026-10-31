using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Hosting;
using MakoKaraGenerator.Media;

namespace MakoKaraGenerator.Backend.Server.Tests;

public sealed partial class PreviewTransportTests
{
    [Fact]
    public async Task WaveformHTTP完整窗口保持峰值位模式且HEAD与条件读取沿安全响应头()
    {
        LoadedWaveform loaded = HttpWaveform();
        await using Fixture f = await Fixture.Create(enabled: false, waveformOverride: loaded);
        string route = $"/api/v1/project-sessions/{f.Project.ProjectSessionId}/waveform";
        ProjectWaveformResponse metadata = (await f.Http.GetFromJsonAsync<ProjectWaveformResponse>(route, Json))!;
        Assert.True(metadata.Available); Assert.Equal(2, metadata.SourceChannels); Assert.Equal(1, metadata.WaveformChannels);
        Assert.Equal("524289", metadata.SampleCount); Assert.Equal("65536125", metadata.DurationUs);
        Assert.Equal(loaded.Peaks.LevelCount, metadata.Levels!.Length);
        string window = $"{route}/peaks?waveformId={metadata.WaveformId}&level=0&start=0&count=8192";
        string origin = f.Http.BaseAddress!.GetLeftPart(UriPartial.Authority);
        using HttpRequestMessage request = new(HttpMethod.Get, window); request.Headers.Add("Origin", origin);
        using HttpResponseMessage response = await f.Http.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/octet-stream", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal(65536, response.Content.Headers.ContentLength);
        Assert.Equal(WaveformBytes(loaded.Peaks, 0, 0, 8192), await response.Content.ReadAsByteArrayAsync());
        string etag = response.Headers.ETag!.ToString();
        AssertWaveformSecurityHeaders(response, origin);
        Assert.False(response.Headers.Contains("Accept-Ranges")); Assert.Null(response.Content.Headers.ContentRange);
        using HttpRequestMessage headRequest = new(HttpMethod.Head, window); headRequest.Headers.Add("Origin", origin);
        using HttpResponseMessage head = await f.Http.SendAsync(headRequest);
        Assert.Equal(HttpStatusCode.OK, head.StatusCode); Assert.Equal(65536, head.Content.Headers.ContentLength);
        Assert.Equal(etag, head.Headers.ETag!.ToString()); Assert.Empty(await head.Content.ReadAsByteArrayAsync());
        AssertWaveformSecurityHeaders(head, origin);
        foreach (HttpMethod method in new[] { HttpMethod.Get, HttpMethod.Head })
        {
            using HttpRequestMessage conditional = new(method, window);
            conditional.Headers.Add("Origin", origin);
            conditional.Headers.TryAddWithoutValidation("If-None-Match", $"\"unrelated\", W/{etag}");
            using HttpResponseMessage cached = await f.Http.SendAsync(conditional);
            Assert.Equal(HttpStatusCode.NotModified, cached.StatusCode); Assert.Empty(await cached.Content.ReadAsByteArrayAsync());
            Assert.Equal(etag, cached.Headers.ETag!.ToString()); AssertWaveformSecurityHeaders(cached, origin);
        }
        using HttpResponseMessage tail = await f.Http.GetAsync($"{route}/peaks?waveformId={metadata.WaveformId}&level=0&start=8192&count=1");
        Assert.Equal(WaveformBytes(loaded.Peaks, 0, 8192, 1), await tail.Content.ReadAsByteArrayAsync());
        Assert.NotEqual(etag, tail.Headers.ETag!.ToString());
        foreach (WaveformLevelDto level in metadata.Levels)
        {
            int count = Math.Min(level.BucketCount, 8192);
            using HttpResponseMessage current = await f.Http.GetAsync($"{route}/peaks?waveformId={metadata.WaveformId}&level={level.Level}&start=0&count={count}");
            Assert.Equal(HttpStatusCode.OK, current.StatusCode);
            Assert.Equal(WaveformBytes(loaded.Peaks, level.Level, 0, count), await current.Content.ReadAsByteArrayAsync());
        }
    }

    [Fact]
    public async Task WaveformHTTP严格查询拒绝Range且条件请求仍先认证及验证来源()
    {
        await using Fixture f = await Fixture.Create();
        string route = $"/api/v1/project-sessions/{f.Project.ProjectSessionId}/waveform";
        ProjectWaveformResponse metadata = (await f.Http.GetFromJsonAsync<ProjectWaveformResponse>(route, Json))!;
        string query = $"waveformId={metadata.WaveformId}&level=0&start=0&count=1";
        using HttpResponseMessage original = await f.Http.GetAsync($"{route}/peaks?{query}");
        string etag = original.Headers.ETag!.ToString();
        (string Query, string Field)[] invalid =
        [
            (query + "&other=1", "other"), (query + "&level=0", "level"), (query + "&waveformId=x", "waveformId"),
            (query.Replace("level=0", "level=-1", StringComparison.Ordinal), "level"),
            (query.Replace("start=0", "start=+1", StringComparison.Ordinal), "start"),
            (query.Replace("start=0", "start=2147483648", StringComparison.Ordinal), "start"),
            (query.Replace("count=1", "count=0", StringComparison.Ordinal), "count"),
            (query.Replace("count=1", "count=8193", StringComparison.Ordinal), "count"),
            (query.Replace("start=0", "start=375", StringComparison.Ordinal), "start"),
            (query.Replace("level=0", "level=100", StringComparison.Ordinal), "level"),
            ("waveformId=x&level=0&start=0", "count"), ("level=0&start=0&count=1", "waveformId")
        ];
        foreach ((string suffix, string field) in invalid)
        {
            using HttpResponseMessage rejected = await f.Http.GetAsync($"{route}/peaks?{suffix}");
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
            BackendErrorResponse error = (await rejected.Content.ReadFromJsonAsync<BackendErrorResponse>(Json))!;
            Assert.Equal(BackendErrorCode.InvalidRequest, error.Code); Assert.Equal(field, error.Field);
        }
        using HttpResponseMessage revision = await f.Http.GetAsync(route + "?expectedRevision=0");
        Assert.Equal("expectedRevision", (await revision.Content.ReadFromJsonAsync<BackendErrorResponse>(Json))!.Field);
        foreach (HttpMethod method in new[] { HttpMethod.Get, HttpMethod.Head })
        {
            using HttpRequestMessage range = new(method, $"{route}/peaks?{query}"); range.Headers.Range = new RangeHeaderValue(0, 7);
            using HttpResponseMessage rejected = await f.Http.SendAsync(range);
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode); Assert.Null(rejected.Content.Headers.ContentRange);
            Assert.False(rejected.Headers.Contains("Accept-Ranges"));
            if (method == HttpMethod.Head) Assert.Empty(await rejected.Content.ReadAsByteArrayAsync());
            else Assert.Equal("Range", (await rejected.Content.ReadFromJsonAsync<BackendErrorResponse>(Json))!.Field);
        }
        using HttpResponseMessage stale = await f.Http.GetAsync($"{route}/peaks?{query.Replace(metadata.WaveformId!, "stale", StringComparison.Ordinal)}");
        await Error(stale, HttpStatusCode.NotFound, BackendErrorCode.ResourceNotFound);
        ClientRegistrationResponse other = f.Clients.Register(f._security.BootstrapToken, new()).Response!;
        using HttpRequestMessage foreign = new(HttpMethod.Get, $"{route}/peaks?{query}");
        foreign.Headers.Authorization = new AuthenticationHeaderValue("Bearer", other.AccessToken);
        foreign.Headers.Add("X-Mako-Client-Id", other.ClientId); foreign.Headers.TryAddWithoutValidation("If-None-Match", etag);
        using HttpResponseMessage refused = await f.Http.SendAsync(foreign); await Error(refused, HttpStatusCode.NotFound, BackendErrorCode.SessionNotFound);
        using HttpRequestMessage unauthenticated = new(HttpMethod.Get, $"{route}/peaks?{query}");
        unauthenticated.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "invalid");
        unauthenticated.Headers.TryAddWithoutValidation("If-None-Match", "*");
        using HttpResponseMessage unauthorized = await f.Http.SendAsync(unauthenticated);
        await Error(unauthorized, HttpStatusCode.Unauthorized, BackendErrorCode.UnauthorizedClient);
    }

    [Fact]
    public async Task WaveformHTTP预算不足仍创建成功且关闭后元数据与峰值均不可读()
    {
        await using Fixture f = await Fixture.Create(waveformCache: new(0));
        string route = $"/api/v1/project-sessions/{f.Project.ProjectSessionId}/waveform";
        using HttpResponseMessage response = await f.Http.GetAsync(route);
        ProjectWaveformResponse metadata = (await response.Content.ReadFromJsonAsync<ProjectWaveformResponse>(Json))!;
        Assert.False(metadata.Available); Assert.Equal("cacheBudgetExceeded", metadata.Reason); Assert.Null(metadata.Levels);
        using HttpResponseMessage missing = await f.Http.GetAsync(route + "/peaks?waveformId=none&level=0&start=0&count=1");
        await Error(missing, HttpStatusCode.NotFound, BackendErrorCode.ResourceNotFound);
        BackendOperationResult<JobResponse> close = await f.Projects.StartCloseAsync(f.Client.ClientId, f.Project.ProjectSessionId,
            new("0", "discard", Metadata: f.Metadata()));
        Assert.True(close.Accepted); await WaitUntil(() => f.Projects.ReadJob(f.Client.ClientId, close.Response!.JobId).Response!.State == "succeeded");
        using HttpResponseMessage closed = await f.Http.GetAsync(route); await Error(closed, HttpStatusCode.NotFound, BackendErrorCode.SessionNotFound);
        using HttpResponseMessage closedPeaks = await f.Http.GetAsync(route + "/peaks?waveformId=none&level=0&start=0&count=1");
        await Error(closedPeaks, HttpStatusCode.NotFound, BackendErrorCode.SessionNotFound);
    }

    private static void AssertWaveformSecurityHeaders(HttpResponseMessage response, string origin)
    {
        Assert.True(response.Headers.CacheControl!.NoStore);
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal(origin, Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
        string expose = Assert.Single(response.Headers.GetValues("Access-Control-Expose-Headers"));
        Assert.Contains("ETag", expose, StringComparison.Ordinal); Assert.Contains("Content-Length", expose, StringComparison.Ordinal);
    }

    private static LoadedWaveform HttpWaveform()
    {
        float[] minima = new float[8193], maxima = new float[8193];
        for (int i = 0; i < minima.Length; i++) { minima[i] = i % 2 == 0 ? -2.5f : -0f; maxima[i] = i % 3 == 0 ? 2.25f : 0f; }
        return new(new(65536125, 8000, 2, "pcm", "wav"), WaveformPeakPyramid.FromBaseBuckets(8000, 524289, 64, minima, maxima));
    }

    private static byte[] WaveformBytes(WaveformPeakPyramid peaks, int level, int start, int count)
    {
        byte[] expected = new byte[count * 8];
        for (int i = 0; i < count; i++)
        {
            PeakRange peak = peaks.GetBucketPeak(level, start + i);
            BinaryPrimitives.WriteSingleLittleEndian(expected.AsSpan(i * 8, 4), peak.Min);
            BinaryPrimitives.WriteSingleLittleEndian(expected.AsSpan(i * 8 + 4, 4), peak.Max);
        }
        return expected;
    }
}
