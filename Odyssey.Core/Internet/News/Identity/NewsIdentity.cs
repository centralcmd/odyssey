using Odyssey.Context;
using Odyssey.Core.Internet.News.Content;
using Odyssey.Dtos;

namespace Odyssey.Core.Internet.News.Identity;

/// <summary>One identity key (§4.8): its kind, the stored <c>IdentityKey</c> text and its SHA-256 (<c>IdentityKeyHash</c>).</summary>
public sealed record NewsIdentityKey(NewsArticleIdentityKind Kind, string Key, byte[] Hash)
{
    public static NewsIdentityKey Create(NewsArticleIdentityKind kind, string key) => new(kind, key, NewsUrlKey.Hash(key));

    /// <summary>The hash as lower-case hex, for dictionary lookups.</summary>
    public string HashHex => NewsHashing.ToHex(Hash);
}

/// <summary>
/// An item's identity candidates (§8.8): <b>G</b> (<c>g:</c>, or <c>g#&lt;sha256&gt;</c> for a GUID over 2000
/// characters), <b>L</b> (<c>l:&lt;LinkKey&gt;</c>) and <b>H</b> (<c>h:&lt;IdentityHash&gt;</c>).
/// </summary>
public sealed record NewsItemKeys(NewsIdentityKey? Guid, NewsIdentityKey? Link, NewsIdentityKey Hash, int TitleAndBodyLength)
{
    /// <summary>The length below which <b>H</b> alone does not identify an item that has a usable link (§8.8 rule 3).</summary>
    public const int MinHashMatchLength = 280;

    public static NewsItemKeys For(string? guid, string? linkKey, byte[] identityHash, int titleAndBodyLength)
    {
        NewsIdentityKey? g = null;
        if (!string.IsNullOrEmpty(guid))
        {
            g = guid.Length > NewsItemNormalizer.MaxGuidLength
                ? NewsIdentityKey.Create(NewsArticleIdentityKind.Guid, "g#" + NewsHashing.ToHex(NewsHashing.Sha256(guid)))
                : NewsIdentityKey.Create(NewsArticleIdentityKind.Guid, "g:" + guid);
        }

        var l = linkKey is null ? null : NewsIdentityKey.Create(NewsArticleIdentityKind.Link, "l:" + linkKey);
        var h = NewsIdentityKey.Create(NewsArticleIdentityKind.ContentHash, "h:" + NewsHashing.ToHex(identityHash));
        return new NewsItemKeys(g, l, h, titleAndBodyLength);
    }
}

/// <summary>
/// What the ingestor knows about a feed's existing identities. Every lookup is by key hash within the one feed (the
/// unique index is <c>(NewsFeedId, IdentityKeyHash)</c>, so a key has at most one owner).
/// </summary>
public interface INewsIdentityLookup
{
    /// <summary>The article that owns the key, if any.</summary>
    Guid? OwnerOf(NewsIdentityKey key);

    /// <summary>The hashes (hex) of every <b>G</b> key the article owns.</summary>
    IReadOnlyCollection<string> GuidKeysOf(Guid articleId);

    /// <summary>How many keys the article already has (the cap is <see cref="NewsLimits.MaxIdentitiesPerArticle"/>).</summary>
    int KeyCountOf(Guid articleId);
}

/// <summary>An in-memory <see cref="INewsIdentityLookup"/> over identity rows the ingestor has loaded.</summary>
public sealed class NewsIdentitySnapshot : INewsIdentityLookup
{
    private readonly Dictionary<string, Guid> owners = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, List<string>> guidKeys = [];
    private readonly Dictionary<Guid, int> counts = [];

    public NewsIdentitySnapshot(IEnumerable<(Guid ArticleId, NewsArticleIdentityKind Kind, byte[] KeyHash)> identities)
    {
        foreach (var (articleId, kind, keyHash) in identities)
        {
            var hex = NewsHashing.ToHex(keyHash);
            if (!owners.TryAdd(hex, articleId))
            {
                continue;
            }

            counts[articleId] = counts.GetValueOrDefault(articleId) + 1;
            if (kind == NewsArticleIdentityKind.Guid)
            {
                (guidKeys.TryGetValue(articleId, out var list) ? list : guidKeys[articleId] = []).Add(hex);
            }
        }
    }

    public Guid? OwnerOf(NewsIdentityKey key) => owners.TryGetValue(key.HashHex, out var owner) ? owner : null;

    public IReadOnlyCollection<string> GuidKeysOf(Guid articleId) => guidKeys.TryGetValue(articleId, out var list) ? list : [];

    public int KeyCountOf(Guid articleId) => counts.GetValueOrDefault(articleId);
}

/// <summary>How one item resolved (§8.8).</summary>
/// <param name="ArticleId">The existing article, or the id pre-allocated for a new one (use it as <c>NewsArticleId</c>).</param>
/// <param name="IsNew">The item creates <paramref name="ArticleId"/>.</param>
/// <param name="DuplicateOfEarlierItem">
/// An earlier item of this same fetch already resolved to the same article (accepted, §8.8: two short items with
/// identical text and no GUID or link merge), so the ingestor applies only the first.
/// </param>
/// <param name="KeysToAttach">The keys to insert for the article: owned by nobody, not shared or unreliable in this fetch, within the cap.</param>
public sealed record NewsIdentityResolution(Guid ArticleId, bool IsNew, bool DuplicateOfEarlierItem, IReadOnlyList<NewsIdentityKey> KeysToAttach);

/// <summary>
/// The identity rules (issue #470 §8.8), pure over a lookup: before matching, GUIDs repeated within the fetch are
/// unreliable and LinkKeys repeated within it are shared; then
/// <list type="number">
/// <item>a reliable known G is that article;</item>
/// <item>a reliable unknown G is new — unless its L is known, unshared and its article A has none of its GUIDs in this
/// fetch, in which case the feed changed A's GUID;</item>
/// <item>with no reliable G but a usable L, a known L wins, else a known H when title plus body is at least 280
/// characters, else new;</item>
/// <item>with neither, H is authoritative at any length.</item>
/// </list>
/// Items are resolved in order and see the keys earlier items of the fetch attached, so the plan can be written
/// as-is and the unique index never fires on the ordinary path.
/// </summary>
public static class NewsIdentityResolver
{
    public static IReadOnlyList<NewsIdentityResolution> Resolve(IReadOnlyList<NewsItemKeys> items, INewsIdentityLookup known, Func<Guid>? newId = null)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(known);
        newId ??= System.Guid.NewGuid;

        var guidCounts = Count(items.Select(i => i.Guid?.HashHex));
        var linkCounts = Count(items.Select(i => i.Link?.HashHex));
        var guidsInFetch = guidCounts.Keys.ToHashSet(StringComparer.Ordinal);

        // Keys attached by earlier items of this fetch, and their articles' key counts.
        var attached = new Dictionary<string, Guid>(StringComparer.Ordinal);
        var addedCounts = new Dictionary<Guid, int>();
        var resolved = new HashSet<Guid>();

        Guid? Owner(NewsIdentityKey? key) =>
            key is null ? null : attached.TryGetValue(key.HashHex, out var a) ? a : known.OwnerOf(key);

        var results = new List<NewsIdentityResolution>(items.Count);
        foreach (var item in items)
        {
            var reliableGuid = item.Guid is { } g && guidCounts[g.HashHex] == 1 ? g : null;
            var usableLink = item.Link is { } l && linkCounts[l.HashHex] == 1 ? l : null;

            Guid? match;
            if (reliableGuid is not null)
            {
                match = Owner(reliableGuid);
                if (match is null && usableLink is not null && Owner(usableLink) is { } byLink
                    && !known.GuidKeysOf(byLink).Any(guidsInFetch.Contains))
                {
                    match = byLink; // rule 2: the feed changed this article's GUID
                }
            }
            else if (usableLink is not null)
            {
                match = Owner(usableLink)
                    ?? (item.TitleAndBodyLength >= NewsItemKeys.MinHashMatchLength ? Owner(item.Hash) : null);
            }
            else
            {
                match = Owner(item.Hash);
            }

            var isNew = match is null;
            var articleId = match ?? newId();
            var duplicate = !resolved.Add(articleId);

            var keys = new List<NewsIdentityKey>();
            var count = (isNew ? 0 : known.KeyCountOf(articleId)) + addedCounts.GetValueOrDefault(articleId);
            foreach (var key in new[] { reliableGuid, usableLink, item.Hash })
            {
                if (key is null || Owner(key) is not null || count >= NewsLimits.MaxIdentitiesPerArticle)
                {
                    continue;
                }

                keys.Add(key);
                attached[key.HashHex] = articleId;
                count++;
            }

            addedCounts[articleId] = count - (isNew ? 0 : known.KeyCountOf(articleId));
            results.Add(new NewsIdentityResolution(articleId, isNew, duplicate, keys));
        }

        return results;
    }

    private static Dictionary<string, int> Count(IEnumerable<string?> hashes)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var hash in hashes)
        {
            if (hash is not null)
            {
                counts[hash] = counts.GetValueOrDefault(hash) + 1;
            }
        }

        return counts;
    }
}
