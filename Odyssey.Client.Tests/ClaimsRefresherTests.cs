using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Odyssey.Client.Services;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// <see cref="IClaimsRefresher"/>, generalised from the weather refresher (issue #509 §7.3, v4 §7, v5 §5): a burst of
/// <c>403</c>s causes one refresh, a non-<c>403</c> none, and <see cref="IWeatherClaimsRefresher"/> stays an adapter
/// over the shared instance, so one refresh serves every caller in the session.
/// </summary>
public class ClaimsRefresherTests
{
    [Fact] // v5 §5: the one-refresh-per-403-burst guard
    public async Task A_burst_of_403s_causes_exactly_one_refresh()
    {
        var auth = new SignedIn();
        var calls = 0;
        var gate = new TaskCompletionSource<bool>();
        using var refresher = new ClaimsRefresher(auth, () => { Interlocked.Increment(ref calls); return gate.Task; });

        var burst = Enumerable.Range(0, 8).Select(_ => refresher.OnResponseAsync(HttpStatusCode.Forbidden)).ToList();
        gate.SetResult(true);
        var results = await Task.WhenAll(burst);

        Assert.Equal(1, calls);
        Assert.All(results, Assert.True);
        Assert.False(await refresher.OnResponseAsync(HttpStatusCode.Forbidden));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Anything_but_a_403_never_refreshes()
    {
        using var refresher = new ClaimsRefresher(new SignedIn(), () => throw new InvalidOperationException("no refresh"));

        Assert.False(await refresher.OnResponseAsync(HttpStatusCode.OK));
        Assert.False(await refresher.OnResponseAsync(HttpStatusCode.TooManyRequests));
        Assert.False(await refresher.OnResponseAsync(HttpStatusCode.ServiceUnavailable));
    }

    [Fact]
    public async Task A_failing_refresh_reports_no_change()
    {
        using var refresher = new ClaimsRefresher(new SignedIn(), () => throw new HttpRequestException("offline"));

        Assert.False(await refresher.OnResponseAsync(HttpStatusCode.Forbidden));
    }

    [Fact] // v5 §5: the weather refresher is an adapter, and shares the session's one refresh
    public async Task The_weather_refresher_delegates_to_the_shared_refresher()
    {
        var calls = 0;
        using var shared = new ClaimsRefresher(new SignedIn(), () => { calls++; return Task.FromResult(true); });
        using var weather = new WeatherClaimsRefresher(shared);

        Assert.True(await weather.OnResponseAsync(HttpStatusCode.Forbidden));
        Assert.False(await shared.OnResponseAsync(HttpStatusCode.Forbidden));
        Assert.Equal(1, calls);
    }

    private sealed class SignedIn : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "u")], "test"))));
    }
}

/// <summary>A test fake for pages: counts the <c>403</c>s it is handed and reports whether a refresh "ran".</summary>
internal sealed class FakeClaimsRefresher : IClaimsRefresher
{
    private readonly Func<Task>? onRefresh;
    private int refreshed;

    public FakeClaimsRefresher(Func<Task>? onRefresh = null) => this.onRefresh = onRefresh;

    public int Forbidden { get; private set; }

    public int Refreshes => refreshed;

    public async Task<bool> OnResponseAsync(HttpStatusCode status)
    {
        if (status != HttpStatusCode.Forbidden)
        {
            return false;
        }

        Forbidden++;
        if (Interlocked.Exchange(ref refreshed, 1) == 1)
        {
            return false;
        }

        if (onRefresh is not null)
        {
            await onRefresh();
        }

        return true;
    }
}
