using System.Net;
using System.Net.Sockets;
using System.Text;
using Odyssey.Testing;
using Xunit;

namespace Odyssey.E2ETests;

/// <summary>
/// How the stack fixtures classify what they see (issue #257). Plain unit tests over a stub handler:
/// they sit outside <see cref="StackCollection"/>, so they run with no stack and no browser.
/// </summary>
public sealed class StackProbeTests
{
    private static readonly Uri Url = new("http://localhost:5199/");
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(5);

    // The TCP half of the probe, stubbed so no test touches a real port.
    private static readonly Func<Uri, Task<string?>> Accepts = _ => Task.FromResult<string?>(null);
    private static readonly Func<Uri, Task<string?>> Refuses = _ => Task.FromResult<string?>("ConnectionRefused");

    [Fact]
    public void Connection_refused_and_name_resolution_mean_not_listening()
    {
        Assert.True(StackProbe.IsNotListening(new HttpRequestException(
            HttpRequestError.ConnectionError, "refused", new SocketException((int)SocketError.ConnectionRefused))));
        Assert.True(StackProbe.IsNotListening(new HttpRequestException(HttpRequestError.NameResolutionError, "nx")));
        Assert.True(StackProbe.IsNotListening(new HttpRequestException(
            "refused", new SocketException((int)SocketError.ConnectionRefused))));
    }

    [Fact]
    public void Something_that_answered_and_misbehaved_is_not_absent()
    {
        Assert.False(StackProbe.IsNotListening(new HttpRequestException(HttpRequestError.ResponseEnded, "ended")));
        Assert.False(StackProbe.IsNotListening(new HttpRequestException(HttpRequestError.InvalidResponse, "garbage")));
        Assert.False(StackProbe.IsNotListening(new InvalidOperationException()));
        // An HTTP timeout happens after the TCP connect was accepted: hung, not absent.
        Assert.False(StackProbe.IsNotListening(new TaskCanceledException("HttpClient.Timeout elapsed")));
    }

    [Fact]
    public async Task Nothing_listening_until_the_deadline_is_a_missing_prerequisite()
    {
        var httpCalls = 0;
        using var client = Client(_ =>
        {
            httpCalls++;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var outcome = await StackProbe.PollAsync(client, Url, Short, StackProbe.SuccessStatus, Tick, Refuses);

        Assert.Equal(TierProbe.PrerequisiteMissing, outcome.Probe);
        Assert.Contains("ConnectionRefused", outcome.Detail);
        Assert.Equal(0, httpCalls);
    }

    [Fact]
    public async Task A_refused_request_after_a_connect_race_is_still_not_listening()
    {
        using var client = Client(_ => throw new HttpRequestException(
            HttpRequestError.ConnectionError, "refused", new SocketException((int)SocketError.ConnectionRefused)));

        var outcome = await StackProbe.PollAsync(client, Url, Short, StackProbe.SuccessStatus, Tick, Accepts);

        Assert.Equal(TierProbe.PrerequisiteMissing, outcome.Probe);
    }

    [Fact]
    public async Task Connected_but_no_http_response_is_a_fault_not_a_skip()
    {
        using var client = Client(_ => throw new TaskCanceledException("The request was canceled due to HttpClient.Timeout"));

        var outcome = await StackProbe.PollAsync(client, Url, Short, StackProbe.SuccessStatus, Tick, Accepts);

        Assert.Equal(TierProbe.Faulted, outcome.Probe);
        Assert.Contains("no HTTP response", outcome.Detail);
    }

    [Fact]
    public async Task A_real_closed_port_is_not_listening()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        Assert.NotNull(await StackProbe.TryConnectAsync(new Uri($"http://127.0.0.1:{port}/")));
    }

    [Fact]
    public async Task A_real_open_port_accepts()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        Assert.Null(await StackProbe.TryConnectAsync(new Uri($"http://127.0.0.1:{port}/")));
        listener.Stop();
    }

    [Fact]
    public async Task Answering_but_never_ready_is_a_fault_not_a_skip()
    {
        using var client = Client(_ => new HttpResponseMessage(HttpStatusCode.BadGateway));

        var outcome = await StackProbe.PollAsync(client, Url, Short, StackProbe.SuccessStatus, Tick, Accepts);

        Assert.Equal(TierProbe.Faulted, outcome.Probe);
        Assert.Contains("502", outcome.Detail);
    }

    [Fact]
    public async Task A_stack_that_comes_up_during_the_window_is_ready()
    {
        var calls = 0;
        using var client = Client(_ => ++calls < 3
            ? throw new HttpRequestException(HttpRequestError.ConnectionError, "refused")
            : new HttpResponseMessage(HttpStatusCode.OK));

        var outcome = await StackProbe.PollAsync(client, Url, TimeSpan.FromSeconds(5), StackProbe.SuccessStatus, Tick, Accepts);

        Assert.Equal(TierProbe.Ready, outcome.Probe);
    }

    [Fact]
    public async Task A_fatal_answer_stops_polling_at_once()
    {
        var calls = 0;
        using var client = Client(_ =>
        {
            calls++;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var outcome = await StackProbe.PollAsync(
            client, Url, TimeSpan.FromSeconds(5), _ => Task.FromResult((ProbeStep.Fatal, "nope")), Tick, Accepts);

        Assert.Equal(TierProbe.Faulted, outcome.Probe);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Ready_timeout_is_short_unless_required_or_managed_and_overridable()
    {
        Assert.Equal(StackProbe.QuickTimeout, StackProbe.ResolveReadyTimeout(managed: false, required: false, null));
        Assert.Equal(StackProbe.RequiredTimeout, StackProbe.ResolveReadyTimeout(managed: false, required: true, null));
        Assert.Equal(StackProbe.ManagedTimeout, StackProbe.ResolveReadyTimeout(managed: true, required: true, ""));
        Assert.Equal(TimeSpan.FromSeconds(300), StackProbe.ResolveReadyTimeout(managed: false, required: false, "300"));
        Assert.Throws<TestTierUnavailableException>(() => StackProbe.ResolveReadyTimeout(false, false, "soon"));
        Assert.Throws<TestTierUnavailableException>(() => StackProbe.ResolveReadyTimeout(false, false, "0"));
    }

    [Fact]
    public void Same_origin_api_probe_tells_proxy_from_spa_fallback_and_retries_a_starting_api()
    {
        Assert.Equal(StackFixture.SameOriginApi.Proxied, StackFixture.ClassifySameOriginApi(Json(HttpStatusCode.OK), out _));
        Assert.Equal(StackFixture.SameOriginApi.Absent, StackFixture.ClassifySameOriginApi(Html(), out var htmlDetail));
        Assert.Contains("SPA fallback", htmlDetail);
        Assert.Null(StackFixture.ClassifySameOriginApi(new HttpResponseMessage(HttpStatusCode.BadGateway), out _));
        // NGINX's own 502 page is text/html too: an API still migrating must be retried, never read as
        // "no proxy on this origin" (which would send a healthy-but-starting Compose stack to the preflight).
        Assert.Null(StackFixture.ClassifySameOriginApi(Html(HttpStatusCode.BadGateway), out _));
        Assert.Null(StackFixture.ClassifySameOriginApi(Html(HttpStatusCode.ServiceUnavailable), out _));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "text/plain")]
    [InlineData(HttpStatusCode.NotFound, null)]
    [InlineData(HttpStatusCode.NotFound, "text/html")]
    [InlineData(HttpStatusCode.MethodNotAllowed, "text/plain")]
    public void A_host_with_no_api_route_is_absent_not_retried(HttpStatusCode status, string? mediaType)
    {
        var response = new HttpResponseMessage(status);
        if (mediaType is not null)
        {
            response.Content = new StringContent("", Encoding.UTF8, mediaType);
        }

        Assert.Equal(StackFixture.SameOriginApi.Absent, StackFixture.ClassifySameOriginApi(response, out _));
    }

    [Fact]
    public async Task Accepted_then_broken_exchange_is_a_fault()
    {
        using var client = Client(_ => throw new HttpRequestException(HttpRequestError.ResponseEnded, "The response ended prematurely."));

        var outcome = await StackProbe.PollAsync(client, Url, Short, StackProbe.SuccessStatus, Tick, Accepts);

        Assert.Equal(TierProbe.Faulted, outcome.Probe);
        Assert.Contains("exchange failed", outcome.Detail);
    }

    [Theory]
    [InlineData("http://localhost:5199/api/manage/info", 200, "text/html; charset=utf-8", true)]
    // A Debug client under Aspire calls the API origin directly — never flagged.
    [InlineData("http://localhost:5188/manage/info", 200, "text/html", false)]
    [InlineData("http://localhost:5199/api/manage/info", 200, "application/json", false)]
    // NGINX's 502 error page while the API migrates is HTML but not the SPA fallback.
    [InlineData("http://localhost:5199/api/manage/info", 502, "text/html", false)]
    // The SPA's own pages and assets are HTML/JS on the same origin — not API calls.
    [InlineData("http://localhost:5199/login", 200, "text/html", false)]
    [InlineData("http://localhost:5199/apiary", 200, "text/html", false)]
    public void Release_under_aspire_signature_is_an_app_api_call_answered_with_html(
        string url, int status, string contentType, bool expected)
    {
        Assert.Equal(expected, StackFixture.IsSameOriginApiHtml("http://localhost:5199", url, status, contentType));
    }

    private static HttpClient Client(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new(new StubHandler(respond));

    private static HttpResponseMessage Json(HttpStatusCode status) =>
        new(status) { Content = new StringContent("{\"status\":\"ok\"}", Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Html(HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent("<!DOCTYPE html>", Encoding.UTF8, "text/html") };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
