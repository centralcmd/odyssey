using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Moq;
using Odyssey.ApiClient;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Layout;
using Odyssey.Client.Services;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Internet;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The Internet module's navigation (issue #473 §3.1, AC 1) and the per-user news catalogue cache (§8.3, AC 15).
/// </summary>
public class NewsNavigationAndCacheTests
{
    private static Func<NavPage, bool> Holding(params string[] claims) => page => page.Claim is null || claims.Contains(page.Claim);

    [Fact]
    public void The_internet_module_holds_news_weather_points_and_news_tags()
    {
        var module = NavModel.All.Single(m => m.Key == "internet");

        Assert.Equal(("Internet", "public"), (module.Label, module.Icon));
        Assert.Equal(["News", "Weather", "Reference"], module.Groups.Select(g => g.Label));
        var pages = module.Groups.SelectMany(g => g.Items).ToList();
        Assert.Equal([("News", "news", "feed", PermissionClaims.NewsRead), ("Feeds", "news-feeds", "rss_feed", PermissionClaims.NewsRead),
                ("Weather points", "weather-points", "wb_sunny", PermissionClaims.WeatherPointsRead),
                ("News Tags", "news-tags", "local_offer", PermissionClaims.NewsRead)],
            pages.Select(p => (p.Label, p.Href, p.Icon, p.Claim)));
    }

    [Fact]
    public void Weather_points_read_alone_shows_the_module_with_only_weather_points()
    {
        Assert.Contains(NavModel.VisibleModules(Holding(PermissionClaims.WeatherPointsRead)), m => m.Key == "internet");
        var hit = Assert.Single(NavModel.AllVisiblePages(Holding(PermissionClaims.WeatherPointsRead)), h => h.ModuleKey == "internet");
        Assert.Equal("weather-points", hit.Page.Key);
        Assert.Equal("internet", NavModel.ModuleOf("weather-points").Key);
    }

    [Fact]
    public void The_module_is_gone_without_news_read_or_weather_points_read_and_news_read_alone_shows_the_three_news_pages()
    {
        Assert.DoesNotContain(NavModel.VisibleModules(Holding()), m => m.Key == "internet");
        Assert.DoesNotContain(NavModel.AllVisiblePages(Holding()), hit => hit.ModuleKey == "internet");
        Assert.Contains(NavModel.VisibleModules(Holding(PermissionClaims.NewsRead)), m => m.Key == "internet");
        Assert.Equal(3, NavModel.AllVisiblePages(Holding(PermissionClaims.NewsRead)).Count(hit => hit.ModuleKey == "internet"));
    }

    [Fact]
    public void The_feeds_route_does_not_highlight_news() =>
        Assert.Equal("news-feeds", NavModel.All.Single(m => m.Key == "internet").Groups.SelectMany(g => g.Items)
            .Single(p => NavModel.IsActive(p, "news-feeds")).Href);

    private sealed class SwitchableAuth : AuthenticationStateProvider
    {
        public string UserId { get; set; } = "alice";

        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, UserId)], "test"))));

        public void SignIn(string id)
        {
            UserId = id;
            NotifyAuthenticationStateChanged(GetAuthenticationStateAsync());
        }
    }

    [Fact]
    public async Task The_cache_is_cleared_when_the_signed_in_user_changes()
    {
        var news = new Mock<INewsApiClient>();
        var reads = 0;
        news.Setup(n => n.ListTagsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => ApiResult<List<ExistingNewsTag>>.Success([new() { Id = Guid.NewGuid(), Name = $"read {++reads}" }], HttpStatusCode.OK));
        var auth = new SwitchableAuth();
        using var cache = new NewsCatalogueCache(news.Object, auth);

        var first = await cache.GetTagsAsync();
        var again = await cache.GetTagsAsync();
        Assert.Same(first.Value, again.Value);

        auth.SignIn("bob");
        var bob = await cache.GetTagsAsync();

        Assert.NotSame(first.Value, bob.Value);
        Assert.Equal("read 2", bob.Value!.Single().Name);
    }

    [Fact]
    public async Task A_different_user_never_sees_the_previous_users_read_even_without_the_event()
    {
        var news = new Mock<INewsApiClient>();
        var reads = 0;
        news.Setup(n => n.GetCatalogueAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => ApiResult<List<NewsFeedCatalogueEntry>>.Success([new() { Id = Guid.NewGuid(), DisplayTitle = $"read {++reads}" }], HttpStatusCode.OK));
        var auth = new SwitchableAuth();
        using var cache = new NewsCatalogueCache(news.Object, auth);

        await cache.GetCatalogueAsync();
        auth.UserId = "bob";

        Assert.Equal("read 2", (await cache.GetCatalogueAsync()).Value!.Single().DisplayTitle);
    }

    [Fact]
    public async Task A_failure_is_never_cached()
    {
        var news = new Mock<INewsApiClient>();
        news.SetupSequence(n => n.ListTagsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<List<ExistingNewsTag>>.Failure(HttpStatusCode.ServiceUnavailable, new ApiProblem { Code = NewsErrorCodes.Busy }))
            .ReturnsAsync(ApiResult<List<ExistingNewsTag>>.Success([], HttpStatusCode.OK));
        using var cache = new NewsCatalogueCache(news.Object, new SwitchableAuth());

        Assert.False((await cache.GetTagsAsync()).IsSuccess);
        Assert.True((await cache.GetTagsAsync()).IsSuccess);
    }
}
