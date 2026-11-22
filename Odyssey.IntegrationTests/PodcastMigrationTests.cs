// EF1002 flags interpolation into ExecuteSqlRawAsync. Every value interpolated below is a Guid or a constant this test
// generated itself — there is no external input.
#pragma warning disable EF1002

using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using Odyssey.Context;
using Odyssey.MigrationService;
using Xunit;

namespace Odyssey.IntegrationTests;

/// <summary>
/// The <c>AddPodcastFeeds</c> migration on a database that already holds news data (issue #556 §11, AC 1, 35): existing
/// feeds and articles read as articles, the podcast keys carry the on-delete behaviour §4 specifies, the CHECKs refuse
/// what they should, and <c>Down()</c> runs after the podcast jobs have recorded runs.
/// </summary>
[Collection(MariaDbCollection.Name)]
public class PodcastMigrationTests(MariaDbFixture fixture)
{
    private const string Database = "odyssey_podcast_migration";
    private const string Migration = "_AddPodcastFeeds";
    private const string Baseline = "_UniqueJournalAndTaskTagNames";
    private const int CheckViolation = 4025;

    [SkippableFact]
    public async Task Existing_news_data_survives_the_migration_and_reads_as_articles()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateAsync();
        try
        {
            await using var context = NewContext();
            await MigrationSeam.MigrateToAsync(context, Baseline);
            var feedId = Guid.NewGuid();
            var articleId = Guid.NewGuid();
            await context.Database.ExecuteSqlRawAsync(
                $"""
                INSERT INTO `NewsFeeds` (`NewsFeedId`, `Url`, `UrlKeyHash`, `IsActive`, `DownloadImages`, `CreatedAtUtc`, `SettingsVersion`)
                VALUES ('{feedId}', 'https://news.example.com/rss', UNHEX(SHA2('{feedId}', 256)), 1, 0, '2026-11-01', UUID());
                INSERT INTO `NewsArticles` (`NewsArticleId`, `NewsFeedId`, `Bucket`, `Title`, `Excerpt`, `ContentHash`, `IdentityHash`,
                    `FirstSeenAtUtc`, `LastSeenAtUtc`, `SortAtUtc`, `IsRead`, `BodyLength`, `RevisionCount`, `FeedVisible`)
                VALUES ('{articleId}', '{feedId}', 1, 'Story', 'Excerpt', UNHEX(SHA2('c', 256)), UNHEX(SHA2('i', 256)),
                    '2026-11-01', '2026-11-01', '2026-11-01', 0, 0, 0, 1);
                """);

            await MigrationRunner.MigrateAsync(context, CancellationToken.None);

            Assert.True(await MigrationSeam.HasRunAsync(context, Migration));
            Assert.Equal(0L, await MigrationSeam.CountAsync(context, $"SELECT `Kind` FROM `NewsFeeds` WHERE `NewsFeedId` = '{feedId}'"));
            Assert.Equal(0L, await MigrationSeam.CountAsync(context, $"SELECT `FeedKind` FROM `NewsArticles` WHERE `NewsArticleId` = '{articleId}'"));
            Assert.Equal(30L, await MigrationSeam.CountAsync(context, $"SELECT `AutoDeleteAfterDays` FROM `NewsFeeds` WHERE `NewsFeedId` = '{feedId}'"));
            var totals = await MigrationSeam.RowAsync(context, "SELECT `AudioBytes`, `ArtworkBytes` FROM `NewsStorageTotals`");
            Assert.Equal((0L, 0L), (Convert.ToInt64(totals!["AudioBytes"]), Convert.ToInt64(totals["ArtworkBytes"])));
        }
        finally
        {
            await DropAsync();
        }
    }

    public static TheoryData<string, string, string> Keys => new()
    {
        { "PodcastFeedDetails", "NewsFeeds", "CASCADE" },
        { "PodcastFeedDetails", "PodcastArtwork", "SET NULL" },
        { "PodcastEpisodes", "NewsArticles", "CASCADE" },
        { "PodcastEpisodes", "PodcastArtwork", "SET NULL" },
        { "PodcastEpisodeAudio", "PodcastEpisodes", "CASCADE" },
        { "PodcastEpisodeAudio", "StoredObjects", "RESTRICT" },
        { "PodcastChapters", "PodcastEpisodes", "CASCADE" },
        { "PodcastArtwork", "NewsFeeds", "CASCADE" },
        { "PodcastArtworkFiles", "PodcastArtwork", "CASCADE" },
        { "PodcastArtworkFiles", "StoredObjects", "RESTRICT" },
    };

    [SkippableTheory]
    [MemberData(nameof(Keys))]
    public async Task Each_podcast_key_has_its_specified_delete_rule(string table, string parent, string rule)
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await using var context = await MigratedAsync();

        var found = await MigrationSeam.ScalarAsync(context,
            $"SELECT `DELETE_RULE` FROM information_schema.REFERENTIAL_CONSTRAINTS WHERE `CONSTRAINT_SCHEMA` = '{Database}' AND `TABLE_NAME` = '{table}' AND `REFERENCED_TABLE_NAME` = '{parent}'");

        Assert.Equal(rule, found as string);
    }

    [SkippableTheory]
    [InlineData("UPDATE `NewsFeeds` SET `Kind` = 7")]
    [InlineData("UPDATE `NewsFeeds` SET `AutoDeleteAfterDays` = 0")]
    [InlineData("UPDATE `NewsArticles` SET `FeedKind` = 9")]
    [InlineData("UPDATE `NewsStorageTotals` SET `AudioReservedBytes` = -1")]
    public async Task The_podcast_checks_refuse_an_impossible_value(string update)
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await using var context = await MigratedAsync();
        await SeedFeedAndArticleAsync(context);

        var refused = await Assert.ThrowsAsync<MySqlException>(() => context.Database.ExecuteSqlRawAsync(update));

        Assert.Equal(CheckViolation, refused.Number);
    }

    [SkippableFact]
    public async Task Down_runs_after_the_podcast_jobs_have_recorded_runs()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await using var context = await MigratedAsync();
        await context.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO `JobRuns` (`JobRunId`, `JobType`, `Trigger`, `Status`, `StartedAtUtc`, `LastActivityAtUtc`, `Generation`,
                `DownloadsStored`, `DownloadsFailed`, `DownloadsDeferred`, `DownloadsRecovered`, `ReservationsRefused`, `AudioBytesStored`)
            VALUES (UUID(), 11, 0, 0, '2026-11-21', '2026-11-21', UUID(), 1, 0, 0, 0, 0, 10);
            INSERT INTO `JobHeartbeats` (`JobType`, `WorkerId`, `LastTickAtUtc`)
            VALUES (12, UUID(), '2026-11-21');
            """);

        await MigrationSeam.MigrateToAsync(context, Baseline);

        Assert.False(await MigrationSeam.HasRunAsync(context, Migration));
        Assert.False(await MigrationSeam.TableExistsAsync(context, "PodcastEpisodes"));
        Assert.Equal(0L, await MigrationSeam.CountAsync(context, "SELECT COUNT(*) FROM `JobRuns` WHERE `JobType` IN (11, 12)"));
        await DropAsync();
    }

    private static Task SeedFeedAndArticleAsync(OdysseyContext context)
    {
        var feedId = Guid.NewGuid();
        return context.Database.ExecuteSqlRawAsync(
            $"""
            INSERT INTO `NewsFeeds` (`NewsFeedId`, `Url`, `UrlKeyHash`, `IsActive`, `DownloadImages`, `CreatedAtUtc`, `SettingsVersion`)
            VALUES ('{feedId}', 'https://news.example.com/rss', UNHEX(SHA2('{feedId}', 256)), 1, 0, '2026-11-01', UUID());
            INSERT INTO `NewsArticles` (`NewsArticleId`, `NewsFeedId`, `Bucket`, `Title`, `Excerpt`, `ContentHash`, `IdentityHash`,
                `FirstSeenAtUtc`, `LastSeenAtUtc`, `SortAtUtc`, `IsRead`, `BodyLength`, `RevisionCount`, `FeedVisible`)
            VALUES (UUID(), '{feedId}', 1, 'Story', 'Excerpt', UNHEX(SHA2('c', 256)), UNHEX(SHA2('i', 256)), '2026-11-01', '2026-11-01', '2026-11-01', 0, 0, 0, 1);
            """);
    }

    private async Task<OdysseyContext> MigratedAsync()
    {
        await RecreateAsync();
        var context = NewContext();
        await MigrationRunner.MigrateAsync(context, CancellationToken.None);
        return context;
    }

    private OdysseyContext NewContext() =>
        new(new DbContextOptionsBuilder<OdysseyContext>()
            .UseMySql(fixture.ConnectionStringFor(Database), ServerVersion.AutoDetect(fixture.OdysseyConnectionString))
            .Options);

    private async Task RecreateAsync()
    {
        await DropAsync();
        await using var server = fixture.CreateOdysseyContext();
        await server.Database.ExecuteSqlRawAsync($"CREATE DATABASE `{Database}`");
    }

    private async Task DropAsync()
    {
        await using var server = fixture.CreateOdysseyContext();
        await server.Database.ExecuteSqlRawAsync($"DROP DATABASE IF EXISTS `{Database}`");
    }
}
