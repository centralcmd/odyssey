using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Odyssey.Core.FileStorage;
using Odyssey.Core.Internet.News.Fetching;
using Odyssey.Core.Internet.News.Files;

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
}
