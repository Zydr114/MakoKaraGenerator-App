using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using MakoKaraGenerator.Backend.Contracts;

namespace MakoKaraGenerator.Backend.Server.Tests;

public sealed partial class PreviewTransportTests
{
    [Fact]
    public async Task 同一Server四十次真正新登记按过期配额回收旧access与resume均拒绝()
    {
        await using Fixture fixture = await Fixture.Create(leaseSeconds: 2, emptyClient: true);
        ClientRegistrationResponse previous = fixture.Client;
        for (int index = 0; index < 40; index++)
        {
            fixture.Clock.Advance(3);
            using HttpResponseMessage response = await Register(fixture, new());
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            ClientRegistrationResponse next = (await response.Content.ReadFromJsonAsync<ClientRegistrationResponse>(Json))!;
            Assert.NotEqual(previous.ClientId, next.ClientId);
            using HttpRequestMessage oldAccess = new(HttpMethod.Get, $"/api/v1/clients/{previous.ClientId}/state");
            oldAccess.Headers.Authorization = new AuthenticationHeaderValue("Bearer", previous.AccessToken);
            oldAccess.Headers.Add("X-Mako-Client-Id", previous.ClientId);
            using HttpResponseMessage rejected = await fixture.Http.SendAsync(oldAccess);
            await Error(rejected, HttpStatusCode.Unauthorized, BackendErrorCode.UnauthorizedClient);
            using HttpResponseMessage resume = await Register(fixture,
                new(ResumeClientId: previous.ClientId, ResumeToken: previous.ResumeToken));
            await Error(resume, HttpStatusCode.Unauthorized, BackendErrorCode.UnauthorizedClient);
            previous = next;
        }
    }

    [Fact]
    public async Task 实际SSE连接在租约过期时保护client断开释放后才可回收()
    {
        await using Fixture fixture = await Fixture.Create(leaseSeconds: 2, emptyClient: true);
        fixture.Projects.PublishPreviewEvent(fixture.Client.ClientId, new("event-probe", "0", "0", "closed", "0", null, null));
        using CancellationTokenSource connection = new();
        using HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/events");
        using HttpResponseMessage events = await fixture.Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, connection.Token);
        Assert.Equal(HttpStatusCode.OK, events.StatusCode);
        Assert.Equal("text/event-stream", events.Content.Headers.ContentType!.MediaType);
        fixture.Clock.Advance(3);
        using (HttpResponseMessage blocked = await Register(fixture, new()))
            await Error(blocked, HttpStatusCode.TooManyRequests, BackendErrorCode.ClientQuotaExceeded);
        connection.Cancel();
        events.Dispose();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(8));
        while (true)
        {
            timeout.Token.ThrowIfCancellationRequested();
            using HttpResponseMessage retry = await Register(fixture, new());
            if (retry.StatusCode == HttpStatusCode.Created) break;
            await Error(retry, HttpStatusCode.TooManyRequests, BackendErrorCode.ClientQuotaExceeded);
            await Task.Delay(10, timeout.Token);
        }
        Assert.False(fixture.Clients.TryReadLease(fixture.Client.ClientId, out _, out _));
    }

    private static Task<HttpResponseMessage> Register(Fixture fixture, ClientRegistrationRequest registration)
    {
        HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/clients")
        { Content = JsonContent.Create(registration, options: Json) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", fixture._security.BootstrapToken);
        return Send();
        async Task<HttpResponseMessage> Send()
        { using (request) return await fixture.Http.SendAsync(request); }
    }
}
