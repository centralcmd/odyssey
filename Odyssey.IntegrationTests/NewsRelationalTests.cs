// EF1002 flags interpolation into ExecuteSqlRawAsync. Every value interpolated below is a constant or a Guid
// this test generated itself — there is no external input.
#pragma warning disable EF1002

using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using Odyssey.Context;
using Odyssey.Dtos.Application;
using Odyssey.Dtos.Internet;
using Xunit;

namespace Odyssey.IntegrationTests;

/// <summary>
/// The relational half of issue #470's schema (B1): <c>AddNewsModule</c> both ways, the seeded totals row, every
/// news CHECK, the FULLTEXT index, the foreign keys' on-delete behaviour (the RESTRICT key to <c>StoredObjects</c>
/// included), the unique keys and the two concurrency tokens. None of it is observable on EF InMemory, which enforces
/// no CHECK, index or key.
/// </summary>
[Collection(MariaDbCollection.Name)]
public class NewsRelationalTests(MariaDbFixture fixture)
{
    private const string Database = "odyssey_news";
    private const string PreviousMigration = "_AddExchangeRateFetchedLookupIndex";
    private const string Migration = "_AddNewsModule";

    // MariaDB error numbers: a failed CHECK, a RESTRICT key refusing a parent delete, a missing parent, a duplicate.
    private const int CheckViolation = 4025;
    private const int RowIsReferenced = 1451;
    private const int NoParentRow = 1452;
    private const int DuplicateKey = 1062;

    private static readonly DateTime Now = new(2026, 11, 16, 8, 0, 0, DateTimeKind.Utc);

    /// <summary>AC 36, 78 and §11.2: a fresh database holds the one totals row; Down removes job 9's rows first and reverts cleanly.</summary>
    [SkippableFact]
    public async Task The_migration_seeds_the_totals_row_and_reverts_and_reapplies_cleanly()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigratedAsync();
        await using var context = NewContext();

        var totals = await context.NewsStorageTotals.AsNoTracking().SingleAsync();
        Assert.Equal((NewsStorageTotal.SingletonId, 0L, 0L), (totals.Id, totals.ImageBytes, totals.TextAndSourceBytes));
        // Nine from AddNewsModule, two from AddNewsOpmlImportLimits (issue #508); migrating below the module reverts both.
        Assert.Equal(11L, await MigrationSeam.CountAsync(context, "SELECT COUNT(*) FROM `SystemSettings` WHERE `Key` LIKE 'News%'"));

        await AddFeedAsync(context);
        await AddRunAsync(context, run => run.FeedsMarked = 3);
        context.JobHeartbeats.Add(new JobHeartbeat { JobType = JobType.NewsFeedFetch, WorkerId = Guid.NewGuid(), LastTickAtUtc = Now });
        await context.SaveChangesAsync();

        // Down deletes the job's run-log rows before the narrowed CHECKs would refuse them, then drops the tables.
        await MigrationSeam.MigrateToAsync(context, PreviousMigration);

        foreach (var table in new[]
        {
            "NewsFeeds", "NewsFeedFetchStates", "NewsFeedCounters", "NewsStorageTotals", "NewsFeedBuckets", "NewsHostThrottle",
            "NewsArticles", "NewsArticleIdentities", "NewsArticleRevisions", "NewsArticleFiles", "NewsArticleImages",
            "NewsTags", "NewsFeedTags", "NewsArticleTags", "NewsFeedCrashEvents",
        })
        {
            Assert.False(await MigrationSeam.TableExistsAsync(context, table), table);
        }

        Assert.Equal(0L, await MigrationSeam.CountAsync(context, "SELECT COUNT(*) FROM `JobRuns` WHERE `JobType` = 9"));
        Assert.Equal(0L, await MigrationSeam.CountAsync(context, "SELECT COUNT(*) FROM `JobHeartbeats` WHERE `JobType` = 9"));
        Assert.Equal(0L, await MigrationSeam.CountAsync(context, "SELECT COUNT(*) FROM `SystemSettings` WHERE `Key` LIKE 'News%'"));
        await AssertRefusedAsync(CheckViolation, () => context.Database.ExecuteSqlRawAsync(
            "INSERT INTO `JobHeartbeats` (`JobType`, `WorkerId`, `LastTickAtUtc`) VALUES (9, UUID(), NOW())"));

        await context.Database.MigrateAsync();
        Assert.True(await MigrationSeam.HasRunAsync(context, Migration));
        Assert.Equal(1L, await MigrationSeam.CountAsync(context, "SELECT COUNT(*) FROM `NewsStorageTotals` WHERE `Id` = 1"));
    }

    /// <summary>§4.14: the job CHECKs admit 9 and the run subtype's counters, which may not go negative.</summary>
    [SkippableFact]
    public async Task A_news_run_is_admitted_and_its_counters_are_non_negative()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigratedAsync();
        await using var context = NewContext();

        var runId = await AddRunAsync(context, run =>
        {
            run.FeedsFetched = 2;
            run.ItemsStored = 10;
        });
        Assert.Equal(1L, await MigrationSeam.CountAsync(context,
            $"SELECT COUNT(*) FROM `JobRuns` WHERE `JobRunId` = '{runId}' AND `JobType` = 9 AND `ExclusiveGuard` IS NULL"));

        await AssertRefusedAsync(CheckViolation, () => context.Database.ExecuteSqlRawAsync(
            $"UPDATE `JobRuns` SET `FeedsMarked` = -1 WHERE `JobRunId` = '{runId}'"));
    }

    [SkippableTheory]
    [InlineData("UPDATE `NewsFeedFetchStates` SET `QuotaReachedAtUtc` = NOW(), `QuotaReason` = 3")]
    [InlineData("UPDATE `NewsFeedFetchStates` SET `QuotaReachedAtUtc` = NOW(), `QuotaReason` = NULL")]
    [InlineData("UPDATE `NewsFeedFetchStates` SET `QuotaReachedAtUtc` = NULL, `QuotaReason` = 1")]
    [InlineData("UPDATE `NewsArticles` SET `IsRead` = 1, `ReadAtUtc` = NULL")]
    [InlineData("UPDATE `NewsArticles` SET `IsRead` = 0, `ReadAtUtc` = NOW()")]
    [InlineData("UPDATE `NewsArticles` SET `BodyOmittedReason` = 3")]
    [InlineData("UPDATE `NewsArticleRevisions` SET `BodyOmittedReason` = 3")]
    [InlineData("UPDATE `NewsArticleFiles` SET `Kind` = 3 WHERE `NewsArticleRevisionId` IS NOT NULL")]
    [InlineData("INSERT INTO `NewsStorageTotals` (`Id`, `ImageBytes`, `TextAndSourceBytes`) VALUES (2, 0, 0)")]
    public async Task Every_news_check_refuses_its_violation(string sql)
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigratedAsync();
        await using var context = NewContext();
        var graph = await AddGraphAsync(context);
        await AddRevisionWithSourceAsync(context, graph);

        await AssertRefusedAsync(CheckViolation, () => context.Database.ExecuteSqlRawAsync(sql));
    }

    /// <summary>AC 59, 75, 76: the legal shapes the CHECKs must not refuse.</summary>
    [SkippableFact]
    public async Task The_legal_shapes_pass_every_check()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigratedAsync();
        await using var context = NewContext();
        var graph = await AddGraphAsync(context);
        await AddRevisionWithSourceAsync(context, graph);

        await context.Database.ExecuteSqlRawAsync("UPDATE `NewsFeedFetchStates` SET `QuotaReachedAtUtc` = NOW(), `QuotaReason` = 2");
        await context.Database.ExecuteSqlRawAsync("UPDATE `NewsArticles` SET `IsRead` = 1, `ReadAtUtc` = NOW(), `BodyOmittedReason` = 2");
        await context.Database.ExecuteSqlRawAsync("UPDATE `NewsArticleRevisions` SET `BodyOmittedReason` = 1");
        await context.Database.ExecuteSqlRawAsync("UPDATE `NewsArticleFiles` SET `Kind` = 2 WHERE `NewsArticleRevisionId` IS NOT NULL");
        await context.Database.ExecuteSqlRawAsync("UPDATE `NewsStorageTotals` SET `TextAndSourceBytes` = `TextAndSourceBytes` + 10");
        Assert.Equal(10L, await MigrationSeam.CountAsync(context, "SELECT `TextAndSourceBytes` FROM `NewsStorageTotals` WHERE `Id` = 1"));
    }

    /// <summary>§4.10: RESTRICT to StoredObjects — an object leaves only once no article file names it.</summary>
    [SkippableFact]
    public async Task A_stored_object_cannot_be_deleted_from_under_an_article_file()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigratedAsync();
        await using var context = NewContext();
        var graph = await AddGraphAsync(context);

        await AssertRefusedAsync(RowIsReferenced, () => context.Database.ExecuteSqlRawAsync(
            $"DELETE FROM `StoredObjects` WHERE `Id` = '{graph.SourceObjectId}'"));

        await context.Database.ExecuteSqlRawAsync($"DELETE FROM `NewsArticleFiles` WHERE `StoredObjectId` = '{graph.SourceObjectId}'");
        await context.Database.ExecuteSqlRawAsync($"DELETE FROM `StoredObjects` WHERE `Id` = '{graph.SourceObjectId}'");
    }

    /// <summary>
    /// AC 10's row half: a feed delete cascades through every news row of the feed and leaves the stored objects (the
    /// store's unreferenced sweep collects them) and the tags (shared, RESTRICT from their links only).
    /// </summary>
    [SkippableFact]
    public async Task A_feed_delete_cascades_every_row_of_the_feed_and_keeps_objects_and_tags()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigratedAsync();
        await using var context = NewContext();
        var graph = await AddGraphAsync(context);
        await AddRevisionWithSourceAsync(context, graph);
        var other = await AddGraphAsync(context);

        await context.Database.ExecuteSqlRawAsync($"DELETE FROM `NewsFeeds` WHERE `NewsFeedId` = '{graph.FeedId}'");

        foreach (var table in new[]
        {
            "NewsFeedFetchStates", "NewsFeedCounters", "NewsFeedBuckets", "NewsArticles", "NewsArticleIdentities", "NewsFeedTags",
        })
        {
            Assert.Equal(0L, await MigrationSeam.CountAsync(context, $"SELECT COUNT(*) FROM `{table}` WHERE `NewsFeedId` = '{graph.FeedId}'"));
        }

        foreach (var table in new[] { "NewsArticleRevisions", "NewsArticleFiles", "NewsArticleImages", "NewsArticleTags" })
        {
            Assert.Equal(0L, await MigrationSeam.CountAsync(context, $"SELECT COUNT(*) FROM `{table}` WHERE `NewsArticleId` = '{graph.ArticleId}'"));
        }

        // The other feed is untouched.
        foreach (var table in new[] { "NewsArticleFiles", "NewsArticleImages", "NewsArticleTags" })
        {
            Assert.NotEqual(0L, await MigrationSeam.CountAsync(context, $"SELECT COUNT(*) FROM `{table}` WHERE `NewsArticleId` = '{other.ArticleId}'"));
        }

        Assert.Equal(1L, await MigrationSeam.CountAsync(context, $"SELECT COUNT(*) FROM `StoredObjects` WHERE `Id` = '{graph.SourceObjectId}'"));
        Assert.Equal(1L, await MigrationSeam.CountAsync(context, $"SELECT COUNT(*) FROM `NewsTags` WHERE `NewsTagId` = '{graph.TagId}'"));
    }

    [SkippableFact]
    public async Task A_tag_in_use_cannot_be_deleted_and_an_unused_one_can()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigratedAsync();
        await using var context = NewContext();
        var graph = await AddGraphAsync(context);

        await AssertRefusedAsync(RowIsReferenced, () => context.Database.ExecuteSqlRawAsync($"DELETE FROM `NewsTags` WHERE `NewsTagId` = '{graph.TagId}'"));

        await context.Database.ExecuteSqlRawAsync($"DELETE FROM `NewsFeedTags` WHERE `NewsTagId` = '{graph.TagId}'");
        await AssertRefusedAsync(RowIsReferenced, () => context.Database.ExecuteSqlRawAsync($"DELETE FROM `NewsTags` WHERE `NewsTagId` = '{graph.TagId}'"));

        await context.Database.ExecuteSqlRawAsync($"DELETE FROM `NewsArticleTags` WHERE `NewsTagId` = '{graph.TagId}'");
        await context.Database.ExecuteSqlRawAsync($"DELETE FROM `NewsTags` WHERE `NewsTagId` = '{graph.TagId}'");
    }

    /// <summary>§4.11: an image outlives its file row, which nulls the reference.</summary>
    [SkippableFact]
    public async Task Removing_an_image_file_row_nulls_the_image_reference()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigratedAsync();
        await using var context = NewContext();
        var graph = await AddGraphAsync(context);

        await context.Database.ExecuteSqlRawAsync($"DELETE FROM `NewsArticleFiles` WHERE `NewsArticleFileId` = '{graph.ImageFileId}'");

        Assert.Equal(1L, await MigrationSeam.CountAsync(context,
            $"SELECT COUNT(*) FROM `NewsArticleImages` WHERE `NewsArticleId` = '{graph.ArticleId}' AND `NewsArticleFileId` IS NULL"));
    }

    /// <summary>§4.8: the composite key pins an identity to its article's feed, and a key hash is unique per feed.</summary>
    [SkippableFact]
    public async Task An_identity_must_name_its_articles_feed_and_is_unique_per_feed()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigratedAsync();
        await using var context = NewContext();
        var graph = await AddGraphAsync(context);
        var other = await AddGraphAsync(context);

        // The article of one feed claimed by another feed's id: no such (article, feed) pair exists.
        await AssertRefusedAsync(NoParentRow, () => InsertIdentityAsync(context, graph.ArticleId, other.FeedId, Hash("g:x")));
        // The same key twice in one feed.
        await AssertRefusedAsync(DuplicateKey, () => InsertIdentityAsync(context, graph.ArticleId, graph.FeedId, graph.IdentityKeyHash));
        // …but the same key in another feed is a different identity.
        await InsertIdentityAsync(context, other.ArticleId, other.FeedId, graph.IdentityKeyHash);
    }

    [SkippableFact]
    public async Task A_feed_url_key_and_a_stored_object_are_unique()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigratedAsync();
        await using var context = NewContext();
        var graph = await AddGraphAsync(context);

        await AssertRefusedAsync(DuplicateKey, () => context.Database.ExecuteSqlRawAsync(
            "INSERT INTO `NewsFeeds` (`NewsFeedId`, `Url`, `UrlKeyHash`, `DownloadImages`, `IsActive`, `CreatedAtUtc`, `SettingsVersion`) "
            + "SELECT UUID(), 'https://dup.example/feed', `UrlKeyHash`, 1, 1, NOW(), UUID() FROM `NewsFeeds` WHERE `NewsFeedId` = {0}",
            graph.FeedId));
        await AssertRefusedAsync(DuplicateKey, () => context.Database.ExecuteSqlRawAsync(
            "INSERT INTO `NewsArticleFiles` (`NewsArticleFileId`, `NewsArticleId`, `Kind`, `StoredObjectId`, `ContentType`, `SizeBytes`, `Sha256`, `CreatedAtUtc`) "
            + "VALUES (UUID(), {0}, 2, {1}, 'text/plain', 1, {2}, NOW())", graph.ArticleId, graph.SourceObjectId, Hash("x")));
    }

    /// <summary>§11.1: the FULLTEXT index is in the schema, and boolean-mode prefix terms match title, author and excerpt.</summary>
    [SkippableFact]
    public async Task Search_runs_on_the_fulltext_index_over_title_author_and_excerpt()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigratedAsync();
        await using var context = NewContext();
        await AddGraphAsync(context, title: "Glaciers retreat across the fjords", author: "Ingrid Solberg", excerpt: "Meltwater levels rose sharply.");

        Assert.Equal(3L, await MigrationSeam.CountAsync(context,
            $"SELECT COUNT(*) FROM information_schema.STATISTICS WHERE TABLE_SCHEMA = '{Database}' AND TABLE_NAME = 'NewsArticles' "
            + "AND INDEX_NAME = 'FT_NewsArticles_Title_Author_Excerpt' AND INDEX_TYPE = 'FULLTEXT'"));

        foreach (var expression in new[] { "+glacie*", "+solber*", "+meltwat*", "+fjord* +ingrid*" })
        {
            var matches = await context.NewsArticles
                .Where(a => EF.Functions.Match(new[] { a.Title, a.Author!, a.Excerpt }, expression, MySqlMatchSearchMode.Boolean) > 0)
                .CountAsync();
            Assert.Equal(1, matches);
        }

        Assert.Equal(0, await context.NewsArticles
            .Where(a => EF.Functions.Match(new[] { a.Title, a.Author!, a.Excerpt }, "+volcano*", MySqlMatchSearchMode.Boolean) > 0)
            .CountAsync());
    }

    /// <summary>§16: the sort composites are descending on SortAtUtc, which is what lets a page read the index in order.</summary>
    [SkippableFact]
    public async Task The_sort_indexes_are_descending_on_their_sort_column()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigratedAsync();
        await using var context = NewContext();

        foreach (var (table, index) in new[]
        {
            ("NewsArticles", "IX_NewsArticles_Feed_Read_Sort"), ("NewsArticles", "IX_NewsArticles_Read_Visible_Sort"),
            ("NewsArticles", "IX_NewsArticles_Sort"), ("NewsArticleTags", "IX_NewsArticleTags_Tag_Sort"),
        })
        {
            Assert.Equal(1L, await MigrationSeam.CountAsync(context,
                $"SELECT COUNT(*) FROM information_schema.STATISTICS WHERE TABLE_SCHEMA = '{Database}' AND TABLE_NAME = '{table}' "
                + $"AND INDEX_NAME = '{index}' AND COLUMN_NAME = 'SortAtUtc' AND COLLATION = 'D'"));
        }
    }

    /// <summary>
    /// §3.3: two rows, two tokens. A settings write and a fetch-state write to one feed both commit; a stale write to
    /// either row is refused by its own token alone.
    /// </summary>
    [SkippableFact]
    public async Task Each_row_checks_only_its_own_token()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigratedAsync();
        await using var setup = NewContext();
        var feedId = await AddFeedAsync(setup);

        await using var api = NewContext();
        await using var worker = NewContext();
        var feed = await api.NewsFeeds.SingleAsync(f => f.NewsFeedId == feedId);
        var state = await worker.NewsFeedFetchStates.SingleAsync(s => s.NewsFeedId == feedId);

        feed.CustomTitle = "Renamed";
        feed.SettingsVersion = Guid.NewGuid();
        state.LeaseOwner = Guid.NewGuid();
        state.FetchFence = Guid.NewGuid();
        await api.SaveChangesAsync();
        await worker.SaveChangesAsync();

        await using var staleApi = NewContext();
        await using var staleWorker = NewContext();
        var staleFeed = await staleApi.NewsFeeds.SingleAsync(f => f.NewsFeedId == feedId);
        var staleState = await staleWorker.NewsFeedFetchStates.SingleAsync(s => s.NewsFeedId == feedId);
        await setup.Database.ExecuteSqlRawAsync("UPDATE `NewsFeeds` SET `SettingsVersion` = UUID() WHERE `NewsFeedId` = {0}", feedId);
        await setup.Database.ExecuteSqlRawAsync("UPDATE `NewsFeedFetchStates` SET `FetchFence` = UUID() WHERE `NewsFeedId` = {0}", feedId);

        staleFeed.CustomTitle = "Lost";
        staleState.LeaseOwner = null;
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => staleApi.SaveChangesAsync());
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => staleWorker.SaveChangesAsync());
    }

    // ── Fixtures ─────────────────────────────────────────────────────────────────────────────────

    private sealed record Graph(Guid FeedId, Guid ArticleId, Guid TagId, Guid SourceObjectId, Guid ImageFileId, byte[] IdentityKeyHash);

    private static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));

    private static async Task<Guid> AddFeedAsync(OdysseyContext context)
    {
        var feedId = Guid.NewGuid();
        context.NewsFeeds.Add(new NewsFeed
        {
            NewsFeedId = feedId, Url = $"https://{feedId:N}.example/feed", UrlKeyHash = Hash(feedId.ToString()), DownloadImages = true,
            IsActive = true, CreatedAtUtc = Now, SettingsVersion = Guid.NewGuid(),
        });
        // Inserted in the save that creates the feed (§4.2, §4.3).
        context.NewsFeedFetchStates.Add(new NewsFeedFetchState { NewsFeedId = feedId, FetchFence = Guid.NewGuid(), NextFetchAtUtc = Now });
        context.NewsFeedCounters.Add(new NewsFeedCounter { NewsFeedId = feedId });
        await context.SaveChangesAsync();
        return feedId;
    }

    /// <summary>A feed with a bucket, a tag on the feed and the article, one article with an identity, a source file and a stored image.</summary>
    private static async Task<Graph> AddGraphAsync(
        OdysseyContext context, string title = "A title", string? author = "An author", string excerpt = "An excerpt.")
    {
        var feedId = await AddFeedAsync(context);
        var articleId = Guid.NewGuid();
        var tagId = Guid.NewGuid();
        var volume = new FileStorageVolume { Id = Guid.NewGuid(), Name = $"v{Guid.NewGuid():N}"[..20], LastSeenAtUtc = Now };
        var source = StoredObjectFor(volume, feedId, articleId, ".xml");
        var image = StoredObjectFor(volume, feedId, articleId, ".jpg");
        var imageFileId = Guid.NewGuid();
        var identityKeyHash = Hash($"g:{articleId}");

        context.FileStorageVolumes.Add(volume);
        context.StoredObjects.AddRange(source, image);
        context.NewsFeedBuckets.Add(new NewsFeedBucket { NewsFeedBucketId = Guid.NewGuid(), NewsFeedId = feedId, CurrentBucket = 1, ArticlesInBucket = 1, UpdatedAtUtc = Now });
        context.NewsTags.Add(new NewsTag { NewsTagId = tagId, Name = $"Tag {tagId:N}"[..20], NameKey = $"tag {tagId:N}"[..20], CreatedAtUtc = Now, UpdatedAtUtc = Now });
        context.NewsArticles.Add(new NewsArticle
        {
            NewsArticleId = articleId, NewsFeedId = feedId, Bucket = 1, Title = title, Author = author, Excerpt = excerpt,
            ContentHash = Hash($"c:{articleId}"), IdentityHash = Hash($"h:{articleId}"), BodyLength = 10, FirstSeenAtUtc = Now,
            LastSeenAtUtc = Now, SortAtUtc = Now, FeedVisible = true,
        });
        await context.SaveChangesAsync();

        context.NewsArticleIdentities.Add(new NewsArticleIdentity
        {
            NewsArticleIdentityId = Guid.NewGuid(), NewsArticleId = articleId, NewsFeedId = feedId, Kind = NewsArticleIdentityKind.Guid,
            IdentityKey = $"g:{articleId}", IdentityKeyHash = identityKeyHash, FirstSeenAtUtc = Now, LastSeenAtUtc = Now,
        });
        context.NewsArticleFiles.Add(FileFor(articleId, source, NewsArticleFileKind.Source, "application/xml"));
        var imageFile = FileFor(articleId, image, NewsArticleFileKind.Image, "image/jpeg");
        imageFile.NewsArticleFileId = imageFileId;
        context.NewsArticleFiles.Add(imageFile);
        context.NewsArticleImages.Add(new NewsArticleImage
        {
            NewsArticleImageId = Guid.NewGuid(), NewsArticleId = articleId, NewsArticleFileId = imageFileId, Position = 1, DisplayOrder = 1,
            Origin = NewsArticleImageOrigin.Enclosure, SourceUrl = "https://img.example/a.jpg", State = NewsArticleImageState.Stored,
        });
        context.NewsFeedTags.Add(new NewsFeedTag { NewsFeedTagId = Guid.NewGuid(), NewsFeedId = feedId, NewsTagId = tagId });
        context.NewsArticleTags.Add(new NewsArticleTag { NewsArticleTagId = Guid.NewGuid(), NewsArticleId = articleId, NewsTagId = tagId, SortAtUtc = Now });
        await context.SaveChangesAsync();

        return new Graph(feedId, articleId, tagId, source.Id, imageFileId, identityKeyHash);
    }

    /// <summary>Revision 1 of the article, with its earlier source re-labelled to it, as an edit does (§3.5).</summary>
    private static async Task AddRevisionWithSourceAsync(OdysseyContext context, Graph graph)
    {
        var revisionId = Guid.NewGuid();
        context.NewsArticleRevisions.Add(new NewsArticleRevision
        {
            NewsArticleRevisionId = revisionId, NewsArticleId = graph.ArticleId, RevisionNumber = 1, Title = "Old", ContentHash = Hash("old"),
            IdentityHash = Hash("old-id"), BodyLength = 3, ValidFromUtc = Now.AddHours(-2), ReplacedAtUtc = Now,
        });
        await context.SaveChangesAsync();
        await context.Database.ExecuteSqlRawAsync(
            "UPDATE `NewsArticleFiles` SET `NewsArticleRevisionId` = {0} WHERE `StoredObjectId` = {1}", revisionId, graph.SourceObjectId);
    }

    private static StoredObject StoredObjectFor(FileStorageVolume volume, Guid feedId, Guid articleId, string extension)
    {
        var id = Guid.NewGuid();
        return new StoredObject
        {
            Id = id, VolumeId = volume.Id, FeatureKey = "NewsArticles", RelativePath = $"news/{feedId:N}/1/{articleId:N}/{id:N}{extension}",
            SizeBytes = 1, Sha256Hash = Convert.ToHexString(SHA256.HashData([1])).ToLowerInvariant(), State = StoredObjectState.Committed,
            CreatedAtUtc = Now,
        };
    }

    private static NewsArticleFile FileFor(Guid articleId, StoredObject stored, NewsArticleFileKind kind, string contentType) => new()
    {
        NewsArticleFileId = Guid.NewGuid(), NewsArticleId = articleId, Kind = kind, StoredObjectId = stored.Id, ContentType = contentType,
        SizeBytes = 1, Sha256 = SHA256.HashData([1]), CreatedAtUtc = Now,
    };

    private static Task InsertIdentityAsync(OdysseyContext context, Guid articleId, Guid feedId, byte[] keyHash) =>
        context.Database.ExecuteSqlRawAsync(
            "INSERT INTO `NewsArticleIdentities` (`NewsArticleIdentityId`, `NewsArticleId`, `NewsFeedId`, `Kind`, `IdentityKey`, `IdentityKeyHash`, `FirstSeenAtUtc`, `LastSeenAtUtc`) "
            + "VALUES (UUID(), {0}, {1}, 1, 'g:x', {2}, NOW(), NOW())", articleId, feedId, keyHash);

    private static async Task<Guid> AddRunAsync(OdysseyContext context, Action<NewsFeedJobRun> configure)
    {
        var run = new NewsFeedJobRun
        {
            JobRunId = Guid.NewGuid(), Trigger = JobRunTrigger.Scheduled, Status = JobRunStatus.Running, StartedAtUtc = Now, LastActivityAtUtc = Now,
        };
        configure(run);
        context.JobRuns.Add(run);
        await context.SaveChangesAsync();
        return run.JobRunId;
    }

    /// <summary>The statement fails with exactly this server error, not merely with some SQL error.</summary>
    private static async Task AssertRefusedAsync(int errorNumber, Func<Task> statement)
    {
        var refused = await Assert.ThrowsAsync<MySqlException>(statement);
        Assert.Equal(errorNumber, refused.Number);
    }

    private async Task MigratedAsync() => await fixture.CreateMigratedDatabaseAsync(Database);

    private OdysseyContext NewContext() => new(new DbContextOptionsBuilder<OdysseyContext>()
        .UseMySql(fixture.ConnectionStringFor(Database), ServerVersion.AutoDetect(fixture.OdysseyConnectionString))
        .Options);
}
