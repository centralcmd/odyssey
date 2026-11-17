using System.Data.Common;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Odyssey.Context;
using Odyssey.Core.Internet.News;
using Odyssey.Core.Internet.News.Opml;
using Odyssey.Core.Internet.News.Reading;
using Odyssey.Dtos;
using Odyssey.Dtos.Internet;
using Xunit;

namespace Odyssey.IntegrationTests;

/// <summary>
/// The MariaDB-only half of the OPML import (issue #508): the column collation deciding tag equality (AC 17), a
/// concurrent tag create between the lookup and the insert (AC 18), the batched lookup bound (AC 20), a concurrent
/// feed create racing the in-transaction re-count (AC 12) and the <c>UrlKeyHash</c> index (AC 30), and the migration
/// (AC 38). EF InMemory has no collation, no unique index and no transaction, so none of it is observable there.
/// </summary>
[Collection(MariaDbCollection.Name)]
public sealed class NewsOpmlRelationalTests(MariaDbFixture fixture)
{
    private static readonly DateTime Base = new(2026, 11, 17, 12, 0, 0, DateTimeKind.Utc);

    private string connection = string.Empty;

    private async Task<OdysseyContext> ContextAsync(params IInterceptor[] interceptors)
    {
        if (connection.Length == 0)
        {
            connection = await fixture.CreateMigratedDatabaseAsync($"odyssey_opml_{Guid.NewGuid():N}"[..40]);
        }

        return new OdysseyContext(new DbContextOptionsBuilder<OdysseyContext>()
            .UseMySql(connection, ServerVersion.AutoDetect(connection), mySql => mySql.EnableRetryOnFailure())
            .AddInterceptors([new NewsQueryCommandInterceptor(), .. interceptors])
            .Options);
    }

    private static NewsOpmlImportService Import(OdysseyContext context, NewsSettings? settings = null) =>
        new(context, new FixedNewsSettings(settings ?? NewsSettings.Defaults), new NewsTagService(context, TimeProvider.System, NullLogger<NewsTagService>.Instance),
            TimeProvider.System, NullLogger<NewsOpmlImportService>.Instance);

    private static async Task<NewsOpmlImportResult> ImportAsync(OdysseyContext context, string xml, NewsSettings? settings = null)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        return await Import(context, settings).ImportAsync(stream, canCreateTags: true, "user-1", CancellationToken.None);
    }

    private static string Opml(params string[] outlines) => $"<opml version=\"2.0\"><body>{string.Concat(outlines)}</body></opml>";

    private static string Feed(string url, string? category = null) =>
        $"<outline xmlUrl=\"{url}\"{(category is null ? "" : $" category=\"{category}\"")}/>";

    private static async Task<Guid> SeedFeedAsync(OdysseyContext context, string url)
    {
        var id = Guid.NewGuid();
        context.NewsFeeds.Add(new NewsFeed
        {
            NewsFeedId = id, Url = url, UrlKeyHash = NewsUrlKey.Hash(NewsUrlKey.UrlKey(url)!), IsActive = true, DownloadImages = true,
            CreatedAtUtc = Base, SettingsVersion = Guid.NewGuid(),
        });
        context.NewsFeedFetchStates.Add(new NewsFeedFetchState { NewsFeedId = id, FetchFence = Guid.NewGuid(), NextFetchAtUtc = Base });
        context.NewsFeedCounters.Add(new NewsFeedCounter { NewsFeedId = id });
        await context.SaveChangesAsync();
        return id;
    }

    [SkippableFact]
    public async Task AC17_Cafe_and_Cafe_with_an_accent_in_one_file_become_one_tag_spelled_as_first_seen()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await using var context = await ContextAsync();

        var result = await ImportAsync(context, Opml(Feed("https://a.example/rss", "/Cafe"), Feed("https://b.example/rss", "/Café")));

        Assert.Equal(2, result.Created);
        Assert.Equal(["Cafe"], result.TagsCreated);
        Assert.Empty(result.TagsSkipped);
        await using var check = await ContextAsync();
        var tag = await check.NewsTags.AsNoTracking().SingleAsync();
        Assert.Equal("Cafe", tag.Name);
        Assert.Equal(2, await check.NewsFeedTags.CountAsync(l => l.NewsTagId == tag.NewsTagId));
    }

    [SkippableFact]
    public async Task AC17_A_file_naming_Cafe_with_an_accent_links_the_existing_Cafe()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await using var context = await ContextAsync();
        var existing = await new NewsTagService(context, TimeProvider.System, NullLogger<NewsTagService>.Instance)
            .CreateAsync(new NewNewsTag { Name = "Cafe" }, CancellationToken.None);

        var result = await ImportAsync(context, Opml(Feed("https://a.example/rss", "/CAFÉ")));

        Assert.Empty(result.TagsCreated);
        Assert.Equal(["CAFÉ"], result.Items[0].Tags);
        await using var check = await ContextAsync();
        Assert.Equal(1, await check.NewsTags.CountAsync());
        Assert.Equal(existing.Id, (await check.NewsFeedTags.AsNoTracking().SingleAsync()).NewsTagId);
    }

    [SkippableFact]
    public async Task AC18_A_tag_committed_between_the_lookup_and_the_insert_is_mapped_onto()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var race = new RaceBeforeSave(typeof(NewsTag), async () =>
        {
            await using var other = await ContextAsync();
            await new NewsTagService(other, TimeProvider.System, NullLogger<NewsTagService>.Instance)
                .CreateAsync(new NewNewsTag { Name = "Racing" }, CancellationToken.None);
        });
        await using var context = await ContextAsync(race);

        var result = await ImportAsync(context, Opml(Feed("https://a.example/rss", "/Racing,/Calm")));

        Assert.True(race.Fired);
        Assert.Equal(["Calm"], result.TagsCreated);
        Assert.Empty(result.TagsSkipped);
        await using var check = await ContextAsync();
        Assert.Equal(2, await check.NewsTags.CountAsync());
        var racing = await check.NewsTags.AsNoTracking().SingleAsync(t => t.Name == "Racing");
        Assert.True(await check.NewsFeedTags.AnyAsync(l => l.NewsTagId == racing.NewsTagId));
    }

    [SkippableFact]
    public async Task AC20_A_thousand_feeds_with_thirty_two_names_each_resolve_in_at_most_sixty_four_lookups()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var counter = new TagLookupCounter();
        await using var context = await ContextAsync(counter);
        var outlines = Enumerable.Range(0, 1000).Select(f =>
            Feed($"https://f{f}.example/rss", string.Join(',', Enumerable.Range(0, 32).Select(t => $"/n{f}x{t}"))));

        var result = await ImportAsync(context, Opml([.. outlines]), NewsSettings.Defaults with { MaxFeeds = 1000 });

        Assert.Equal(1000, result.Created);
        Assert.Equal(NewsLimits.MaxTags, result.TagsCreated.Count);
        Assert.Equal(32_000 - NewsLimits.MaxTags, result.TagsSkippedTotal);
        Assert.InRange(counter.Lookups, 1, 64);
    }

    [SkippableFact]
    public async Task AC12_A_create_racing_the_import_reduces_its_created_count_and_the_limit_holds()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var race = new RaceBeforeFeedCount(async () =>
        {
            await using var other = await ContextAsync();
            await SeedFeedAsync(other, "https://racer.example/rss");
        });
        await using var context = await ContextAsync(race);

        var result = await ImportAsync(context, Opml([.. Enumerable.Range(0, 5).Select(i => Feed($"https://f{i}.example/rss"))]),
            NewsSettings.Defaults with { MaxFeeds = 5 });

        Assert.True(race.Fired);
        Assert.Equal((4, 1), (result.Created, result.FeedLimit));
        Assert.Equal(NewsOpmlImportOutcome.FeedLimit, result.Items.Single(i => i.Position == 5).Outcome);
        await using var check = await ContextAsync();
        Assert.Equal(5, await check.NewsFeeds.CountAsync());
    }

    [SkippableFact]
    public async Task AC30_A_UrlKeyHash_duplicate_at_the_feed_save_is_409_with_no_feed_committed_and_a_retry_succeeds()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        Guid racer = default;
        var race = new RaceBeforeSave(typeof(NewsFeed), async () =>
        {
            await using var other = await ContextAsync();
            racer = await SeedFeedAsync(other, "https://b.example/rss");
        });
        await using var context = await ContextAsync(race);
        var file = Opml(Feed("https://a.example/rss", "/T"), Feed("https://b.example/rss"));

        var error = await Assert.ThrowsAsync<NewsException>(() => ImportAsync(context, file));

        Assert.Equal((409, NewsErrorCodes.OpmlConflict), (error.StatusCode, error.Code));
        await using (var check = await ContextAsync())
        {
            Assert.Equal([racer], await check.NewsFeeds.Select(f => f.NewsFeedId).ToListAsync());
            Assert.Equal(0, await check.NewsFeedFetchStates.CountAsync(s => s.NewsFeedId != racer));
        }

        await using var retryContext = await ContextAsync();
        var retry = await ImportAsync(retryContext, file);
        Assert.Equal((1, 1), (retry.Created, retry.AlreadySubscribed));
        Assert.Equal(racer, retry.Items.Single(i => i.Outcome == NewsOpmlImportOutcome.AlreadySubscribed).FeedId);
        Assert.Empty(retry.TagsCreated); // the first attempt's tag stayed, and the retry mapped onto it (§8.3.6)
    }

    [SkippableFact]
    public async Task AC38_The_migration_applies_and_reverts_and_inserts_both_rows_at_their_defaults()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var database = $"odyssey_opml_mig_{Guid.NewGuid():N}"[..40];
        var migrations = await fixture.CreateMigratedDatabaseAsync(database);
        await using var context = new OdysseyContext(new DbContextOptionsBuilder<OdysseyContext>()
            .UseMySql(migrations, ServerVersion.AutoDetect(migrations)).Options);

        async Task<string?> ValueAsync(string key) =>
            await context.SystemSettings.AsNoTracking().Where(s => s.Key == key).Select(s => s.Value).FirstOrDefaultAsync();

        Assert.Equal("64", await ValueAsync(SystemSettingsKeys.NewsOpmlMaxImportMegabytes));
        Assert.Equal("100000", await ValueAsync(SystemSettingsKeys.NewsOpmlMaxImportEntries));

        await MigrationSeam.MigrateToAsync(context, "_AddNewsModule");
        Assert.False(await MigrationSeam.HasRunAsync(context, "_AddNewsOpmlImportLimits"));
        Assert.Null(await ValueAsync(SystemSettingsKeys.NewsOpmlMaxImportMegabytes));
        Assert.Null(await ValueAsync(SystemSettingsKeys.NewsOpmlMaxImportEntries));

        await context.Database.MigrateAsync();
        Assert.True(await MigrationSeam.HasRunAsync(context, "_AddNewsOpmlImportLimits"));
        Assert.Equal(SystemSettingsDefaults.NewsOpmlMaxImportMegabytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
            await ValueAsync(SystemSettingsKeys.NewsOpmlMaxImportMegabytes));
        Assert.Equal(SystemSettingsDefaults.NewsOpmlMaxImportEntries.ToString(System.Globalization.CultureInfo.InvariantCulture),
            await ValueAsync(SystemSettingsKeys.NewsOpmlMaxImportEntries));
    }

    /// <summary>Runs <paramref name="race"/> once, on its own connection, just before the first save that adds an entity of the type.</summary>
    private sealed class RaceBeforeSave(Type entityType, Func<Task> race) : SaveChangesInterceptor
    {
        public bool Fired { get; private set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!Fired && eventData.Context!.ChangeTracker.Entries().Any(e => e.State == EntityState.Added && e.Entity.GetType() == entityType))
            {
                Fired = true;
                await race();
            }

            return result;
        }
    }

    /// <summary>Runs <paramref name="race"/> once, just before the feed count inside the import's transaction.</summary>
    private sealed class RaceBeforeFeedCount(Func<Task> race) : DbCommandInterceptor
    {
        public bool Fired { get; private set; }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (!Fired && command.Transaction is not null && command.CommandText.Contains("COUNT(*)", StringComparison.Ordinal)
                && command.CommandText.Contains("`NewsFeeds`", StringComparison.Ordinal))
            {
                Fired = true;
                await race();
            }

            return result;
        }

        public override async ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
        {
            if (!Fired && command.Transaction is not null && command.CommandText.Contains("COUNT(*)", StringComparison.Ordinal)
                && command.CommandText.Contains("`NewsFeeds`", StringComparison.Ordinal))
            {
                Fired = true;
                await race();
            }

            return result;
        }
    }

    /// <summary>Counts the batched tag lookups: the SELECTs over <c>NewsTags</c> by <c>NameKey</c>.</summary>
    private sealed class TagLookupCounter : DbCommandInterceptor
    {
        public int Lookups { get; private set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            var text = command.CommandText;
            if (text.Contains("FROM `NewsTags`", StringComparison.Ordinal) && text.Contains("`NameKey`", StringComparison.Ordinal)
                && !text.Contains("COUNT(", StringComparison.Ordinal) && !text.TrimStart().StartsWith("INSERT", StringComparison.Ordinal))
            {
                Lookups++;
            }

            return ValueTask.FromResult(result);
        }
    }

    private sealed class FixedNewsSettings(NewsSettings settings) : INewsSettingsLookup
    {
        public Task<NewsSettings> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult(settings);

        public Task<IReadOnlyList<NewsPrivateHostGrant>> GetAllowListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<NewsPrivateHostGrant>>([]);
    }
}
