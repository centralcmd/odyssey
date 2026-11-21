using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Odyssey.Core.FileStorage;
using Odyssey.Core.Internet.News.Fetching;
using Odyssey.Core.Internet.News.Files;
using Odyssey.Core.Internet.News.Jobs;
using Odyssey.Core.Jobs;

namespace Odyssey.Core.Internet.News;

public static class NewsServiceCollectionExtensions
{
    /// <summary>
    /// The news reader's shared registration (issue #470 §3.1), called by both hosts after
    /// <see cref="FileStorageServiceCollectionExtensions.AddFileStorage"/>. It declares the <c>NewsArticles</c> storage
    /// feature, so every host with volumes must bind it (check B1), and the egress client: the named
    /// <see cref="NewsEgress.HttpClientName"/> client over <see cref="NewsEgress.CreateHandler"/>, whose connect-time
    /// check (<see cref="NewsConnectGuard"/>) applies the address policy. Only the worker sends with it (§7.9: the API
    /// makes no outbound request). Its default loggers are removed, because they log the request URL and a feed URL can
    /// carry a token (§7.13): the caller logs hosts only.
    /// </summary>
    public static IServiceCollection AddNewsReader(this IServiceCollection services)
    {
        services.AddSingleton(NewsArticleStorage.Feature);
        // Podcasts (issue #556 §3.2): two more features every host with volumes must bind.
        services.AddSingleton(Podcasts.Files.PodcastAudioStorage.Feature);
        services.AddSingleton(Podcasts.Files.PodcastArtworkStorage.Feature);
        services.TryAddScoped<INewsSettingsLookup, NewsSettingsLookup>();
        services.TryAddSingleton<INewsCounterWriter, NewsCounterWriter>();
        services.TryAddSingleton<INewsFetchStateFlags, NewsFetchStateFlags>();
        services.TryAddSingleton<Podcasts.IPodcastAudioRequests, Podcasts.PodcastAudioRequests>();

        services.TryAddSingleton<INewsDnsResolver, SystemNewsDnsResolver>();
        services.TryAddSingleton<NewsConnectGuard>();
        services.AddHttpClient(NewsEgress.HttpClientName, client =>
            {
                client.Timeout = NewsEgress.FeedTimeout;
                client.DefaultRequestHeaders.ConnectionClose = true;
            })
            .ConfigurePrimaryHttpMessageHandler(provider => NewsEgress.CreateHandler(provider.GetRequiredService<NewsConnectGuard>()))
            .RemoveAllLoggers();
        return services;
    }

    /// <summary>
    /// The fetch job's services (issue #470 §3.4), for <c>Odyssey.Worker</c> (and the tests): the ingestor, the image
    /// downloader, the purge, the maintenance, the host throttle and the article writer over the store. Call after
    /// <see cref="AddNewsReader"/>; the host registers <see cref="NewsFeedJob"/> itself with <c>AddPeriodicJob</c>.
    /// </summary>
    public static IServiceCollection AddNewsFeedFetching(this IServiceCollection services)
    {
        services.AddJobRunRecording();
        services.TryAddScoped<NewsArticleWriter>();
        services.TryAddScoped<NewsBucketAllocator>();
        services.TryAddSingleton<NewsHostThrottleGate>();
        services.TryAddSingleton<Podcasts.PodcastIngest>();
        services.TryAddSingleton<NewsIngestor>();
        services.TryAddSingleton<NewsFeedFetcher>();
        services.TryAddSingleton<NewsImageDownloader>();
        // Podcasts (issue #556 §3.4): the artwork and chapter-file phases and the writer over the two podcast features.
        services.TryAddScoped<Podcasts.Files.PodcastFileWriter>();
        services.TryAddSingleton<Podcasts.PodcastArtworkPhase>();
        services.TryAddSingleton<Podcasts.PodcastChaptersPhase>();
        services.TryAddSingleton<Journal.Thumbnails.PhotoThumbnailProcessHooks>();
        services.TryAddSingleton<NewsFeedPurger>();
        services.TryAddSingleton<NewsMaintenance>();
        return services;
    }
}
