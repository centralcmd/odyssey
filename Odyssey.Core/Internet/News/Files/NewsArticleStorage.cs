using Odyssey.Context;
using Odyssey.Core.FileStorage;

namespace Odyssey.Core.Internet.News.Files;

/// <summary>
/// The news article feature on the #346 store (issue #470 §3.2):
/// <c>news/&lt;feedId:N&gt;/&lt;bucket&gt;/&lt;articleId:N&gt;/&lt;storedObjectId:N&gt;.&lt;ext&gt;</c>. Opaque names (a user
/// decision), so no publisher-supplied name and no user text reaches the disk or a database path; released after the
/// deletion grace like every production feature. <see cref="NewsArticleFile"/> is its one referencing table.
/// </summary>
public static class NewsArticleStorage
{
    public const string FeatureKey = "NewsArticles";

    public const string RootFolder = "news";

    public static readonly StorageFeature Feature = StorageFeature.Create<NewsArticleLayout>(
        FeatureKey,
        RootFolder,
        args =>
        [
            args.FeedId.ToString("N"),
            args.Bucket.ToString(System.Globalization.CultureInfo.InvariantCulture),
            args.ArticleId.ToString("N"),
        ],
        StorageReleaseMode.Graced,
        StorageFeatureReference.To<NewsArticleFile>(f => f.StoredObjectId),
        StorageNamingMode.Opaque);
}

/// <summary>
/// The folder of one article: its feed, the bucket fixed at insert (<see cref="NewsArticle.Bucket"/>) and the article.
/// Every later file of the article — a revision, a late image — is written with the same three values.
/// </summary>
public sealed record NewsArticleLayout(Guid FeedId, int Bucket, Guid ArticleId);
