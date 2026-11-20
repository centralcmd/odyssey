using System.Diagnostics;
using System.Net;
using System.Text;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Odyssey.Context;
using Odyssey.Core.Internet.News;
using Odyssey.Core.Internet.News.Opml;
using Odyssey.Core.Internet.News.Reading;
using Odyssey.Dtos;
using Odyssey.Dtos.Internet;
using Odyssey.Testing;
using Xunit;

namespace Odyssey.Core.Tests.Internet;

/// <summary>
/// The OPML import and export services of issue #508 over EF InMemory. Test names carry the AC they pin. The
/// MariaDB-only criteria (collation, concurrent tag create, the <c>UrlKeyHash</c> race, the lookup count, the
/// migration) live in <c>Odyssey.IntegrationTests</c>.
/// </summary>
[Collection(MemoryProbeCollection.Name)]
public sealed class NewsOpmlServiceTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 11, 17, 17, 0, 0, DateTimeKind.Utc);

    private readonly string database = $"news-opml-{Guid.NewGuid():N}";
    private readonly FakeTimeProvider time = new(new DateTimeOffset(Now));
    private readonly FakeNewsSettings settings = new();
    private readonly RecordingLogger logs = new();
    private readonly List<OdysseyContext> contexts = [];

    public void Dispose()
    {
        foreach (var context in contexts)
        {
            context.Dispose();
        }
    }

    private OdysseyContext Context(string? name = null)
    {
        var context = new OdysseyContext(new DbContextOptionsBuilder<OdysseyContext>().UseInMemoryDatabase(name ?? database).Options);
        contexts.Add(context);
        return context;
    }

    private NewsOpmlImportService Import(OdysseyContext? context = null)
    {
        var ctx = context ?? Context();
        return new NewsOpmlImportService(ctx, settings, new NewsTagService(ctx, time, NullLogger<NewsTagService>.Instance), time,
            new ForwardingLogger<NewsOpmlImportService>(logs));
    }

    private NewsOpmlExportService Export(OdysseyContext? context = null) =>
        new(context ?? Context(), time, new ForwardingLogger<NewsOpmlExportService>(logs));

    private Task<NewsOpmlImportResult> ImportAsync(string xml, bool canCreateTags = true, OdysseyContext? context = null) =>
        ImportAsync(Encoding.UTF8.GetBytes(xml), canCreateTags, context);

    private async Task<NewsOpmlImportResult> ImportAsync(byte[] bytes, bool canCreateTags = true, OdysseyContext? context = null)
    {
        using var stream = new MemoryStream(bytes);
        return await Import(context).ImportAsync(stream, canCreateTags, "user-1", CancellationToken.None);
    }

    private static string Opml(params string[] outlines) =>
        $"<opml version=\"2.0\"><body>{string.Concat(outlines)}</body></opml>";

    private static string Feed(string url, string? text = null, string? category = null, string? extra = null) =>
        $"<outline type=\"rss\" xmlUrl=\"{url}\"{(text is null ? "" : $" text=\"{text}\"")}{(category is null ? "" : $" category=\"{category}\"")} {extra}/>";

    private async Task<Guid> SeedFeedAsync(
        string url, string? customTitle = null, string? title = "Publisher", bool active = true, bool archived = false,
        bool pending = false, int? interval = null, bool images = true, string? siteUrl = null, params Guid[] tagIds)
    {
        var context = Context();
        var id = Guid.NewGuid();
        context.NewsFeeds.Add(new NewsFeed
        {
            NewsFeedId = id,
            Url = url,
            UrlKeyHash = NewsUrlKey.Hash(NewsUrlKey.UrlKey(url)!),
            CustomTitle = customTitle,
            FetchIntervalMinutes = interval,
            DownloadImages = images,
            IsActive = active,
            CreatedAtUtc = Now,
            ArchivedAtUtc = archived ? Now : null,
            DeletionRequestedAtUtc = pending ? Now : null,
            SettingsVersion = Guid.NewGuid(),
        });
        context.NewsFeedFetchStates.Add(new NewsFeedFetchState { NewsFeedId = id, FetchFence = Guid.NewGuid(), NextFetchAtUtc = Now, Title = title, SiteUrl = siteUrl });
        context.NewsFeedCounters.Add(new NewsFeedCounter { NewsFeedId = id });
        context.NewsFeedTags.AddRange(tagIds.Select(t => new NewsFeedTag { NewsFeedTagId = Guid.NewGuid(), NewsFeedId = id, NewsTagId = t }));
        await context.SaveChangesAsync();
        return id;
    }

    private async Task<Guid> SeedTagAsync(string name, bool archived = false)
    {
        var context = Context();
        var id = Guid.NewGuid();
        context.NewsTags.Add(new NewsTag
        {
            NewsTagId = id, Name = name, NameKey = NewsTagService.NameKey(name), CreatedAtUtc = Now, UpdatedAtUtc = Now,
            ArchivedAtUtc = archived ? Now : null,
        });
        await context.SaveChangesAsync();
        return id;
    }

    private async Task<string> ExportAsync(IReadOnlyCollection<Guid>? ids = null, OdysseyContext? context = null)
    {
        var export = Export(context);
        var document = await export.LoadAsync(ids, "user-1", CancellationToken.None);
        using var stream = new MemoryStream();
        await OpmlDocumentWriter.WriteAsync(document, stream, CancellationToken.None);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static readonly XNamespace Ody = OpmlDocumentReader.OdysseyNamespace;

    // ── Export ────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AC1_AC2_AC3_The_export_is_opml_2_with_folders_categories_and_the_extension()
    {
        var local = await SeedTagAsync("Local");
        var norway = await SeedTagAsync("Norway");
        await SeedFeedAsync("https://harbour.example/rss", customTitle: "Harbour Times", interval: 60, siteUrl: "https://harbour.example/", tagIds: [local, norway]);
        await SeedFeedAsync("https://other.example/feed", title: "Untagged feed");
        await SeedFeedAsync("https://paused.example/feed", active: false, tagIds: [local]);
        await SeedFeedAsync("https://archived.example/feed", archived: true, images: false);
        await SeedFeedAsync("https://gone.example/feed", pending: true);

        var xml = XDocument.Parse(await ExportAsync());

        var root = xml.Root!;
        Assert.Equal("opml", root.Name.LocalName);
        Assert.Equal("2.0", (string?)root.Attribute("version"));
        Assert.Equal(OpmlDocumentReader.OdysseyNamespace, (string?)root.Attribute(XNamespace.Xmlns + "odyssey"));
        var body = root.Element("body")!;
        var folders = body.Elements("outline").Where(o => o.Attribute("xmlUrl") is null).ToList();
        Assert.Equal(["Local", "Norway"], folders.Select(f => (string)f.Attribute("text")!));

        // A feed with two tags appears under both folders with both in category.
        var harbour = body.Descendants("outline").Where(o => (string?)o.Attribute("xmlUrl") == "https://harbour.example/rss").ToList();
        Assert.Equal(2, harbour.Count);
        Assert.All(harbour, h => Assert.Equal("/Local,/Norway", (string?)h.Attribute("category")));
        Assert.Equal("Harbour Times", (string?)harbour[0].Attribute(Ody + "customTitle"));
        Assert.Equal("60", (string?)harbour[0].Attribute(Ody + "fetchIntervalMinutes"));
        Assert.Equal("https://harbour.example/", (string?)harbour[0].Attribute("htmlUrl"));
        Assert.Equal("rss", (string?)harbour[0].Attribute("type"));

        // Untagged feeds are top-level, after the folders; pending deletion is not exported.
        var top = body.Elements("outline").Where(o => o.Attribute("xmlUrl") is not null).ToList();
        Assert.Equal(2, top.Count);
        Assert.DoesNotContain(body.Descendants("outline"), o => (string?)o.Attribute("xmlUrl") == "https://gone.example/feed");
        var untagged = top.Single(o => (string?)o.Attribute("xmlUrl") == "https://other.example/feed");
        Assert.Equal("Untagged feed", (string?)untagged.Attribute("text"));
        Assert.Null(untagged.Attribute(Ody + "customTitle"));
        Assert.Null(untagged.Attribute(Ody + "fetchIntervalMinutes"));
        Assert.Null(untagged.Attribute("htmlUrl"));
        Assert.Equal("true", (string?)untagged.Attribute(Ody + "active"));

        // AC 3: paused and archived write active="false".
        Assert.Equal("false", (string?)body.Descendants("outline").First(o => (string?)o.Attribute("xmlUrl") == "https://paused.example/feed").Attribute(Ody + "active"));
        var archived = top.Single(o => (string?)o.Attribute("xmlUrl") == "https://archived.example/feed");
        Assert.Equal("false", (string?)archived.Attribute(Ody + "active"));
        Assert.Equal("false", (string?)archived.Attribute(Ody + "downloadImages"));
    }

    [Fact]
    public async Task AC4_Markup_controls_and_a_lone_surrogate_in_a_title_export_well_formed()
    {
        await SeedFeedAsync("https://x.example/rss", customTitle: "A <b> & \"q\" \u0001bell \uD800lone \uFFFEend");

        var text = await ExportAsync();
        var outline = XDocument.Parse(text).Descendants("outline").Single();

        Assert.Equal("A <b> & \"q\" bell lone end", (string?)outline.Attribute(Ody + "customTitle"));
        Assert.Contains("&lt;b&gt; &amp; &quot;q&quot;", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AC39_AC40_A_filtered_export_writes_the_named_feeds_once_and_only_their_tags()
    {
        var a = await SeedTagAsync("Alpha");
        var b = await SeedTagAsync("Beta");
        var c = await SeedTagAsync("Gamma");
        var one = await SeedFeedAsync("https://one.example/rss", tagIds: [a]);
        var two = await SeedFeedAsync("https://two.example/rss", tagIds: [b]);
        var three = await SeedFeedAsync("https://three.example/rss");
        await SeedFeedAsync("https://four.example/rss", tagIds: [c]);
        var pending = await SeedFeedAsync("https://pending.example/rss", pending: true);

        var document = await Export().LoadAsync([one, two, three, one, pending, Guid.NewGuid()], "user-1", CancellationToken.None);

        Assert.Equal(3, document.Feeds.Count);
        Assert.Equal(["Alpha", "Beta"], document.Folders);
        Assert.Contains(logs.Lines, l => l.Contains("(filtered)", StringComparison.Ordinal) && l.Contains(": 3 feeds", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AC41_A_list_of_only_unknown_ids_writes_a_valid_document_with_an_empty_body()
    {
        var xml = await ExportAsync([Guid.NewGuid()]);

        var body = XDocument.Parse(xml).Root!.Element("body")!;
        Assert.Empty(body.Elements());
        var result = await ImportAsync(xml, context: Context($"empty-{Guid.NewGuid():N}"));
        Assert.Equal(0, result.Feeds);
    }

    // ── Round trip ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AC6_An_export_reimported_into_an_empty_store_restores_every_feed()
    {
        string[] names = ["100%", "a,b", "a/b", "a%2Fb", "Local"];
        var tags = new List<Guid>();
        foreach (var name in names)
        {
            tags.Add(await SeedTagAsync(name));
        }

        await SeedFeedAsync("https://a.example/rss", customTitle: "Mine", interval: 45, images: false, active: false, tagIds: [.. tags]);
        await SeedFeedAsync("https://b.example/rss", title: "Publisher title", tagIds: [tags[4]]);
        await SeedFeedAsync("https://c.example/rss", archived: true);
        var xml = await ExportAsync();

        var target = Context($"target-{Guid.NewGuid():N}");
        var result = await ImportAsync(xml, context: target);

        Assert.Equal((3, 3, 0, 0), (result.Feeds, result.Created, result.Invalid, result.FeedLimit));
        Assert.Equal(4, result.MergedOutlines); // a.example under five folders, b.example under one
        Assert.Equal(names.Order(StringComparer.Ordinal), result.TagsCreated.Order(StringComparer.Ordinal));
        var feeds = await target.NewsFeeds.AsNoTracking().ToListAsync();
        var a = feeds.Single(f => f.Url == "https://a.example/rss");
        Assert.Equal(("Mine", 45, false, false), (a.CustomTitle, a.FetchIntervalMinutes, a.DownloadImages, a.IsActive));
        var b = feeds.Single(f => f.Url == "https://b.example/rss");
        Assert.Null(b.CustomTitle); // a round trip never freezes a publisher title as custom
        Assert.True(b.IsActive);
        Assert.False(feeds.Single(f => f.Url == "https://c.example/rss").IsActive);

        var linked = await (from link in target.NewsFeedTags.AsNoTracking()
                            join tag in target.NewsTags.AsNoTracking() on link.NewsTagId equals tag.NewsTagId
                            where link.NewsFeedId == a.NewsFeedId
                            select tag.Name).ToListAsync();
        Assert.Equal(names.Order(StringComparer.Ordinal), linked.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task AC6_A_store_with_the_entry_cap_at_100_imports_its_own_60_feed_two_tag_export()
    {
        var x = await SeedTagAsync("X");
        var y = await SeedTagAsync("Y");
        for (var i = 0; i < 60; i++)
        {
            await SeedFeedAsync($"https://f{i}.example/rss", tagIds: [x, y]);
        }

        settings.Settings = settings.Settings with { OpmlMaxImportEntries = 100, MaxFeeds = 1000 };
        var result = await ImportAsync(await ExportAsync(), context: Context($"cap-{Guid.NewGuid():N}"));

        Assert.Equal((60, 60, 60), (result.Feeds, result.Created, result.MergedOutlines));
    }

    // ── Import ────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AC9_A_url_differing_only_by_host_case_or_a_trailing_slash_is_already_subscribed_and_unchanged()
    {
        var tag = await SeedTagAsync("Kept");
        var existing = await SeedFeedAsync("https://news.example.org", tagIds: [tag]);
        var version = (await Context().NewsFeeds.AsNoTracking().SingleAsync()).SettingsVersion;

        var result = await ImportAsync(Opml(Feed("HTTPS://News.Example.ORG/", category: "/Added")));

        var item = Assert.Single(result.Items);
        Assert.Equal((NewsOpmlImportOutcome.AlreadySubscribed, (Guid?)existing), (item.Outcome, item.FeedId));
        var feed = await Context().NewsFeeds.AsNoTracking().SingleAsync();
        Assert.Equal(version, feed.SettingsVersion);
        Assert.Equal([tag], await Context().NewsFeedTags.AsNoTracking().Select(l => l.NewsTagId).ToListAsync());
        Assert.Empty(result.TagsCreated);
        Assert.Equal(1, await Context().NewsTags.CountAsync());
    }

    [Fact]
    public async Task AC9_A_match_on_a_feed_pending_deletion_is_pending_deletion_with_no_id()
    {
        await SeedFeedAsync("https://gone.example/rss", pending: true);

        var item = Assert.Single((await ImportAsync(Opml(Feed("https://gone.example/rss")))).Items);

        Assert.Equal(NewsOpmlImportOutcome.PendingDeletion, item.Outcome);
        Assert.Null(item.FeedId);
    }

    [Fact]
    public async Task AC10_Two_outlines_with_one_url_key_are_one_feed_with_the_union_of_their_tags()
    {
        var result = await ImportAsync(Opml(
            "<outline text=\"F1\">" + Feed("https://x.example/rss?utm_source=a", text: "First", category: "/C1") + "</outline>",
            "<outline text=\"F2\">" + Feed("https://X.example/rss", text: "Second", category: "/C1,/C2") + "</outline>"));

        Assert.Equal((1, 1, 1), (result.Feeds, result.MergedOutlines, result.Created));
        var item = Assert.Single(result.Items);
        Assert.Equal("First", item.Title);
        Assert.Equal(["F1", "C1", "F2", "C2"], item.Tags);
        Assert.Equal("https://x.example/rss?utm_source=a", (await Context().NewsFeeds.SingleAsync()).Url);
    }

    [Fact]
    public async Task AC11_With_room_for_three_feeds_the_outcomes_follow_document_order()
    {
        settings.Settings = settings.Settings with { MaxFeeds = 4 };
        await SeedFeedAsync("https://existing.example/rss");

        var result = await ImportAsync(Opml(
            Feed("https://n1.example/rss"), Feed("https://n2.example/rss"), Feed("ftp://invalid.example/"),
            Feed("https://n3.example/rss"), Feed("https://existing.example/rss"), Feed("https://n4.example/rss"),
            Feed("https://n5.example/rss")));

        Assert.Equal((7, 3, 2, 1, 1), (result.Feeds, result.Created, result.FeedLimit, result.Invalid, result.AlreadySubscribed));
        Assert.Equal(
            [NewsOpmlImportOutcome.Created, NewsOpmlImportOutcome.Created, NewsOpmlImportOutcome.Created,
             NewsOpmlImportOutcome.Invalid, NewsOpmlImportOutcome.AlreadySubscribed, NewsOpmlImportOutcome.FeedLimit, NewsOpmlImportOutcome.FeedLimit],
            result.Items.Select(i => i.Outcome));
        Assert.Equal([1, 2, 4, 3, 5, 6, 7], result.Items.Select(i => i.Position));
        Assert.Equal(4, await Context().NewsFeeds.CountAsync());
    }

    [Theory]
    [InlineData("http://10.0.0.5/feed", "This address cannot be fetched.")]
    [InlineData("ftp://x/", "A feed address must start with http:// or https://.")]
    [InlineData("https://user:pw@host.example/", "A feed address cannot carry a user name or password.")]
    [InlineData("https://host.example:8443/", "A feed address may use port 80 or 443 unless an administrator has granted its host another.")]
    public async Task AC13_A_refused_address_is_invalid_with_the_rules_message(string url, string message)
    {
        var item = Assert.Single((await ImportAsync(Opml(Feed(url)))).Items);

        Assert.Equal((NewsOpmlImportOutcome.Invalid, message), (item.Outcome, item.Message));
        Assert.Equal(0, await Context().NewsFeeds.CountAsync());
    }

    [Fact]
    public async Task AC13_A_granted_private_host_is_created()
    {
        settings.Grants = NewsPrivateHostAllowListRule.Parse("10.0.0.5");

        Assert.Equal(1, (await ImportAsync(Opml(Feed("http://10.0.0.5/feed")))).Created);
    }

    [Fact]
    public async Task AC13_AC22_Five_thousand_refused_feeds_report_at_most_a_thousand_rows_with_exact_counts()
    {
        var result = await ImportAsync(Opml([.. Enumerable.Range(0, 5000).Select(i => Feed($"ftp://bad{i}.example/"))]));

        Assert.Equal((5000, 5000), (result.Feeds, result.Invalid));
        Assert.Equal(1000, result.Items.Count);
        Assert.True(result.ItemsTruncated);
        Assert.Equal(Enumerable.Range(1, 1000), result.Items.Select(i => i.Position));
    }

    [Fact]
    public async Task AC15_Each_created_feed_has_its_fetch_state_due_now_and_its_counter_row()
    {
        var result = await ImportAsync(Opml(Feed("https://a.example/rss"), Feed("https://b.example/rss")));

        var context = Context();
        foreach (var item in result.Items)
        {
            var state = await context.NewsFeedFetchStates.SingleAsync(s => s.NewsFeedId == item.FeedId);
            Assert.Equal(Now, state.NextFetchAtUtc);
            Assert.NotEqual(Guid.Empty, state.FetchFence);
            Assert.True(await context.NewsFeedCounters.AnyAsync(c => c.NewsFeedId == item.FeedId));
        }

        Assert.Equal(2, (await context.NewsFeedFetchStates.Select(s => s.FetchFence).Distinct().ToListAsync()).Count);
    }

    [Fact]
    public async Task AC16_A_missing_tag_named_by_ten_feeds_is_created_once_and_bare()
    {
        var result = await ImportAsync(Opml([.. Enumerable.Range(0, 10).Select(i => Feed($"https://f{i}.example/rss", category: "/Shared"))]));

        Assert.Equal(["Shared"], result.TagsCreated);
        var tag = await Context().NewsTags.SingleAsync();
        Assert.Equal(("Shared", "shared", null, null, null), (tag.Name, tag.NameKey, tag.Description, tag.Icon, tag.Color));
        Assert.Equal(10, await Context().NewsFeedTags.CountAsync(l => l.NewsTagId == tag.NewsTagId));
    }

    [Fact]
    public async Task AC7_Without_news_tags_create_feeds_import_existing_tags_link_and_missing_ones_are_reported_once()
    {
        var existing = await SeedTagAsync("Existing");

        var result = await ImportAsync(
            Opml(Feed("https://a.example/rss", category: "/Existing,/Missing"), Feed("https://b.example/rss", category: "/missing")),
            canCreateTags: false);

        Assert.Equal(2, result.Created);
        Assert.Equal([new NewsOpmlSkippedTag { Name = "Missing", Reason = NewsOpmlTagSkipReason.NoPermission },
                      new NewsOpmlSkippedTag { Name = "missing", Reason = NewsOpmlTagSkipReason.NoPermission }], result.TagsSkipped);
        Assert.Equal(["Existing"], result.Items[0].Tags);
        Assert.Equal(1, await Context().NewsTags.CountAsync());
        Assert.Equal(1, await Context().NewsFeedTags.CountAsync(l => l.NewsTagId == existing));
    }

    [Fact]
    public async Task AC19_The_tag_cap_name_length_archived_tags_and_refused_feeds()
    {
        var context = Context();
        for (var i = 0; i < 498; i++)
        {
            context.NewsTags.Add(new NewsTag { NewsTagId = Guid.NewGuid(), Name = $"t{i}", NameKey = $"t{i}", CreatedAtUtc = Now, UpdatedAtUtc = Now });
        }

        await context.SaveChangesAsync();
        await SeedTagAsync("Old", archived: true);
        await SeedFeedAsync("https://existing.example/rss");
        var longName = new string('n', 65);

        var result = await ImportAsync(Opml(
            Feed("https://a.example/rss", category: $"/First,/Second,/{longName},/Old"),
            Feed("ftp://refused.example/", category: "/FromInvalid"),
            Feed("https://existing.example/rss", category: "/FromExisting")));

        // 499 tags (one archived) and two new names: First fits under the 500 cap, Second does not.
        Assert.Equal(["First"], result.TagsCreated);
        Assert.Contains(result.TagsSkipped, s => s is { Name: "Second", Reason: NewsOpmlTagSkipReason.TagLimit });
        Assert.Contains(result.TagsSkipped, s => s.Reason == NewsOpmlTagSkipReason.NameTooLong && s.Name.StartsWith("nnnn", StringComparison.Ordinal));
        Assert.Contains(result.TagsSkipped, s => s is { Name: "Old", Reason: NewsOpmlTagSkipReason.Archived });
        Assert.Equal(3, result.TagsSkippedTotal);
        Assert.Equal(["First"], result.Items.Single(i => i.Outcome == NewsOpmlImportOutcome.Created).Tags);
        Assert.DoesNotContain(result.TagsSkipped, s => s.Name.StartsWith("From", StringComparison.Ordinal));
        Assert.False(await Context().NewsTags.AnyAsync(t => t.Name.StartsWith("From")));
        Assert.Equal(NewsLimits.MaxTags, await Context().NewsTags.CountAsync());
    }

    [Fact]
    public async Task AC20_A_feed_naming_forty_distinct_tags_links_thirty_two_and_reports_eight_dropped()
    {
        var category = string.Join(',', Enumerable.Range(0, 40).Select(i => $"/tag{i}"));

        var item = Assert.Single((await ImportAsync(Opml(Feed("https://a.example/rss", category: category)))).Items);

        Assert.Equal(32, item.Tags.Count);
        Assert.Equal(8, item.TagsDropped);
        Assert.Equal(32, await Context().NewsFeedTags.CountAsync());
    }

    [Fact]
    public async Task AC21_Skipped_tags_are_capped_at_a_hundred_rows_with_an_exact_total()
    {
        var outlines = Enumerable.Range(0, 30).Select(f =>
            Feed($"https://f{f}.example/rss", category: string.Join(',', Enumerable.Range(0, 5).Select(t => $"/m{f}-{t}"))));

        var result = await ImportAsync(Opml([.. outlines]), canCreateTags: false);

        Assert.Equal(100, result.TagsSkipped.Count);
        Assert.Equal(150, result.TagsSkippedTotal);
    }

    [Fact]
    public async Task AC23_Server_owned_attributes_in_the_file_set_nothing()
    {
        var supplied = Guid.NewGuid();
        var result = await ImportAsync(Opml(Feed("https://a.example/rss",
            extra: $"id=\"{supplied}\" created=\"Mon, 01 Jan 2001 00:00:00 GMT\" odyssey:health=\"Healthy\" xmlns:odyssey=\"urn:odyssey:opml:1\"")));

        var feed = await Context().NewsFeeds.SingleAsync();
        Assert.NotEqual(supplied, feed.NewsFeedId);
        Assert.Equal(Now, feed.CreatedAtUtc);
        var state = await Context().NewsFeedFetchStates.SingleAsync();
        Assert.Equal(NewsFeedHealth.Pending, NewsFeedProjection.Health(feed, state));
        Assert.Equal(result.Items[0].FeedId, feed.NewsFeedId);
    }

    [Fact]
    public async Task AC27_More_distinct_feeds_than_the_entry_cap_is_422_and_writes_nothing()
    {
        settings.Settings = settings.Settings with { OpmlMaxImportEntries = 100 };

        var error = await Assert.ThrowsAsync<NewsException>(() =>
            ImportAsync(Opml([.. Enumerable.Range(0, 101).Select(i => Feed($"https://f{i}.example/rss", category: "/T"))])));

        Assert.Equal((422, NewsErrorCodes.OpmlTooManyEntries), (error.StatusCode, error.Code));
        Assert.Equal(100, error.Extensions!["limit"]);
        Assert.Equal(0, await Context().NewsFeeds.CountAsync());
        Assert.Equal(0, await Context().NewsTags.CountAsync());
    }

    [Fact]
    public async Task AC25_An_unreadable_file_is_400_news_opml_unreadable_with_a_generic_message()
    {
        var error = await Assert.ThrowsAsync<NewsException>(() => ImportAsync("<rss><channel>&nope;</channel></rss>"));

        Assert.Equal((400, NewsErrorCodes.OpmlUnreadable, "This file is not an OPML document."), (error.StatusCode, error.Code, error.Message));
    }

    [Fact]
    public async Task AC31_An_interrupted_news_transaction_is_news_busy()
    {
        Assert.True(NewsQueryGuard.IsBusy(new NewsTransactionRetryException()));
        var busy = await Assert.ThrowsAsync<NewsException>(() => NewsQueryGuard.RunAsync<int>(() => throw new NewsTransactionRetryException()));
        Assert.Equal((503, NewsErrorCodes.Busy, (int?)NewsErrors.BusyRetryAfterSeconds), (busy.StatusCode, busy.Code, busy.RetryAfterSeconds));
    }

    [Fact]
    public async Task AC36_Import_and_export_log_lines_carry_no_url_title_tag_name_or_file_name()
    {
        await SeedFeedAsync("https://secret-export.example/rss?token=abc", customTitle: "Secret Export Title", tagIds: [await SeedTagAsync("SecretExportTag")]);
        await ExportAsync();
        await ImportAsync(Opml(
            Feed("https://secret-import.example/rss", text: "Secret Import Title", category: "/SecretImportTag"),
            Feed("ftp://secret-invalid.example/")));

        Assert.Equal(2, logs.Lines.Count);
        foreach (var line in logs.Lines)
        {
            foreach (var secret in new[] { "secret", "Secret", "token", ".opml" })
            {
                Assert.DoesNotContain(secret, line, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void The_category_escape_round_trips_every_reserved_character()
    {
        foreach (var name in new[] { "100%", "a,b", "a/b", "a%2Fb", "%%2C%", "plain" })
        {
            var escaped = OpmlDocumentWriter.EscapeSegment(name);
            Assert.DoesNotContain(',', escaped);
            Assert.DoesNotContain('/', escaped);
            Assert.Equal(name, OpmlDocumentReader.DecodeSegment(escaped));
        }
    }

    /// <summary>
    /// AC 28 at its stated volume, nightly: the 100,000-feed file is a perf-tier test (<see cref="PerfTier"/>). The
    /// per-PR run keeps the same import, its truncation and the memory bound on 5,000 feeds below.
    /// </summary>
    [SkippableFact]
    [Trait(PerfTier.TraitName, PerfTier.TraitValue)]
    public async Task AC28_A_60_MB_file_with_100000_distinct_feeds_stays_under_the_memory_target()
    {
        PerfTier.SkipUnlessRequested();
        await AssertDistinctFeedsImportUnderTheMemoryTargetAsync(feeds: 100_000, minimumBytes: 60_000_000, minimumSamples: 57);
    }

    [Fact]
    public async Task AC28_A_3_MB_file_with_5000_distinct_feeds_stays_under_the_memory_target()
    {
        await AssertDistinctFeedsImportUnderTheMemoryTargetAsync(feeds: 5_000, minimumBytes: 3_000_000, minimumSamples: 2);
    }

    private async Task AssertDistinctFeedsImportUnderTheMemoryTargetAsync(int feeds, long minimumBytes, int minimumSamples)
    {
        // The file is generated as it is read, so the only large thing alive is what the import keeps.
        var padding = new string('p', 520);
        var lines = Enumerable.Range(0, feeds)
            .Select(i => $"<outline text=\"Feed {i} {padding}\" xmlUrl=\"https://f{i}.example/rss\" category=\"/A,/B\"/>\n");
        var probe = new HeapProbe();
        await using var stream = new LineStream("<opml version=\"2.0\"><body>\n", lines, "</body></opml>", probe);

        var (result, peak) = await MeasurePeakAsync(probe, () => Import().ImportAsync(stream, true, "user-1", CancellationToken.None));

        Assert.True(stream.BytesRead > minimumBytes, $"only {stream.BytesRead} bytes were generated");
        Assert.Equal((feeds, NewsSettings.Defaults.MaxFeeds), (result.Feeds, result.Created));
        Assert.True(result.ItemsTruncated);
        Assert.True(probe.Samples >= minimumSamples, $"only {probe.Samples} heap samples were taken");
        Assert.True(peak < 50L * 1024 * 1024, $"peak {peak / (1024 * 1024)} MB above the baseline");
    }

    [Fact]
    public async Task AC28_An_adversarial_60_MB_attribute_stays_under_the_memory_target()
    {
        var chunk = new string('a', 59_999) + ">";
        var lines = Enumerable.Range(0, 1_000).Select(_ => chunk);
        // The import refuses this file after about 76 KB, long before a MiB clock would tick, so this stream samples
        // every 16 KiB and the test asserts the probe actually saw the work.
        var probe = new HeapProbe();
        await using var stream = new LineStream(
            "<opml><body><outline xmlUrl=\"https://x.example/\" text=\"", lines, "\"/></body></opml>", probe, 16 * 1024);

        var (error, peak) = await MeasurePeakAsync(probe, () => Assert.ThrowsAsync<NewsException>(() =>
            Import().ImportAsync(stream, true, "user-1", CancellationToken.None)));

        Assert.Equal(NewsErrorCodes.OpmlUnreadable, error.Code);
        Assert.True(probe.Samples >= 4, $"only {probe.Samples} heap samples were taken");
        Assert.True(peak < 50L * 1024 * 1024, $"peak {peak / (1024 * 1024)} MB above the baseline");
    }

    /// <summary>
    /// The test-time allocation probe: the live heap sampled under forced collections while the work runs. The class
    /// sits in a non-parallel collection so no other test's allocations reach the process-wide heap figure.
    ///
    /// <para>
    /// Samples are clocked two ways. <see cref="LineStream"/> takes one per MiB the import reads (or the interval a
    /// test passes), so how often the probe samples follows the import's progress, whatever a collection costs, and a
    /// background sampler covers work that reads nothing (the save at the end). The background sampler waits at least four times as long as its last
    /// collection took. A full collection costs in proportion to the whole live heap, which in the full suite holds
    /// everything the earlier test classes left behind, and it suspends the work thread. Sampling
    /// <see cref="GC.GetTotalMemory(bool)"/> with <c>true</c> every 5 ms, which repeats collections until the figure
    /// settles, made the 100,000-feed import take 18 s alone and over 14 minutes in the suite, past CI's hang timeout.
    /// </para>
    /// </summary>
    private static async Task<(T Result, long Peak)> MeasurePeakAsync<T>(HeapProbe probe, Func<Task<T>> work)
    {
        using var stop = new CancellationTokenSource();
        var sampler = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                var cost = probe.Sample();
                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(5, cost.TotalMilliseconds * 4)), stop.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        });

        var result = await Task.Run(work);
        await stop.CancelAsync();
        await sampler;
        probe.Sample();
        GC.KeepAlive(result);
        return (result, probe.Peak);
    }

    /// <summary>The live heap above a baseline taken at construction, each sample one blocking full collection.</summary>
    private sealed class HeapProbe
    {
        private readonly Lock gate = new();
        private readonly long baseline = GC.GetTotalMemory(forceFullCollection: true);
        private long peak;
        private int samples;

        /// <summary>How many samples were taken, so a test can fail on a probe that never saw its work.</summary>
        public int Samples
        {
            get
            {
                lock (gate)
                {
                    return samples;
                }
            }
        }

        public long Peak
        {
            get
            {
                lock (gate)
                {
                    return peak;
                }
            }
        }

        public TimeSpan Sample()
        {
            var started = Stopwatch.GetTimestamp();
            GC.Collect();
            var live = GC.GetTotalMemory(forceFullCollection: false) - baseline;
            lock (gate)
            {
                peak = Math.Max(peak, live);
                samples++;
            }

            return Stopwatch.GetElapsedTime(started);
        }
    }

    /// <summary>A stream generated line by line, so a large test file is never held in memory.</summary>
    private sealed class LineStream(
        string head, IEnumerable<string> lines, string tail, HeapProbe? probe = null, long sampleEveryBytes = 1024 * 1024)
        : Stream
    {
        private readonly IEnumerator<string> source = new[] { head }.Concat(lines).Append(tail).GetEnumerator();
        private byte[] current = [];
        private int offset;

        public long BytesRead { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => BytesRead; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int index, int count)
        {
            while (offset == current.Length)
            {
                if (!source.MoveNext())
                {
                    return 0;
                }

                current = Encoding.UTF8.GetBytes(source.Current);
                offset = 0;
            }

            var n = Math.Min(count, current.Length - offset);
            Array.Copy(current, offset, buffer, index, n);
            offset += n;
            var before = BytesRead;
            BytesRead += n;
            if (probe is not null && before / sampleEveryBytes != BytesRead / sampleEveryBytes)
            {
                probe.Sample();
            }

            return n;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                source.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private sealed class FakeNewsSettings : INewsSettingsLookup
    {
        public NewsSettings Settings { get; set; } = NewsSettings.Defaults;

        public IReadOnlyList<NewsPrivateHostGrant> Grants { get; set; } = [];

        public Task<NewsSettings> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult(Settings);

        public Task<IReadOnlyList<NewsPrivateHostGrant>> GetAllowListAsync(CancellationToken cancellationToken = default) => Task.FromResult(Grants);
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add(formatter(state, exception));
    }
}
