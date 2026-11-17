using System.Text.RegularExpressions;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The published service worker and the news reader (issue #473 §10, AC 28): news assets are not precached, and a
/// news API response or news image is never answered from the cache. Both lists are read out of the worker file and
/// run against the URLs they must catch, as <see cref="LocationMapServiceWorkerTests"/> does for the map.
/// </summary>
public class NewsServiceWorkerTests
{
    private static readonly Lazy<string> Worker = new(() =>
        File.ReadAllText(Path.Combine(ClientSource.Root, "wwwroot", "service-worker.published.js")));

    private static IReadOnlyList<Regex> Patterns(string array)
    {
        var block = Regex.Match(Worker.Value, $@"const {array} = \[(?<body>.*?)\];", RegexOptions.Singleline);
        Assert.True(block.Success, $"service-worker.published.js has no {array} array.");
        return Regex.Matches(block.Groups["body"].Value, @"/(?<pattern>\^[^\n]*?)/(?=\s*(?:,|$))", RegexOptions.Multiline)
            .Select(m => new Regex(m.Groups["pattern"].Value))
            .ToList();
    }

    [Theory]
    [InlineData("css/news.css")]
    [InlineData("css/news.abc123xyz.css")]
    public void News_assets_are_kept_out_of_the_precache(string url) =>
        Assert.Contains(Patterns("offlineAssetsExclude"), pattern => pattern.IsMatch(url));

    [Theory]
    [InlineData("/api/news-articles")]
    [InlineData("/api/news-articles/0b6f/images/9a1c/content")]
    [InlineData("/api/news-feeds/catalogue")]
    [InlineData("/api/news-status")]
    public void News_api_responses_and_images_always_go_to_the_network(string path) =>
        Assert.Contains(Patterns("networkOnlyPaths"), pattern => pattern.IsMatch(path));

    [Theory]
    [InlineData("/api/accounts")]
    [InlineData("/news")]
    public void Other_requests_keep_the_cache_path(string path) =>
        Assert.DoesNotContain(Patterns("networkOnlyPaths"), pattern => pattern.IsMatch(path));

    [Fact]
    public void The_fetch_handler_checks_the_network_only_list_before_the_cache()
    {
        var handler = Worker.Value[Worker.Value.IndexOf("async function onFetch", StringComparison.Ordinal)..];
        Assert.True(handler.IndexOf("networkOnlyPaths", StringComparison.Ordinal) < handler.IndexOf("cache.match", StringComparison.Ordinal));
    }
}
