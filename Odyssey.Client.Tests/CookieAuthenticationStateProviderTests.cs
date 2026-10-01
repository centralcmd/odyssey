using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Odyssey.ApiClient.Auth;
using Odyssey.Client.Auth;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The provider resolves the session once and serves every later read from it (issue #250): before,
/// each read cost two sequential round-trips, paid by the layout, the page and every dialog open. Only a
/// definitive answer may be cached — a failed probe must not sign the user out for the app's lifetime.
/// </summary>
public class CookieAuthenticationStateProviderTests
{
    private const string ClaimsJson = """[{"type":"permission","value":"accounts.read"}]""";

    [Fact]
    public async Task RepeatedReads_ProbeTheSessionOnce()
    {
        var api = new ScriptedApi();
        var provider = Provider(api);

        var first = await provider.GetAuthenticationStateAsync();
        var second = await provider.GetAuthenticationStateAsync();
        await provider.GetAuthenticationStateAsync();

        Assert.True(first.User.Identity?.IsAuthenticated);
        Assert.Same(first, second);
        Assert.Equal(1, api.InfoCalls);
        Assert.Equal(1, api.ClaimsCalls);
    }

    [Fact]
    public async Task ConcurrentReads_ShareOneProbe()
    {
        var api = new ScriptedApi();
        var provider = Provider(api);

        await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => provider.GetAuthenticationStateAsync()));

        Assert.Equal(1, api.InfoCalls);
    }

    [Fact]
    public async Task AnAnonymousAnswer_IsCachedToo()
    {
        var api = new ScriptedApi { Info = HttpStatusCode.Unauthorized };
        var provider = Provider(api);

        var state = await provider.GetAuthenticationStateAsync();
        await provider.GetAuthenticationStateAsync();

        Assert.False(state.User.Identity?.IsAuthenticated);
        Assert.Equal(1, api.InfoCalls);
    }

    [Fact]
    public async Task RefreshAsync_ReplacesTheCachedState_AndAnnouncesIt()
    {
        var api = new ScriptedApi { Info = HttpStatusCode.Unauthorized };
        var provider = Provider(api);
        Assert.False((await provider.GetAuthenticationStateAsync()).User.Identity?.IsAuthenticated);

        Task<Microsoft.AspNetCore.Components.Authorization.AuthenticationState>? announced = null;
        provider.AuthenticationStateChanged += task => announced = task;
        api.Info = HttpStatusCode.OK;
        await provider.RefreshAsync();

        Assert.NotNull(announced);
        Assert.True((await announced).User.Identity?.IsAuthenticated);
        Assert.True((await provider.GetAuthenticationStateAsync()).User.Identity?.IsAuthenticated);
        Assert.Equal(2, api.InfoCalls);
    }

    [Fact]
    public async Task ATransientFailure_IsRetried_ThenRecovers()
    {
        var api = new ScriptedApi();
        api.InfoSequence.Enqueue(HttpStatusCode.ServiceUnavailable);
        var provider = Provider(api);

        var state = await provider.GetAuthenticationStateAsync();

        Assert.True(state.User.Identity?.IsAuthenticated);
        Assert.Equal(2, api.InfoCalls);
    }

    [Fact]
    public async Task APersistentFailure_ReadsUnavailable_ButIsNotCached()
    {
        var api = new ScriptedApi { Info = HttpStatusCode.ServiceUnavailable };
        var provider = Provider(api);

        var failed = await provider.GetAuthenticationStateAsync();
        Assert.False(failed.User.Identity?.IsAuthenticated);
        Assert.True(SessionUnavailable.Is(failed.User));
        Assert.Equal(1 + RetryDelays.Length, api.InfoCalls);

        // The outage ends: RefreshAsync (sign-in, or the panel's Retry) probes again instead of the
        // failure being served for the app's lifetime.
        api.Info = HttpStatusCode.OK;
        await provider.RefreshAsync();
        var recovered = await provider.GetAuthenticationStateAsync();

        Assert.True(recovered.User.Identity?.IsAuthenticated);
        Assert.Equal(2 + RetryDelays.Length, api.InfoCalls);
    }

    [Fact]
    public async Task WhileRecoveryRuns_ReadsDoNotStartProbesOfTheirOwn()
    {
        var api = new ScriptedApi { Info = HttpStatusCode.TooManyRequests };
        var provider = Provider(api);

        await provider.GetAuthenticationStateAsync();
        var calls = api.InfoCalls;

        // The router, the layout, the expiry redirect: each read during an outage used to start its own
        // retry chain against a rate limiter that was already refusing.
        for (var i = 0; i < 5; i++)
        {
            Assert.True(SessionUnavailable.Is((await provider.GetAuthenticationStateAsync()).User));
        }

        Assert.Equal(calls, api.InfoCalls);
    }

    [Fact]
    public async Task RefreshAsync_DuringRecovery_BypassesIt_AndTheLoopThenStandsDown()
    {
        var api = new ScriptedApi { Info = HttpStatusCode.ServiceUnavailable };
        var delays = new ControlledDelays();
        var provider = Provider(api, delays);
        await provider.GetAuthenticationStateAsync();
        await delays.WaitForPendingAsync();

        api.Info = HttpStatusCode.OK;
        await provider.RefreshAsync();
        Assert.True((await provider.GetAuthenticationStateAsync()).User.Identity?.IsAuthenticated);
        var calls = api.InfoCalls;

        // The loop wakes, sees a definitive answer it did not produce, re-announces what is cached and
        // ends — without a probe of its own.
        var loopEnded = new TaskCompletionSource<Microsoft.AspNetCore.Components.Authorization.AuthenticationState>();
        provider.AuthenticationStateChanged += async task => loopEnded.TrySetResult(await task);
        await delays.ReleaseNextAsync();

        Assert.True((await loopEnded.Task.WaitAsync(TimeSpan.FromSeconds(5))).User.Identity?.IsAuthenticated);
        Assert.Equal(calls, api.InfoCalls);
        Assert.Single(delays.Requested);
    }

    [Fact]
    public async Task AShortRetryAfter_IsRetriedWithinTheRead()
    {
        var api = new ScriptedApi { Info = HttpStatusCode.OK, RetryAfterSeconds = 1 };
        api.InfoSequence.Enqueue(HttpStatusCode.TooManyRequests);
        var provider = new CookieAuthenticationStateProvider(
            Client(api), [TimeSpan.FromSeconds(1)], _ => new TaskCompletionSource().Task);

        var state = await provider.GetAuthenticationStateAsync();

        Assert.True(state.User.Identity?.IsAuthenticated);
        Assert.Equal(2, api.InfoCalls);
    }

    [Fact]
    public async Task AnUnexpectedProbeException_DoesNotEndTheRecoveryLoop()
    {
        var api = new ScriptedApi { Info = HttpStatusCode.ServiceUnavailable };
        var delays = new ControlledDelays();
        var provider = Provider(api, delays);
        var announced = new TaskCompletionSource<Microsoft.AspNetCore.Components.Authorization.AuthenticationState>();
        provider.AuthenticationStateChanged += async task => announced.TrySetResult(await task);
        await provider.GetAuthenticationStateAsync();

        api.ThrowNext = true;
        await delays.ReleaseNextAsync();
        await delays.WaitForPendingAsync();

        api.Info = HttpStatusCode.OK;
        await delays.ReleaseNextAsync();

        Assert.True((await announced.Task.WaitAsync(TimeSpan.FromSeconds(5))).User.Identity?.IsAuthenticated);
    }

    [Fact]
    public async Task AClaimsFailure_NeverYieldsASignedInUserWithNoPermissions()
    {
        var api = new ScriptedApi { Claims = HttpStatusCode.InternalServerError };
        var provider = Provider(api);

        var state = await provider.GetAuthenticationStateAsync();

        Assert.False(state.User.Identity?.IsAuthenticated);
        Assert.True(SessionUnavailable.Is(state.User));
        Assert.DoesNotContain(state.User.Claims, claim => claim.Type == "permission");
    }

    [Fact]
    public async Task AGenuine401_IsAnonymous_NotUnavailable()
    {
        var api = new ScriptedApi { Info = HttpStatusCode.Unauthorized };
        var provider = Provider(api);

        var state = await provider.GetAuthenticationStateAsync();

        Assert.False(SessionUnavailable.Is(state.User));
        Assert.Empty(state.User.Claims);
    }

    [Fact]
    public async Task AfterAnOutage_TheBackgroundProbe_AnnouncesTheRecoveredSession()
    {
        var api = new ScriptedApi { Info = HttpStatusCode.ServiceUnavailable };
        var delays = new ControlledDelays();
        var provider = Provider(api, delays);
        var announced = new TaskCompletionSource<Microsoft.AspNetCore.Components.Authorization.AuthenticationState>();
        provider.AuthenticationStateChanged += async task => announced.TrySetResult(await task);

        Assert.True(SessionUnavailable.Is((await provider.GetAuthenticationStateAsync()).User));

        // Still down at the first background probe: nothing is announced, the wait doubles.
        await delays.ReleaseNextAsync();
        await delays.WaitForPendingAsync();
        Assert.False(announced.Task.IsCompleted);

        api.Info = HttpStatusCode.OK;
        await delays.ReleaseNextAsync();

        var state = await announced.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(state.User.Identity?.IsAuthenticated);
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)], delays.Requested);

        // Cached: a later read does not probe again.
        var calls = api.InfoCalls;
        Assert.True((await provider.GetAuthenticationStateAsync()).User.Identity?.IsAuthenticated);
        Assert.Equal(calls, api.InfoCalls);
    }

    [Fact]
    public async Task TheBackgroundBackoff_IsCappedAtThirtySeconds()
    {
        var api = new ScriptedApi { Info = HttpStatusCode.ServiceUnavailable };
        var delays = new ControlledDelays();
        var provider = Provider(api, delays);

        await provider.GetAuthenticationStateAsync();
        for (var i = 0; i < 7; i++)
        {
            await delays.ReleaseNextAsync();
            await delays.WaitForPendingAsync();
        }

        Assert.Equal(
            new[] { 1, 2, 4, 8, 16, 30, 30, 30 }.Select(seconds => TimeSpan.FromSeconds(seconds)),
            delays.Requested);
    }

    [Fact]
    public async Task ARateLimitedProbe_HonoursRetryAfter()
    {
        var api = new ScriptedApi { Info = HttpStatusCode.TooManyRequests, RetryAfterSeconds = 20 };
        var delays = new ControlledDelays();
        var provider = Provider(api, delays);

        var state = await provider.GetAuthenticationStateAsync();
        await delays.WaitForPendingAsync();

        Assert.True(SessionUnavailable.Is(state.User));

        // A wait longer than the in-read retries is not spent holding up the router: one probe, then the
        // background loop waits out exactly what the server asked for.
        Assert.Equal(1, api.InfoCalls);
        Assert.Equal([TimeSpan.FromSeconds(20)], delays.Requested);
    }

    [Fact]
    public async Task ARetryAfterBeyondTheCeiling_IsClamped()
    {
        var api = new ScriptedApi { Info = HttpStatusCode.TooManyRequests, RetryAfterSeconds = 3600 };
        var delays = new ControlledDelays();
        var provider = Provider(api, delays);

        await provider.GetAuthenticationStateAsync();
        await delays.WaitForPendingAsync();

        Assert.Equal([CookieAuthenticationStateProvider.RetryAfterCeiling], delays.Requested);
    }

    private static readonly TimeSpan[] RetryDelays = [TimeSpan.Zero, TimeSpan.Zero];

    // The default never completes, so the background recovery loop sits parked and leaves the probe
    // counts of the tests above untouched.
    private static CookieAuthenticationStateProvider Provider(ScriptedApi api, ControlledDelays? delays = null) =>
        new(Client(api), RetryDelays, delays is null ? _ => new TaskCompletionSource().Task : delays.WaitAsync);

    private static AuthApiClient Client(ScriptedApi api) =>
        new(new HttpClient(api) { BaseAddress = new Uri("https://api.odyssey.test/") },
            new AntiforgeryTokenStore(new ServiceCollection().BuildServiceProvider()));

    /// <summary>Records each background wait and completes it only when the test releases it.</summary>
    private sealed class ControlledDelays
    {
        private readonly Lock gate = new();
        private readonly Queue<TaskCompletionSource> waiting = new();
        private TaskCompletionSource pending = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<TimeSpan> Requested { get; } = [];

        public Task WaitAsync(TimeSpan wait)
        {
            var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (gate)
            {
                Requested.Add(wait);
                waiting.Enqueue(released);
                pending.TrySetResult();
            }

            return released.Task;
        }

        /// <summary>Completes once the loop is parked on a wait that has not been released yet.</summary>
        public Task WaitForPendingAsync()
        {
            lock (gate)
            {
                return waiting.Count > 0 ? Task.CompletedTask : pending.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }

        public async Task ReleaseNextAsync()
        {
            await WaitForPendingAsync();
            TaskCompletionSource next;
            lock (gate)
            {
                next = waiting.Dequeue();
                pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            next.SetResult();
        }
    }

    private sealed class ScriptedApi : HttpMessageHandler
    {
        public HttpStatusCode Info { get; set; } = HttpStatusCode.OK;
        public Queue<HttpStatusCode> InfoSequence { get; } = new();
        public HttpStatusCode Claims { get; set; } = HttpStatusCode.OK;
        public int? RetryAfterSeconds { get; set; }
        public bool ThrowNext { get; set; }
        public int InfoCalls;
        public int ClaimsCalls;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Yield();
            switch (request.RequestUri!.AbsolutePath)
            {
                case "/manage/info":
                    Interlocked.Increment(ref InfoCalls);
                    if (ThrowNext)
                    {
                        // Not one of the transport failures GetSessionAsync converts to Unavailable.
                        ThrowNext = false;
                        throw new InvalidOperationException("unexpected");
                    }

                    var info = new HttpResponseMessage(InfoSequence.TryDequeue(out var next) ? next : Info);
                    if (RetryAfterSeconds is { } seconds)
                    {
                        info.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(
                            TimeSpan.FromSeconds(seconds));
                    }

                    return info;
                case "/auth/claims":
                    Interlocked.Increment(ref ClaimsCalls);
                    return new HttpResponseMessage(Claims)
                    {
                        Content = new StringContent(ClaimsJson, Encoding.UTF8, "application/json"),
                    };
                default:
                    throw new InvalidOperationException(request.RequestUri.ToString());
            }
        }
    }
}
