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

    [Fact]
    public void Connection_refused_name_resolution_and_timeouts_mean_not_listening()
    {
        Assert.True(StackProbe.IsNotListening(new HttpRequestException(
            HttpRequestError.ConnectionError, "refused", new SocketException((int)SocketError.ConnectionRefused))));
        Assert.True(StackProbe.IsNotListening(new HttpRequestException(HttpRequestError.NameResolutionError, "nx")));
        Assert.True(StackProbe.IsNotListening(new HttpRequestException(
            "refused", new SocketException((int)SocketError.ConnectionRefused))));
        Assert.True(StackProbe.IsNotListening(new TaskCanceledException("HttpClient.Timeout elapsed")));
    }

    [Fact]
    public void Something_that_answered_and_misbehaved_is_not_absent()
    {
        Assert.False(StackProbe.IsNotListening(new HttpRequestException(HttpRequestError.ResponseEnded, "ended")));
        Assert.False(StackProbe.IsNotListening(new HttpRequestException(HttpRequestError.InvalidResponse, "garbage")));
        Assert.False(StackProbe.IsNotListening(new InvalidOperationException()));
    }

    [Fact]
    public async Task Nothing_listening_until_the_deadline_is_a_missing_prerequisite()
    {
        using var client = Client(_ => throw new HttpRequestException(
            HttpRequestError.ConnectionError, "refused", new SocketException((int)SocketError.ConnectionRefused)));

        var outcome = await StackProbe.PollAsync(client, Url, Short, StackProbe.SuccessStatus, Tick);

        Assert.Equal(TierProbe.PrerequisiteMissing, outcome.Probe);
    }

    [Fact]
    public async Task Answering_but_never_ready_is_a_fault_not_a_skip()
    {
        using var client = Client(_ => new HttpResponseMessage(HttpStatusCode.BadGateway));

        var outcome = await StackProbe.PollAsync(client, Url, Short, StackProbe.SuccessStatus, Tick);

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

        var outcome = await StackProbe.PollAsync(client, Url, TimeSpan.FromSeconds(5), StackProbe.SuccessStatus, Tick);

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
            client, Url, TimeSpan.FromSeconds(5), _ => Task.FromResult((ProbeStep.Fatal, "nope")), Tick);

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
        Assert.Equal(ProbeStep.Ready, StackFixture.ClassifySameOriginApi(Json(HttpStatusCode.OK), out _));
        Assert.Equal(ProbeStep.Ready, StackFixture.ClassifySameOriginApi(Html(), out var htmlDetail));
        Assert.Contains("SPA fallback", htmlDetail);
        Assert.Equal(ProbeStep.Retry, StackFixture.ClassifySameOriginApi(new HttpResponseMessage(HttpStatusCode.BadGateway), out _));
    }

    [Theory]
    [InlineData("http://localhost:5199/api/manage/info", "text/html; charset=utf-8", true)]
    // A Debug client under Aspire calls the API origin directly — never flagged.
    [InlineData("http://localhost:5188/manage/info", "text/html", false)]
    [InlineData("http://localhost:5199/api/manage/info", "application/json", false)]
    // The SPA's own pages and assets are HTML/JS on the same origin — not API calls.
    [InlineData("http://localhost:5199/login", "text/html", false)]
    [InlineData("http://localhost:5199/apiary", "text/html", false)]
    public void Release_under_aspire_signature_is_an_app_api_call_answered_with_html(string url, string contentType, bool expected)
    {
        Assert.Equal(expected, StackFixture.IsSameOriginApiHtml("http://localhost:5199", url, contentType));
    }

    private static HttpClient Client(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new(new StubHandler(respond));

    private static HttpResponseMessage Json(HttpStatusCode status) =>
        new(status) { Content = new StringContent("{\"status\":\"ok\"}", Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Html() =>
        new(HttpStatusCode.OK) { Content = new StringContent("<!DOCTYPE html>", Encoding.UTF8, "text/html") };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
