using System.Net;
using Odyssey.ApiClient.Auth;
using Odyssey.ApiClient.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Odyssey.ApiClient.Tests;

/// <summary>
/// Regression coverage for the antiforgery pipeline and the login-time token invalidation.
/// </summary>
/// <remarks>
/// These exist because the bug they pin was previously caught only incidentally: signing in re-issues
/// the antiforgery cookie for the new identity, so a token cached before login no longer pairs with it
/// and every later write is rejected with <c>400</c>. The Blazor app hid this by force-reloading after
/// sign-in (a fresh DI scope, hence a fresh store); any non-browser consumer keeps the same store and
/// hits it. Deleting <c>AuthApiClient.LoginAsync</c>'s <c>Invalidate()</c> call must fail a test here.
/// </remarks>
public class AuthApiClientTests
{
    /// <summary>
    /// Serves a distinct antiforgery token per fetch, so a stale-vs-fresh token is observable, and
    /// records the token echoed on each write.
    /// </summary>
    private sealed class TokenHandler : HttpMessageHandler
    {
        private int issued;

        public List<string?> EchoedTokens { get; } = [];
        public int TokenFetches => issued;
        public HttpStatusCode LoginStatus { get; set; } = HttpStatusCode.OK;
        public string LoginProblem { get; set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path.EndsWith("/api/antiforgery/token", StringComparison.Ordinal))
            {
                issued++;
                return Json($$"""{"token":"token-{{issued}}"}""");
            }

            if (path.EndsWith("/login", StringComparison.Ordinal))
            {
                EchoedTokens.Add(Header(request));
                return LoginStatus == HttpStatusCode.OK
                    ? new HttpResponseMessage(HttpStatusCode.OK)
                    : new HttpResponseMessage(LoginStatus)
                    {
                        Content = new StringContent($$"""{"detail":"{{LoginProblem}}"}""",
                                                    System.Text.Encoding.UTF8, "application/problem+json"),
                    };
            }

            EchoedTokens.Add(Header(request));
            return new HttpResponseMessage(HttpStatusCode.NoContent);

            static string? Header(HttpRequestMessage r) =>
                r.Headers.TryGetValues(AntiforgeryHandler.HeaderName, out var v) ? v.FirstOrDefault() : null;

            static HttpResponseMessage Json(string body) =>
                new(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
                };
        }
    }

    private static (ServiceProvider Provider, TokenHandler Handler) Build()
    {
        var handler = new TokenHandler();
        var services = new ServiceCollection();
        services.AddOdysseyApiClient();
        services.AddScoped(sp =>
        {
            var antiforgery = sp.GetRequiredService<AntiforgeryHandler>();
            antiforgery.InnerHandler = handler;
            return new HttpClient(antiforgery) { BaseAddress = new Uri("http://localhost/") };
        });
        return (services.BuildServiceProvider(), handler);
    }

    /// <summary>
    /// The regression guard: a write after login must not reuse the token minted before it. Deleting
    /// <c>Invalidate()</c> from <c>LoginAsync</c> makes this fail.
    /// </summary>
    [Fact]
    public async Task A_write_after_login_uses_a_freshly_fetched_antiforgery_token()
    {
        var (provider, handler) = Build();
        var auth = provider.GetRequiredService<AuthApiClient>();
        var api = provider.GetRequiredService<IOdysseyApi>();

        var outcome = await auth.LoginAsync(new LoginRequest { Email = "a@b.c", Password = "pw" });
        Assert.Equal(LoginOutcome.Success, outcome);

        await api.SendAsync(HttpMethod.Post, "api/contacts", new { name = "Acme" });

        Assert.Equal(2, handler.TokenFetches);                    // one for login, one re-fetched after
        Assert.Equal("token-1", handler.EchoedTokens[0]);         // the login POST
        Assert.Equal("token-2", handler.EchoedTokens[1]);         // the post-login write — not the stale one
    }

    /// <summary>
    /// The cache must still work within a session: repeated writes after one login share a token
    /// rather than re-fetching per request.
    /// </summary>
    [Fact]
    public async Task Writes_after_the_first_reuse_the_cached_token()
    {
        var (provider, handler) = Build();
        var auth = provider.GetRequiredService<AuthApiClient>();
        var api = provider.GetRequiredService<IOdysseyApi>();

        await auth.LoginAsync(new LoginRequest { Email = "a@b.c", Password = "pw" });
        await api.SendAsync(HttpMethod.Post, "api/contacts", new { name = "A" });
        await api.SendAsync(HttpMethod.Post, "api/contacts", new { name = "B" });

        Assert.Equal(2, handler.TokenFetches);
        Assert.Equal("token-2", handler.EchoedTokens[1]);
        Assert.Equal("token-2", handler.EchoedTokens[2]);
    }

    /// <summary>
    /// A <c>429</c> from the per-IP Identity limiter is its own outcome, not <c>Failed</c>. The two are
    /// worlds apart to a user: the limiter rejects the request before any credential is looked at, so
    /// reporting it as a failed sign-in tells someone who typed their password correctly to go and
    /// re-check it. The login page keys its message off this distinction.
    /// </summary>
    [Fact]
    public async Task A_rate_limited_login_is_reported_as_RateLimited_not_Failed()
    {
        var (provider, handler) = Build();
        handler.LoginStatus = HttpStatusCode.TooManyRequests;
        var auth = provider.GetRequiredService<AuthApiClient>();

        var outcome = await auth.LoginAsync(new LoginRequest { Email = "a@b.c", Password = "pw" });

        Assert.Equal(LoginOutcome.RateLimited, outcome);
    }

    /// <summary>
    /// A failed sign-in must not invalidate: the anonymous session's token is still the valid one,
    /// and dropping it would cost an extra round-trip on every retry.
    /// </summary>
    [Fact]
    public async Task A_failed_login_does_not_invalidate_the_cached_token()
    {
        var (provider, handler) = Build();
        handler.LoginStatus = HttpStatusCode.Unauthorized;
        handler.LoginProblem = "Bad credentials";
        var auth = provider.GetRequiredService<AuthApiClient>();
        var api = provider.GetRequiredService<IOdysseyApi>();

        var outcome = await auth.LoginAsync(new LoginRequest { Email = "a@b.c", Password = "wrong" });
        Assert.Equal(LoginOutcome.Failed, outcome);

        await api.SendAsync(HttpMethod.Post, "api/contacts", new { name = "Acme" });

        Assert.Equal(1, handler.TokenFetches);
        Assert.Equal("token-1", handler.EchoedTokens[1]);
    }

    [Fact]
    public async Task Logout_invalidates_the_cached_token()
    {
        var (provider, handler) = Build();
        var auth = provider.GetRequiredService<AuthApiClient>();
        var api = provider.GetRequiredService<IOdysseyApi>();

        await auth.LoginAsync(new LoginRequest { Email = "a@b.c", Password = "pw" });
        await auth.LogoutAsync();
        await api.SendAsync(HttpMethod.Post, "api/contacts", new { name = "Acme" });

        Assert.Equal(3, handler.TokenFetches);
        Assert.Equal("token-3", handler.EchoedTokens[^1]);
    }

    // Safe methods must not trigger a token fetch — otherwise resolving the token would recurse,
    // since the token endpoint is itself a GET.
    [Theory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    public async Task Safe_methods_carry_no_antiforgery_token(string method)
    {
        var (provider, handler) = Build();
        var http = provider.GetRequiredService<HttpClient>();

        await http.SendAsync(new HttpRequestMessage(new HttpMethod(method), "api/contacts"));

        Assert.Equal(0, handler.TokenFetches);
        Assert.Null(Assert.Single(handler.EchoedTokens));
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task Unsafe_methods_carry_an_antiforgery_token(string method)
    {
        var (provider, handler) = Build();
        var http = provider.GetRequiredService<HttpClient>();

        await http.SendAsync(new HttpRequestMessage(new HttpMethod(method), "api/contacts"));

        Assert.Equal(1, handler.TokenFetches);
        Assert.Equal("token-1", Assert.Single(handler.EchoedTokens));
    }
}
