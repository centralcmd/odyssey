using Microsoft.Extensions.DependencyInjection;
using Odyssey.Core.FileStorage;
using Odyssey.Core.Internet.News.Files;

namespace Odyssey.Core.Internet.News;

public static class NewsServiceCollectionExtensions
{
    /// <summary>
    /// The news reader's shared registration (issue #470 §3.1), called by both hosts after
    /// <see cref="FileStorageServiceCollectionExtensions.AddFileStorage"/>. Today it declares the <c>NewsArticles</c>
    /// storage feature, so every host with volumes must bind it (check B1); the services, the article store and the
    /// worker job join it in later slices.
    /// </summary>
    public static IServiceCollection AddNewsReader(this IServiceCollection services)
    {
        services.AddSingleton(NewsArticleStorage.Feature);
        return services;
    }
}
