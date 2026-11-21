using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Odyssey.Context;
using Odyssey.Core.Internet.News;
using Odyssey.Core.Internet.News.Fetching;
using Odyssey.Core.Internet.News.Files;
using Odyssey.Core.Internet.News.Jobs;
using Odyssey.Testing.FileStorage;

namespace Odyssey.Testing.News;

/// <summary>
/// A worker "process" of the news job for tests (issue #470 B3): the production registrations
/// (<c>AddNewsReader</c>, <c>AddNewsFeedFetching</c>) over the file store's test host, with the egress client's
/// primary handler replaced by <see cref="FakeNewsServer"/> (no socket, no DNS), the host throttle's spacing at zero so
/// a manual clock can follow a same-host redirect, and the volume verified. Two harnesses over one database and one
/// folder are two replicas.
/// </summary>
public sealed class NewsJobHarness : IAsyncDisposable
{
    private NewsJobHarness(FileStorageTestHost host, FakeNewsServer server)
    {
        Host = host;
        Server = server;
    }

    public FileStorageTestHost Host { get; }

    public FakeNewsServer Server { get; }

    public TestClock Clock => Host.Clock;

    public NewsFeedJob Job => Host.Services.GetRequiredService<NewsFeedJob>();

    public static async Task<NewsJobHarness> CreateAsync(
        Action<DbContextOptionsBuilder<OdysseyContext>> useDatabase,
        string volumeRoot,
        TestClock? clock = null,
        FakeNewsServer? server = null,
        int? bucketCapacity = null,
        Action<IServiceCollection>? configureServices = null,
        TimeSpan? throttleSpacing = null)
    {
        server ??= new FakeNewsServer();
        var host = FileStorageTestHost.Create(
            useDatabase,
            new Dictionary<string, string> { ["primary"] = volumeRoot },
            new Dictionary<string, string> { [NewsArticleStorage.FeatureKey] = "primary" },
            features: [],
            clock: clock,
            configureServices: services =>
            {
                services.AddNewsReader();
                services.AddNewsFeedFetching();
                services.AddHttpClient(NewsEgress.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => server);
                services.Replace(ServiceDescriptor.Singleton(sp => new NewsHostThrottleGate(
                    sp.GetRequiredService<IServiceScopeFactory>(), sp.GetRequiredService<TimeProvider>())
                { Spacing = throttleSpacing ?? TimeSpan.Zero }));
                if (bucketCapacity is { } capacity)
                {
                    services.Replace(ServiceDescriptor.Scoped(sp => new NewsBucketAllocator(
                        sp.GetRequiredService<IDbContextFactory<OdysseyContext>>(), sp.GetRequiredService<TimeProvider>())
                    { Capacity = capacity }));
                }

                services.AddSingleton<NewsFeedJob>();
                configureServices?.Invoke(services);
            });
        await host.VerifyAsync();
        return new NewsJobHarness(host, server);
    }

    public AsyncServiceScope Scope() => Host.Scope();

    /// <summary>A new context over the harness's database, for arranging and asserting.</summary>
    public async Task<T> WithContextAsync<T>(Func<OdysseyContext, Task<T>> work)
    {
        await using var scope = Scope();
        return await work(scope.ServiceProvider.GetRequiredService<OdysseyContext>());
    }

    public Task WithContextAsync(Func<OdysseyContext, Task> work) =>
        WithContextAsync(async context =>
        {
            await work(context);
            return true;
        });

    /// <summary>Creates a feed as the API's <c>POST</c> does: the feed, its fetch state (due now) and its counters in one save.</summary>
    public Task<Guid> AddFeedAsync(string url, bool downloadImages = false, int? fetchIntervalMinutes = null) =>
        WithContextAsync(async context =>
        {
            var feedId = Guid.NewGuid();
            var now = Clock.GetUtcNow().UtcDateTime;
            context.NewsFeeds.Add(new NewsFeed
            {
                NewsFeedId = feedId,
                Url = url,
                UrlKeyHash = NewsUrlKey.Hash(NewsUrlKey.UrlKey(url)!),
                DownloadImages = downloadImages,
                IsActive = true,
                FetchIntervalMinutes = fetchIntervalMinutes,
                CreatedAtUtc = now,
                SettingsVersion = Guid.NewGuid(),
            });
            context.NewsFeedFetchStates.Add(new NewsFeedFetchState { NewsFeedId = feedId, FetchFence = Guid.NewGuid(), NextFetchAtUtc = now });
            context.NewsFeedCounters.Add(new NewsFeedCounter { NewsFeedId = feedId });
            if (!await context.NewsStorageTotals.AnyAsync())
            {
                context.NewsStorageTotals.Add(new NewsStorageTotal { Id = NewsStorageTotal.SingletonId });
            }

            await context.SaveChangesAsync();
            return feedId;
        });

    public Task<NewsFeedFetchState> StateAsync(Guid feedId) =>
        WithContextAsync(context => context.NewsFeedFetchStates.AsNoTracking().SingleAsync(s => s.NewsFeedId == feedId));

    public Task<List<NewsArticle>> ArticlesAsync(Guid feedId) =>
        WithContextAsync(context => context.NewsArticles.AsNoTracking().Where(a => a.NewsFeedId == feedId).OrderBy(a => a.SortAtUtc).ToListAsync());

    /// <summary>Moves the clock past every lease and schedule the last tick set, so the next tick sees the feeds due again.</summary>
    public void Advance(TimeSpan by) => Clock.Advance(by);

    public ValueTask DisposeAsync() => Host.DisposeAsync();
}

/// <summary>
/// The fake publisher: a response per absolute URL, every request recorded. An unknown URL answers <c>404</c>.
/// </summary>
public sealed class FakeNewsServer : HttpMessageHandler
{
    private readonly ConcurrentDictionary<string, Func<HttpRequestMessage, HttpResponseMessage>> routes = new(StringComparer.Ordinal);

    public ConcurrentQueue<HttpRequestMessage> Requests { get; } = new();

    public void Map(string url, Func<HttpRequestMessage, HttpResponseMessage> respond) => routes[url] = respond;

    /// <summary>A feed body (UTF-8) with optional validators; a matching <c>If-None-Match</c> answers <c>304</c>.</summary>
    public void Feed(string url, string body, string? etag = null, string contentType = "application/rss+xml") =>
        Map(url, request =>
        {
            if (etag is not null && request.Headers.TryGetValues("If-None-Match", out var sent) && sent.Contains(etag))
            {
                return new HttpResponseMessage(HttpStatusCode.NotModified);
            }

            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body)) };
            response.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
            if (etag is not null)
            {
                response.Headers.TryAddWithoutValidation("ETag", etag);
            }

            return response;
        });

    public void Bytes(string url, byte[] body, string contentType) =>
        Map(url, _ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
            response.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
            return response;
        });

    public void Redirect(string url, HttpStatusCode status, string location) =>
        Map(url, _ =>
        {
            var response = new HttpResponseMessage(status);
            response.Headers.TryAddWithoutValidation("Location", location);
            return response;
        });

    public void Status(string url, HttpStatusCode status) => Map(url, _ => new HttpResponseMessage(status));

    public int CountFor(string url) => Requests.Count(r => r.RequestUri?.AbsoluteUri == url);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Enqueue(request);
        var key = request.RequestUri!.AbsoluteUri;
        var response = routes.TryGetValue(key, out var respond) ? respond(request) : new HttpResponseMessage(HttpStatusCode.NotFound);
        response.RequestMessage = request;
        return Task.FromResult(response);
    }
}

/// <summary>RSS and Atom documents for the job tests.</summary>
public static class NewsFeedFixtures
{
    public sealed record Item(string? Guid, string Title, string? Link, string Body, string? PubDate = "Mon, 26 Oct 2026 08:00:00 GMT", string? Enclosure = null);

    public static string Rss(params Item[] items) => Rss("Example news", items);

    public static string Rss(string title, params Item[] items)
    {
        var builder = new StringBuilder();
        builder.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?><rss version=\"2.0\"><channel>");
        builder.Append("<title>").Append(WebUtility.HtmlEncode(title)).Append("</title><link>https://www.example.com/</link><description>About.</description>");
        foreach (var item in items)
        {
            builder.Append("<item>");
            if (item.Guid is not null)
            {
                builder.Append("<guid isPermaLink=\"false\">").Append(WebUtility.HtmlEncode(item.Guid)).Append("</guid>");
            }

            builder.Append("<title>").Append(WebUtility.HtmlEncode(item.Title)).Append("</title>");
            if (item.Link is not null)
            {
                builder.Append("<link>").Append(WebUtility.HtmlEncode(item.Link)).Append("</link>");
            }

            builder.Append("<description>").Append(WebUtility.HtmlEncode(item.Body)).Append("</description>");
            if (item.PubDate is not null)
            {
                builder.Append("<pubDate>").Append(item.PubDate).Append("</pubDate>");
            }

            if (item.Enclosure is not null)
            {
                builder.Append("<enclosure url=\"").Append(WebUtility.HtmlEncode(item.Enclosure)).Append("\" type=\"image/jpeg\" length=\"1\"/>");
            }

            builder.Append("</item>");
        }

        return builder.Append("</channel></rss>").ToString();
    }

    public static string Atom(params Item[] items)
    {
        var builder = new StringBuilder();
        builder.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?><feed xmlns=\"http://www.w3.org/2005/Atom\"><title>Atom example</title>");
        builder.Append("<link rel=\"alternate\" href=\"https://atom.example.com/\"/><id>urn:feed</id><updated>2026-10-26T08:00:00Z</updated>");
        foreach (var item in items)
        {
            builder.Append("<entry><id>").Append(WebUtility.HtmlEncode(item.Guid ?? "urn:x")).Append("</id><title>")
                .Append(WebUtility.HtmlEncode(item.Title)).Append("</title>");
            if (item.Link is not null)
            {
                builder.Append("<link rel=\"alternate\" href=\"").Append(WebUtility.HtmlEncode(item.Link)).Append("\"/>");
            }

            builder.Append("<updated>2026-10-26T08:00:00Z</updated><content type=\"html\">").Append(WebUtility.HtmlEncode(item.Body))
                .Append("</content></entry>");
        }

        return builder.Append("</feed>").ToString();
    }
}
