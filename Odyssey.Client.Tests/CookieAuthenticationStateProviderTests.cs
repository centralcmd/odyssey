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
    public async Task APersistentFailure_ReadsAnonymous_ButIsNotCached()
    {
        var api = new ScriptedApi { Info = HttpStatusCode.ServiceUnavailable };
        var provider = Provider(api);

        var failed = await provider.GetAuthenticationStateAsync();
        Assert.False(failed.User.Identity?.IsAuthenticated);
        Assert.Equal(1 + RetryDelays.Length, api.InfoCalls);

        // The outage ends: the next read probes again instead of serving the failure forever.
        api.Info = HttpStatusCode.OK;
        var recovered = await provider.GetAuthenticationStateAsync();

        Assert.True(recovered.User.Identity?.IsAuthenticated);
        Assert.Equal(2 + RetryDelays.Length, api.InfoCalls);
    }

    [Fact]
    public async Task AClaimsFailure_NeverYieldsASignedInUserWithNoPermissions()
    {
        var api = new ScriptedApi { Claims = HttpStatusCode.InternalServerError };
        var provider = Provider(api);

        var state = await provider.GetAuthenticationStateAsync();

        Assert.False(state.User.Identity?.IsAuthenticated);
        Assert.Empty(state.User.Claims);
    }

    private static readonly TimeSpan[] RetryDelays = [TimeSpan.Zero, TimeSpan.Zero];

    private static CookieAuthenticationStateProvider Provider(ScriptedApi api) =>
        new(new AuthApiClient(
                new HttpClient(api) { BaseAddress = new Uri("https://api.odyssey.test/") },
                new AntiforgeryTokenStore(new ServiceCollection().BuildServiceProvider())),
            RetryDelays);

    private sealed class ScriptedApi : HttpMessageHandler
    {
        public HttpStatusCode Info { get; set; } = HttpStatusCode.OK;
        public Queue<HttpStatusCode> InfoSequence { get; } = new();
        public HttpStatusCode Claims { get; set; } = HttpStatusCode.OK;
        public int InfoCalls;
        public int ClaimsCalls;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Yield();
            switch (request.RequestUri!.AbsolutePath)
            {
                case "/manage/info":
                    Interlocked.Increment(ref InfoCalls);
                    return new HttpResponseMessage(InfoSequence.TryDequeue(out var next) ? next : Info);
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
