using System.Net;
using Odyssey.ApiClient;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Services;
using Odyssey.Dtos.Journal;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary><see cref="MapStylesCache"/> (issue #371 §4, AC 14; F6, R2-S3).</summary>
public class MapStylesCacheTests
{
    private static readonly MapStyles Styles = new()
    {
        LightStyleUrl = "https://tiles.example/styles/light",
        DarkStyleUrl = "/maps/blank-dark.json",
        Attribution = "© OpenStreetMap",
        AttributionUrl = "https://www.openstreetmap.org/copyright",
    };

    [Fact]
    public async Task A_success_is_fetched_once_per_session()
    {
        var api = new FakeApi { Next = () => Task.FromResult(ApiResult<MapStyles>.Success(Styles, HttpStatusCode.OK)) };
        var cache = new MapStylesCache(api);

        Assert.Same(Styles, await cache.GetAsync());
        Assert.Same(Styles, await cache.GetAsync());
        Assert.Equal(1, api.Calls);
    }

    [Fact]
    public async Task A_failure_is_never_cached_and_has_no_fallback()
    {
        var api = new FakeApi { Next = () => Task.FromResult(ApiResult<MapStyles>.Failure(HttpStatusCode.ServiceUnavailable, new ApiProblem())) };
        var cache = new MapStylesCache(api);

        Assert.Null(await cache.GetAsync());

        api.Next = () => Task.FromResult(ApiResult<MapStyles>.Success(Styles, HttpStatusCode.OK));
        Assert.Same(Styles, await cache.GetAsync());
        Assert.Equal(2, api.Calls);
    }

    [Fact]
    public async Task One_callers_cancellation_never_cancels_another_callers_fetch()
    {
        var gate = new TaskCompletionSource<ApiResult<MapStyles>>();
        var api = new FakeApi { Next = () => gate.Task };
        var cache = new MapStylesCache(api);
        using var cancelled = new CancellationTokenSource();

        var first = cache.GetAsync();
        var second = cache.GetAsync(cancelled.Token);
        await cancelled.CancelAsync();
        gate.SetResult(ApiResult<MapStyles>.Success(Styles, HttpStatusCode.OK));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        Assert.Same(Styles, await first);
        Assert.Equal(1, api.Calls);
        Assert.Equal(CancellationToken.None, api.LastToken);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("http://tiles.example/s")]
    [InlineData("//evil.example/s.json")]
    [InlineData("/\\evil.example/s.json")]
    [InlineData("data:application/json,{}")]
    [InlineData("")]
    public async Task A_style_url_that_is_neither_https_nor_root_relative_is_unavailable(string url)
    {
        var api = new FakeApi { Next = () => Task.FromResult(ApiResult<MapStyles>.Success(Styles with { LightStyleUrl = url }, HttpStatusCode.OK)) };

        Assert.Null(await new MapStylesCache(api).GetAsync());
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("http://www.openstreetmap.org/copyright")]
    public async Task An_attribution_link_that_is_not_https_is_unavailable(string url)
    {
        var api = new FakeApi { Next = () => Task.FromResult(ApiResult<MapStyles>.Success(Styles with { AttributionUrl = url }, HttpStatusCode.OK)) };

        Assert.Null(await new MapStylesCache(api).GetAsync());
    }

    private sealed class FakeApi : IMapStylesApiClient
    {
        public required Func<Task<ApiResult<MapStyles>>> Next { get; set; }

        public int Calls { get; private set; }

        public CancellationToken LastToken { get; private set; }

        public Task<ApiResult<MapStyles>> GetAsync(CancellationToken ct = default)
        {
            Calls++;
            LastToken = ct;
            return Next();
        }
    }
}
