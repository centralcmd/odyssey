using System.Linq.Expressions;
using Odyssey.Context;
using Odyssey.Dtos.Internet;

namespace Odyssey.Core.Internet.News.Reading;

/// <summary>
/// The article list's Status filter: Unread, Read and Updated OR'ed together. Updated — read, then changed by the
/// publisher (<c>ContentChangedAtUtc &gt; ReadAtUtc</c>) — is a subset of Read, so the set is normalised before it is
/// applied or hashed: Read absorbs Updated, and Unread with Read is no filter at all.
/// </summary>
public static class NewsReadStatusFilter
{
    /// <summary>Read and changed by the publisher since it was read.</summary>
    public static readonly Expression<Func<NewsArticle, bool>> IsUpdatedSinceRead =
        a => a.IsRead && a.ContentChangedAtUtc != null && a.ReadAtUtc != null && a.ContentChangedAtUtc > a.ReadAtUtc;

    /// <summary>The same test on a materialised article.</summary>
    public static bool UpdatedSinceRead(NewsArticle article) =>
        article.IsRead && article.ContentChangedAtUtc is { } changed && article.ReadAtUtc is { } read && changed > read;

    /// <summary>The canonical member set; empty means every article.</summary>
    public static IReadOnlyList<NewsArticleReadStatus> Normalize(bool? isRead, IEnumerable<NewsArticleReadStatus>? statuses)
    {
        var set = new HashSet<NewsArticleReadStatus>(statuses ?? []);
        set.Remove(NewsArticleReadStatus.Unknown);
        if (isRead is { } read)
        {
            set.Add(read ? NewsArticleReadStatus.Read : NewsArticleReadStatus.Unread);
        }

        if (set.Contains(NewsArticleReadStatus.Read))
        {
            set.Remove(NewsArticleReadStatus.Updated);
        }

        if (set.Contains(NewsArticleReadStatus.Unread) && set.Contains(NewsArticleReadStatus.Read))
        {
            set.Clear();
        }

        return [.. set.Order()];
    }

    /// <summary>
    /// The cursor-hash token. The single-member Unread and Read sets keep the tokens the older <c>isRead</c> filter
    /// wrote, so a cursor issued before the Status filter existed still continues.
    /// </summary>
    public static string HashToken(IReadOnlyList<NewsArticleReadStatus> statuses) => statuses switch
    {
        [] => "-",
        [NewsArticleReadStatus.Unread] => "0",
        [NewsArticleReadStatus.Read] => "1",
        _ => "s" + string.Join(',', statuses.Select(s => (int)s)),
    };

    /// <summary>Applies a normalised set.</summary>
    public static IQueryable<NewsArticle> Apply(IQueryable<NewsArticle> articles, IReadOnlyList<NewsArticleReadStatus> statuses) => statuses switch
    {
        [] => articles,
        [NewsArticleReadStatus.Unread] => articles.Where(a => !a.IsRead),
        [NewsArticleReadStatus.Read] => articles.Where(a => a.IsRead),
        [NewsArticleReadStatus.Updated] => articles.Where(IsUpdatedSinceRead),
        // Unread or Updated: the only two-member set left after normalisation.
        _ => articles.Where(a => !a.IsRead
            || (a.ContentChangedAtUtc != null && a.ReadAtUtc != null && a.ContentChangedAtUtc > a.ReadAtUtc)),
    };
}
