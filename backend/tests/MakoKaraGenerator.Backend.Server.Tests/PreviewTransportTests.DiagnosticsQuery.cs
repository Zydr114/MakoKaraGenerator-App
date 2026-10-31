using System.Net;
using System.Net.Http.Json;
using MakoKaraGenerator.Backend.Contracts;

namespace MakoKaraGenerator.Backend.Server.Tests;

public sealed partial class PreviewTransportTests
{
    [Theory]
    [InlineData("cursor=a&cursor=b&limit=invalid", null, "分页参数只能出现一次。")]
    [InlineData("limit=1&limit=2", null, "分页参数只能出现一次。")]
    [InlineData("limit=", "limit", "诊断页大小必须为整数。")]
    [InlineData("limit=%2B1", "limit", "诊断页大小必须为整数。")]
    [InlineData("limit=%201", "limit", "诊断页大小必须为整数。")]
    [InlineData("limit=1%20", "limit", "诊断页大小必须为整数。")]
    [InlineData("limit=-1", "limit", "诊断页大小必须为整数。")]
    [InlineData("limit=1.0", "limit", "诊断页大小必须为整数。")]
    [InlineData("limit=2147483648", "limit", "诊断页大小必须为整数。")]
    public async Task 两种任务诊断页保持重复参数优先与严格整数错误(string query, string? field, string message)
    {
        await using Fixture fixture = await Fixture.Create();
        foreach (string kind in new[] { "validation", "export" })
        {
            using HttpResponseMessage response = await fixture.Http.GetAsync($"/api/v1/jobs/missing/{kind}-diagnostics?{query}");
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            BackendErrorResponse error = (await response.Content.ReadFromJsonAsync<BackendErrorResponse>(Json))!;
            Assert.Equal(BackendErrorCode.InvalidRequest, error.Code);
            Assert.Equal(field, error.Field); Assert.Equal(message, error.Message);
        }
    }

    [Theory]
    [InlineData("limit=1")]
    [InlineData("limit=0001")]
    [InlineData("future=value")]
    [InlineData("future=one&future=two&limit=1")]
    public async Task 两种任务诊断页保留整数与未知查询参数原策略(string query)
    {
        await using Fixture fixture = await Fixture.Create();
        foreach (string kind in new[] { "validation", "export" })
        {
            string route = $"/api/v1/jobs/missing/{kind}-diagnostics";
            using HttpResponseMessage baseline = await fixture.Http.GetAsync(route);
            using HttpResponseMessage response = await fixture.Http.GetAsync(route + "?" + query);
            Assert.Equal(HttpStatusCode.NotFound, baseline.StatusCode);
            Assert.Equal(baseline.StatusCode, response.StatusCode);
            Assert.Equal(await baseline.Content.ReadAsStringAsync(), await response.Content.ReadAsStringAsync());
        }
    }
}
