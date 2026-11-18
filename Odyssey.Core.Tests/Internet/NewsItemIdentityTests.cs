using System.Text;
using Odyssey.Context;
using Odyssey.Core.Internet.News.Content;
using Odyssey.Core.Internet.News.Html;
using Odyssey.Core.Internet.News.Identity;
using Odyssey.Core.Internet.News.Parsing;
using Odyssey.Dtos;
using Odyssey.Dtos.Internet;
using Xunit;

namespace Odyssey.Core.Tests.Internet;

/// <summary>
/// The per-item normaliser and the identity rules (issue #470 §7.10, §8.7, §8.8): field refusal (AC 18), item isolation
/// (AC 16), the omitted body and its hash (AC 75), edit detection (AC 14), and the resolution rules (AC 12, AC 13).
/// </summary>
public class NewsItemIdentityTests
{
    private static readonly Uri Base = new("https://site.example/");

    private static NewsParsedItem Item(string? title = "Title", string? body = "<p>Body</p>", string? guid = null, string? link = null, string? summary = null) => new()
    {
        Title = title is null ? null : new NewsTextField(title, NewsTextKind.Text),
        Content = body is null ? null : new NewsTextField(body, NewsTextKind.Html),
        Summary = summary is null ? null : new NewsTextField(summary, NewsTextKind.Html),
        Guid = guid,
        Link = link,
        BaseUrl = Base,
    };

    private static string Deep(int depth, string inner = "x") =>
        string.Concat(Enumerable.Repeat("<div>", depth)) + inner + string.Concat(Enumerable.Repeat("</div>", depth));

    // ── Normalising ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_body_is_plain_text_the_excerpt_prefers_the_summary_and_links_are_sanitised()
    {
        var item = NewsItemNormalizer.Normalize(Item(body: "<p>Para one.</p><p>Para two.</p>", summary: "<b>Summary</b> text", link: "/a?utm_source=x"));

        Assert.Equal("Para one.\n\nPara two.", item.Body);
        Assert.Equal(NewsText.CodePointLength(item.Body!), item.BodyLength);
        Assert.Equal("Summary text", item.Excerpt);
        Assert.Equal("https://site.example/a?utm_source=x", item.Link);
        Assert.Equal("site.example/a", item.LinkKey);
        Assert.Null(item.BodyOmittedReason);
    }

    [Fact]
    public void AC24_a_javascript_link_is_stored_as_null() =>
        Assert.Null(NewsItemNormalizer.Normalize(Item(link: "javascript:alert(document.cookie)")).Link);

    [Fact]
    public void AC16_a_10000_character_guid_a_5_MB_title_and_an_invalid_date_are_all_stored()
    {
        var guid = new string('g', 10_000);
        var title = new string('t', 5 * 1024 * 1024);
        var normalized = NewsItemNormalizer.Normalize(Item(title: title, guid: guid) with { PublishedAt = null });

        Assert.Equal(NewsLimits.MaxArticleTitleLength, NewsText.CodePointLength(normalized.Title));
        Assert.EndsWith(NewsText.Ellipsis, normalized.Title);
        Assert.StartsWith("g#", normalized.Keys.Guid!.Key, StringComparison.Ordinal);
        Assert.True(normalized.Keys.Guid.Key.Length <= NewsLimits.MaxIdentityKeyLength);
        Assert.Null(normalized.PublishedAtUtc);
    }

    [Fact]
    public void Images_come_in_section_6_2_order_with_inline_images_last_and_duplicates_once()
    {
        var parsed = Item(body: "<p><img src=\"/inline.png\" alt=\"i\"><img src=\"https://site.example/thumb.jpg\"></p>") with
        {
            Images =
            [
                new NewsParsedImage(NewsArticleImageOrigin.MediaThumbnail, "https://site.example/thumb.jpg", null, null, null),
                new NewsParsedImage(NewsArticleImageOrigin.Enclosure, "https://site.example/enc.jpg", null, null, null),
            ],
        };

        var images = NewsItemNormalizer.Normalize(parsed).Images;
        Assert.Equal(
            [
                (NewsArticleImageOrigin.Enclosure, "https://site.example/enc.jpg"),
                (NewsArticleImageOrigin.MediaThumbnail, "https://site.example/thumb.jpg"),
                (NewsArticleImageOrigin.InlineImg, "https://site.example/inline.png"),
            ],
            images.Select(i => (i.Origin, i.SourceUrl)));
    }

    // ── AC 18 / AC 75: refused fragments ────────────────────────────────────────────────────────────────

    [Fact]
    public void AC75_a_refused_body_is_omitted_with_its_reason_and_the_excerpt_falls_back_to_the_summary()
    {
        var item = NewsItemNormalizer.Normalize(Item(body: Deep(NewsLimits.MaxHtmlDepth + 1), summary: "<p>The summary</p>"));

        Assert.Equal(NewsBodyOmittedReason.TooComplex, item.BodyOmittedReason);
        Assert.Null(item.Body);
        Assert.Equal(0, item.BodyLength);
        Assert.Equal("The summary", item.Excerpt);
        Assert.Equal("Title", item.Title);
    }

    [Fact]
    public void AC75_an_oversized_body_is_TooLarge()
    {
        var item = NewsItemNormalizer.Normalize(Item(body: "<p>" + new string('x', NewsHtmlPrescan.MaxFragmentBytes) + "</p>"));
        Assert.Equal(NewsBodyOmittedReason.TooLarge, item.BodyOmittedReason);
        Assert.Equal(string.Empty, item.Excerpt);
    }

    [Fact]
    public void AC75_an_omitted_body_hashes_its_raw_bytes_so_a_one_byte_change_is_an_edit_and_no_change_is_not()
    {
        var first = NewsItemNormalizer.Normalize(Item(body: Deep(300, "a")));
        var same = NewsItemNormalizer.Normalize(Item(body: Deep(300, "a")));
        var changed = NewsItemNormalizer.Normalize(Item(body: Deep(300, "b")));

        Assert.Equal(first.ContentHash, same.ContentHash);
        Assert.NotEqual(first.ContentHash, changed.ContentHash);
        Assert.Equal(first.IdentityHash, changed.IdentityHash); // the identity uses the empty body
    }

    [Fact]
    public void AC18_a_refused_title_is_stored_empty_and_an_edit_confined_to_it_is_still_detected()
    {
        var htmlTitle = new NewsTextField(Deep(NewsLimits.MaxHtmlDepth + 1, "one"), NewsTextKind.Html);
        var first = NewsItemNormalizer.Normalize(Item() with { Title = htmlTitle });
        var edited = NewsItemNormalizer.Normalize(Item() with { Title = htmlTitle with { Value = Deep(NewsLimits.MaxHtmlDepth + 1, "two") } });

        Assert.Equal(string.Empty, first.Title);
        Assert.Equal(NewsHtmlRefusal.TooComplex, first.TitleRefusal);
        Assert.Null(first.BodyOmittedReason); // only the body sets it
        Assert.Equal("Body", first.Body);
        Assert.NotEqual(first.ContentHash, edited.ContentHash);
    }

    [Fact]
    public void AC18_a_refused_summary_follows_the_excerpt_fallback()
    {
        var item = NewsItemNormalizer.Normalize(Item(body: "<p>Body text</p>", summary: Deep(300)));
        Assert.Equal("Body text", item.Excerpt);
        Assert.Null(item.BodyOmittedReason);
    }

    // ── AC 14: change detection ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AC14_a_change_to_the_body_tail_or_the_link_changes_the_content_hash_but_not_the_identity_hash()
    {
        var head = new string('a', NewsItemNormalizer.IdentityBodyCodePoints + 100);
        var original = NewsItemNormalizer.Normalize(Item(body: $"<p>{head}end</p>", link: "https://site.example/a"));
        var tail = NewsItemNormalizer.Normalize(Item(body: $"<p>{head}END</p>", link: "https://site.example/a"));
        var moved = NewsItemNormalizer.Normalize(Item(body: $"<p>{head}end</p>", link: "https://site.example/b"));

        Assert.NotEqual(original.ContentHash, tail.ContentHash);
        Assert.NotEqual(original.ContentHash, moved.ContentHash);
        Assert.Equal(original.IdentityHash, tail.IdentityHash);
        Assert.Equal(original.IdentityHash, moved.IdentityHash);
    }

    [Fact]
    public void Hashes_are_stable_and_32_bytes()
    {
        var a = NewsItemNormalizer.Normalize(Item());
        var b = NewsItemNormalizer.Normalize(Item());
        Assert.Equal(a.ContentHash, b.ContentHash);
        Assert.Equal(a.IdentityHash, b.IdentityHash);
        Assert.Equal(NewsLimits.HashLength, a.ContentHash.Length);
        Assert.Equal(NewsLimits.HashLength, a.Keys.Hash.Hash.Length);
        Assert.StartsWith("h:", a.Keys.Hash.Key, StringComparison.Ordinal);
    }

    // ── AC 12 / AC 13: identity resolution across fetches ───────────────────────────────────────────────

    /// <summary>A feed's identity store across fetches: applies each resolution as the ingestor would.</summary>
    private sealed class FeedStore
    {
        private readonly List<(Guid ArticleId, NewsArticleIdentityKind Kind, byte[] Hash)> identities = [];

        public HashSet<Guid> Articles { get; } = [];

        public IReadOnlyList<NewsIdentityResolution> Fetch(params NewsParsedItem[] items)
        {
            var keys = items.Select(i => NewsItemNormalizer.Normalize(i).Keys).ToList();
            var results = NewsIdentityResolver.Resolve(keys, new NewsIdentitySnapshot(identities));
            foreach (var result in results)
            {
                if (result.IsNew)
                {
                    Articles.Add(result.ArticleId);
                }

                foreach (var key in result.KeysToAttach)
                {
                    Assert.DoesNotContain(identities, row => row.Hash.AsSpan().SequenceEqual(key.Hash)); // the unique index never fires
                    identities.Add((result.ArticleId, key.Kind, key.Hash));
                }
            }

            return results;
        }
    }

    [Fact]
    public void AC12_a_changed_guid_with_the_same_link_while_the_old_guid_is_absent_is_matched()
    {
        var store = new FeedStore();
        var first = store.Fetch(Item(guid: "old", link: "https://site.example/a"))[0];
        var second = store.Fetch(Item(guid: "new", link: "https://site.example/a"))[0];

        Assert.False(second.IsNew);
        Assert.Equal(first.ArticleId, second.ArticleId);
    }

    [Fact]
    public void AC12_a_new_guid_sharing_a_link_with_an_item_whose_guid_is_still_present_is_new()
    {
        var store = new FeedStore();
        store.Fetch(Item(guid: "one", link: "https://site.example/a"));
        var second = store.Fetch(Item(guid: "one", link: "https://site.example/z"), Item(title: "Other", guid: "two", link: "https://site.example/a"));

        Assert.False(second[0].IsNew);
        Assert.True(second[1].IsNew);
    }

    [Fact]
    public void AC12_every_item_sharing_one_link_keeps_each_guid_its_own_article_across_3_fetches()
    {
        var store = new FeedStore();
        NewsParsedItem[] Items() => Enumerable.Range(1, 3).Select(i => Item(title: $"T{i}", guid: $"g{i}", link: "https://site.example/live")).ToArray();
        for (var fetch = 0; fetch < 3; fetch++)
        {
            store.Fetch(Items());
        }

        Assert.Equal(3, store.Articles.Count);
    }

    [Fact]
    public void AC12_an_http_to_https_move_of_a_guidless_feed_creates_no_duplicates()
    {
        var store = new FeedStore();
        store.Fetch(Item(link: "http://www.site.example/a"), Item(title: "B", link: "http://www.site.example/b"));
        var moved = store.Fetch(Item(link: "https://site.example/a"), Item(title: "B", link: "https://site.example/b"));

        Assert.All(moved, r => Assert.False(r.IsNew));
        Assert.Equal(2, store.Articles.Count);
    }

    [Fact]
    public void AC12_an_item_with_no_guid_no_link_and_under_280_characters_is_stored_once_across_3_fetches()
    {
        var store = new FeedStore();
        for (var fetch = 0; fetch < 3; fetch++)
        {
            store.Fetch(Item(title: "Short", body: "<p>tiny</p>"));
        }

        Assert.Single(store.Articles);
    }

    [Fact]
    public void AC12_two_items_in_one_fetch_with_the_same_guid_are_both_stored()
    {
        var store = new FeedStore();
        var results = store.Fetch(Item(title: "A", guid: "dup", link: "https://site.example/a"), Item(title: "B", guid: "dup", link: "https://site.example/b"));

        Assert.All(results, r => Assert.True(r.IsNew));
        Assert.Equal(2, store.Articles.Count);
        Assert.All(results, r => Assert.DoesNotContain(r.KeysToAttach, k => k.Kind == NewsArticleIdentityKind.Guid));
    }

    [Fact]
    public void AC12_guidless_items_sharing_a_link_and_under_280_characters_are_each_stored_once_across_3_fetches()
    {
        var store = new FeedStore();
        for (var fetch = 0; fetch < 3; fetch++)
        {
            store.Fetch(Item(title: "One", link: "https://site.example/same"), Item(title: "Two", link: "https://site.example/same"));
        }

        Assert.Equal(2, store.Articles.Count);
    }

    [Fact]
    public void AC13_republishing_with_at_least_280_characters_matches_by_hash_and_below_that_is_new()
    {
        var longBody = "<p>" + new string('w', 300) + "</p>";
        var store = new FeedStore();
        var original = store.Fetch(Item(title: "Long", body: longBody, link: "https://site.example/old"))[0];
        var republished = store.Fetch(Item(title: "Long", body: longBody, link: "https://site.example/new"))[0];
        Assert.Equal(original.ArticleId, republished.ArticleId);

        var shortStore = new FeedStore();
        shortStore.Fetch(Item(title: "Short", body: "<p>brief</p>", link: "https://site.example/old"));
        Assert.True(shortStore.Fetch(Item(title: "Short", body: "<p>brief</p>", link: "https://site.example/new"))[0].IsNew);
    }

    [Fact]
    public void Identical_short_items_in_one_fetch_merge_into_the_first()
    {
        var results = new FeedStore().Fetch(Item(title: "Same", body: "<p>x</p>"), Item(title: "Same", body: "<p>x</p>"));

        Assert.True(results[0].IsNew);
        Assert.False(results[1].IsNew);
        Assert.True(results[1].DuplicateOfEarlierItem);
        Assert.Equal(results[0].ArticleId, results[1].ArticleId);
    }

    [Fact]
    public void No_article_gets_more_than_32_keys()
    {
        var known = Enumerable.Range(0, NewsLimits.MaxIdentitiesPerArticle)
            .Select(i => (Guid.Empty, NewsArticleIdentityKind.ContentHash, NewsHashing.Sha256($"k{i}")))
            .Append((Guid.Empty, NewsArticleIdentityKind.Guid, NewsIdentityKey.Create(NewsArticleIdentityKind.Guid, "g:full").Hash))
            .ToList();
        var item = NewsItemNormalizer.Normalize(Item(guid: "full", body: "<p>new body</p>")).Keys;

        var result = NewsIdentityResolver.Resolve([item], new NewsIdentitySnapshot(known)).Single();
        Assert.Equal(Guid.Empty, result.ArticleId);
        Assert.Empty(result.KeysToAttach);
    }

    [Fact]
    public void Keys_have_the_documented_prefixes()
    {
        var keys = NewsItemNormalizer.Normalize(Item(guid: "abc", link: "https://www.site.example/x")).Keys;
        Assert.Equal("g:abc", keys.Guid!.Key);
        Assert.Equal("l:site.example/x", keys.Link!.Key);
        Assert.Equal(2 + 64, Encoding.UTF8.GetByteCount(keys.Hash.Key));
    }
}
