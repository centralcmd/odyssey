using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Odyssey.Context;
using Odyssey.Core.FileStorage;
using Odyssey.Core.Internet.News.Files;
using Xunit;

namespace Odyssey.Core.Tests.Internet;

/// <summary>
/// The shape of the news model (issue #470 §4, §16), read from the design-time model of the MariaDB provider — the
/// same model the migration was scaffolded from — so the keys, tokens, CHECKs and index flags are checked on every
/// build, not only where Docker runs. The real engine's enforcement of them is <c>NewsRelationalTests</c>.
/// </summary>
public class NewsModelTests
{
    private static readonly Lazy<IModel> DesignModel = new(() =>
    {
        // No connection is opened: an explicit server version needs no probe, and only the model is read.
        using var context = new OdysseyContext(new DbContextOptionsBuilder<OdysseyContext>()
            .UseMySql("server=localhost;database=unused", new MariaDbServerVersion(new Version(11, 4)))
            .Options);
        return context.GetService<IDesignTimeModel>().Model;
    });

    private static IEntityType Entity<T>() => DesignModel.Value.FindEntityType(typeof(T))!;

    private static readonly Type[] NewsEntities =
    [
        typeof(NewsFeed), typeof(NewsFeedFetchState), typeof(NewsFeedCounter), typeof(NewsStorageTotal), typeof(NewsFeedBucket),
        typeof(NewsHostThrottle), typeof(NewsArticle), typeof(NewsArticleIdentity), typeof(NewsArticleRevision),
        typeof(NewsArticleFile), typeof(NewsArticleImage), typeof(NewsTag), typeof(NewsFeedTag), typeof(NewsArticleTag),
        typeof(NewsFeedCrashEvent),
    ];

    [Fact]
    public void Every_news_table_of_the_spec_is_mapped_under_its_own_name()
    {
        string[] expected =
        [
            "NewsArticleFiles", "NewsArticleIdentities", "NewsArticleImages", "NewsArticleRevisions", "NewsArticleTags",
            "NewsArticles", "NewsFeedBuckets", "NewsFeedCounters", "NewsFeedCrashEvents", "NewsFeedFetchStates",
            "NewsFeedTags", "NewsFeeds", "NewsHostThrottle", "NewsStorageTotals", "NewsTags",
        ];

        Assert.Equal(expected, NewsEntities.Select(t => DesignModel.Value.FindEntityType(t)!.GetTableName()!).Order(StringComparer.Ordinal));
    }

    /// <summary>§4: no user ids anywhere — acting-user attribution goes to logs only.</summary>
    [Fact]
    public void No_news_table_carries_a_user_id_or_a_key_to_AspNetUsers()
    {
        foreach (var type in NewsEntities)
        {
            var entity = DesignModel.Value.FindEntityType(type)!;
            Assert.DoesNotContain(entity.GetProperties(), p => p.Name.Contains("UserId", StringComparison.Ordinal));
            Assert.DoesNotContain(entity.GetForeignKeys(), fk => fk.PrincipalEntityType.ClrType == typeof(ApplicationUser));
        }
    }

    /// <summary>
    /// §3.3: two rows, two tokens. EF puts every concurrency token of an entity in an update's WHERE clause, so a second
    /// token on either row would make the API's and the worker's saves fail on each other's writes.
    /// </summary>
    [Fact]
    public void The_feed_and_its_fetch_state_each_carry_exactly_their_own_token()
    {
        Assert.Equal([nameof(NewsFeed.SettingsVersion)], Tokens<NewsFeed>());
        Assert.Equal([nameof(NewsFeedFetchState.FetchFence)], Tokens<NewsFeedFetchState>());
        Assert.Equal([nameof(NewsHostThrottle.NextAllowedAtUtc)], Tokens<NewsHostThrottle>());
        Assert.Empty(Tokens<NewsFeedCounter>());
        Assert.Empty(Tokens<NewsStorageTotal>());

        static string[] Tokens<T>() => Entity<T>().GetProperties().Where(p => p.IsConcurrencyToken).Select(p => p.Name).ToArray();
    }

    [Fact]
    public void The_fetch_state_and_the_counters_are_one_to_one_with_their_feed_and_cascade()
    {
        foreach (var dependent in new[] { Entity<NewsFeedFetchState>(), Entity<NewsFeedCounter>() })
        {
            var key = Assert.Single(dependent.GetForeignKeys());
            Assert.Equal(typeof(NewsFeed), key.PrincipalEntityType.ClrType);
            Assert.True(key.IsUnique);
            Assert.Equal(DeleteBehavior.Cascade, key.DeleteBehavior);
            Assert.Equal([nameof(NewsFeed.NewsFeedId)], dependent.FindPrimaryKey()!.Properties.Select(p => p.Name));
            Assert.Equal(ValueGenerated.Never, dependent.FindPrimaryKey()!.Properties.Single().ValueGenerated);
        }
    }

    /// <summary>§4.4, AC 78: the single totals row is part of the model's seed, so the migration inserts it.</summary>
    [Fact]
    public void The_storage_totals_row_is_seeded_once_with_zero_totals()
    {
        var seed = Assert.Single(Entity<NewsStorageTotal>().GetSeedData());
        Assert.Equal(NewsStorageTotal.SingletonId, seed[nameof(NewsStorageTotal.Id)]);
        Assert.Equal(0L, seed[nameof(NewsStorageTotal.ImageBytes)]);
        Assert.Equal(0L, seed[nameof(NewsStorageTotal.TextAndSourceBytes)]);
    }

    [Theory]
    [InlineData(typeof(NewsArticle), "CK_NewsArticles_ReadState", "(`IsRead` = 0 AND `ReadAtUtc` IS NULL) OR (`IsRead` = 1 AND `ReadAtUtc` IS NOT NULL)")]
    [InlineData(typeof(NewsArticle), "CK_NewsArticles_BodyOmittedReason", "`BodyOmittedReason` IS NULL OR `BodyOmittedReason` IN (1, 2)")]
    [InlineData(typeof(NewsArticleRevision), "CK_NewsArticleRevisions_BodyOmittedReason", "`BodyOmittedReason` IS NULL OR `BodyOmittedReason` IN (1, 2)")]
    [InlineData(typeof(NewsArticleFile), "CK_NewsArticleFiles_RevisionKind", "`NewsArticleRevisionId` IS NULL OR `Kind` IN (1, 2)")]
    [InlineData(typeof(NewsFeedFetchState), "CK_NewsFeedFetchStates_QuotaReason",
        "(`QuotaReachedAtUtc` IS NULL) = (`QuotaReason` IS NULL) AND (`QuotaReason` IS NULL OR `QuotaReason` IN (1, 2))")]
    [InlineData(typeof(NewsStorageTotal), "CK_NewsStorageTotals_SingleRow", "`Id` = 1")]
    public void Every_spec_check_constraint_is_declared(Type entity, string name, string sql)
    {
        var check = DesignModel.Value.FindEntityType(entity)!.GetCheckConstraints().SingleOrDefault(c => c.ModelName == name);
        Assert.NotNull(check);
        Assert.Equal(sql, check!.Sql);
    }

    /// <summary>§4.14: the job CHECKs admit 9, and the run subtype's counters join the non-negative CHECK.</summary>
    [Fact]
    public void The_job_checks_admit_the_news_job_and_its_counters()
    {
        var runs = Entity<JobRun>().GetCheckConstraints().ToDictionary(c => c.ModelName);
        // Issue #519 appended the recurrence key backfill (10); 5 and 7 are the two retired file moves.
        Assert.Equal("`JobType` IN (1, 2, 3, 4, 6, 8, 9, 10)", runs["CK_JobRuns_JobType"].Sql);
        Assert.Equal("`JobType` IN (1, 2, 3, 4, 6, 8, 9, 10)",
            Entity<JobHeartbeat>().GetCheckConstraints().Single(c => c.ModelName == "CK_JobHeartbeats_JobType").Sql);
        foreach (var counter in new[]
        {
            nameof(NewsFeedJobRun.FeedsFetched), nameof(NewsFeedJobRun.ItemsStored), nameof(NewsFeedJobRun.ItemsRevised),
            nameof(NewsFeedJobRun.ImagesStored), nameof(NewsFeedJobRun.FeedsQuarantined), nameof(NewsFeedJobRun.FeedsMarked),
        })
        {
            Assert.Contains($"COALESCE(`{counter}`, 0) >= 0", runs["CK_JobRuns_CountersNonNegative"].Sql, StringComparison.Ordinal);
        }

        // The per-feed leases are the job's guard, and no new held reason exists (6, BackingOff, is retired).
        Assert.Equal("`HeldReason` IS NULL OR `HeldReason` BETWEEN 1 AND 5", runs["CK_JobRuns_HeldReason"].Sql);
        Assert.Equal("IF(`Status` = 0 AND (`JobType` IN (1, 3, 8) OR `Trigger` = 1), `JobType`, NULL)",
            Entity<JobRun>().FindProperty(nameof(JobRun.ExclusiveGuard))!.GetComputedColumnSql());
        Assert.Equal(typeof(NewsFeedJobRun), DesignModel.Value.GetEntityTypes()
            .Single(e => Equals(e.GetDiscriminatorValue(), Dtos.Application.JobType.NewsFeedFetch)).ClrType);
    }

    /// <summary>§4.10: the feature's one referencing table, RESTRICT to StoredObjects and unique per object.</summary>
    [Fact]
    public void The_article_files_hold_the_one_restrict_key_to_stored_objects()
    {
        var newsKeys = NewsEntities
            .SelectMany(t => DesignModel.Value.FindEntityType(t)!.GetForeignKeys())
            .Where(fk => fk.PrincipalEntityType.ClrType == typeof(StoredObject))
            .ToList();

        var key = Assert.Single(newsKeys);
        Assert.Equal(typeof(NewsArticleFile), key.DeclaringEntityType.ClrType);
        Assert.Equal(DeleteBehavior.Restrict, key.DeleteBehavior);
        Assert.Equal(nameof(NewsArticleFile.StoredObjectId), key.Properties.Single().Name);
        Assert.Contains(Entity<NewsArticleFile>().GetIndexes(), i => i.IsUnique && i.Properties.Single().Name == nameof(NewsArticleFile.StoredObjectId));

        // …and it is the reference the feature declares, so the unreferenced sweep reads this column.
        Assert.Equal(typeof(NewsArticleFile), NewsArticleStorage.Feature.Reference.EntityType);
        Assert.Equal(nameof(NewsArticleFile.StoredObjectId), NewsArticleStorage.Feature.Reference.PropertyName);
    }

    [Theory]
    [InlineData(typeof(NewsFeedBucket), typeof(NewsFeed), DeleteBehavior.Cascade)]
    [InlineData(typeof(NewsArticle), typeof(NewsFeed), DeleteBehavior.Cascade)]
    [InlineData(typeof(NewsArticleRevision), typeof(NewsArticle), DeleteBehavior.Cascade)]
    [InlineData(typeof(NewsArticleImage), typeof(NewsArticle), DeleteBehavior.Cascade)]
    [InlineData(typeof(NewsArticleImage), typeof(NewsArticleFile), DeleteBehavior.SetNull)]
    [InlineData(typeof(NewsFeedTag), typeof(NewsFeed), DeleteBehavior.Cascade)]
    [InlineData(typeof(NewsFeedTag), typeof(NewsTag), DeleteBehavior.Restrict)]
    [InlineData(typeof(NewsArticleTag), typeof(NewsArticle), DeleteBehavior.Cascade)]
    [InlineData(typeof(NewsArticleTag), typeof(NewsTag), DeleteBehavior.Restrict)]
    public void Each_key_has_the_spec_delete_behaviour(Type dependent, Type principal, DeleteBehavior expected)
    {
        var key = Assert.Single(DesignModel.Value.FindEntityType(dependent)!.GetForeignKeys(), fk => fk.PrincipalEntityType.ClrType == principal);
        Assert.Equal(expected, key.DeleteBehavior);
    }

    [Fact]
    public void Article_files_cascade_from_their_article_and_their_revision()
    {
        var keys = Entity<NewsArticleFile>().GetForeignKeys().ToDictionary(fk => fk.PrincipalEntityType.ClrType);
        Assert.Equal(DeleteBehavior.Cascade, keys[typeof(NewsArticle)].DeleteBehavior);
        Assert.Equal(DeleteBehavior.Cascade, keys[typeof(NewsArticleRevision)].DeleteBehavior);
        Assert.False(keys[typeof(NewsArticleRevision)].IsRequired);
    }

    /// <summary>§4.8, decision 12: one composite key to the article, backed by the article's unique pair; none to the feed.</summary>
    [Fact]
    public void An_identity_has_only_the_composite_key_to_its_article()
    {
        var key = Assert.Single(Entity<NewsArticleIdentity>().GetForeignKeys());
        Assert.Equal(typeof(NewsArticle), key.PrincipalEntityType.ClrType);
        Assert.Equal([nameof(NewsArticle.NewsArticleId), nameof(NewsArticle.NewsFeedId)], key.Properties.Select(p => p.Name));
        Assert.Equal([nameof(NewsArticle.NewsArticleId), nameof(NewsArticle.NewsFeedId)], key.PrincipalKey.Properties.Select(p => p.Name));
        Assert.Equal(DeleteBehavior.Cascade, key.DeleteBehavior);
        Assert.Contains(Entity<NewsArticleIdentity>().GetIndexes(), i => i.IsUnique
            && i.Properties.Select(p => p.Name).SequenceEqual([nameof(NewsArticleIdentity.NewsFeedId), nameof(NewsArticleIdentity.IdentityKeyHash)]));
    }

    /// <summary>§16: the composites the list query plan reads, with their descending sort columns.</summary>
    [Theory]
    [InlineData(typeof(NewsArticle), "NewsFeedId,IsRead,SortAtUtc,NewsArticleId", "False,False,True,False", false)]
    [InlineData(typeof(NewsArticle), "IsRead,FeedVisible,SortAtUtc,NewsArticleId", "False,False,True,False", false)]
    [InlineData(typeof(NewsArticle), "SortAtUtc,NewsArticleId", "True,False", false)]
    [InlineData(typeof(NewsArticle), "NewsFeedId,IsRead,FirstSeenAtUtc", "", false)]
    [InlineData(typeof(NewsArticleTag), "NewsTagId,SortAtUtc,NewsArticleId", "False,True,False", false)]
    [InlineData(typeof(NewsArticleTag), "NewsArticleId,NewsTagId", "", true)]
    [InlineData(typeof(NewsArticleTag), "NewsTagId,NewsArticleId", "", false)]
    [InlineData(typeof(NewsFeedTag), "NewsFeedId,NewsTagId", "", true)]
    [InlineData(typeof(NewsFeedTag), "NewsTagId,NewsFeedId", "", false)]
    [InlineData(typeof(NewsFeedFetchState), "QuarantinedAtUtc,NextFetchAtUtc", "", false)]
    [InlineData(typeof(NewsFeedFetchState), "RefreshRequestedAtUtc", "", false)]
    [InlineData(typeof(NewsFeedFetchState), "LeaseExpiresAtUtc", "", false)]
    [InlineData(typeof(NewsFeed), "UrlKeyHash", "", true)]
    [InlineData(typeof(NewsFeedBucket), "NewsFeedId", "", true)]
    [InlineData(typeof(NewsArticleRevision), "NewsArticleId,RevisionNumber", "", true)]
    [InlineData(typeof(NewsArticleFile), "NewsArticleId,NewsArticleRevisionId,Kind", "", false)]
    [InlineData(typeof(NewsArticleImage), "NewsArticleId,Position", "", true)]
    [InlineData(typeof(NewsArticleImage), "NewsArticleId,DisplayOrder", "", false)]
    [InlineData(typeof(NewsArticleImage), "State,NextAttemptAtUtc", "", false)]
    [InlineData(typeof(NewsArticleImage), "NewsArticleFileId", "", true)]
    [InlineData(typeof(NewsTag), "NameKey", "", true)]
    [InlineData(typeof(NewsTag), "ArchivedAtUtc", "", false)]
    [InlineData(typeof(NewsFeedCrashEvent), "DetectedAtUtc", "", false)]
    public void Every_spec_index_is_declared(Type entity, string columns, string descending, bool unique)
    {
        var index = DesignModel.Value.FindEntityType(entity)!.GetIndexes()
            .SingleOrDefault(i => string.Join(',', i.Properties.Select(p => p.Name)) == columns);

        Assert.NotNull(index);
        Assert.Equal(unique, index!.IsUnique);
        var actualDescending = index.IsDescending is { Count: > 0 } flags && flags.Any(f => f) ? string.Join(',', flags) : "";
        Assert.Equal(descending, actualDescending);
    }

    /// <summary>§4.7, §11.1: the article side of the identity key is a unique pair, and search is a FULLTEXT index in the model.</summary>
    [Fact]
    public void Articles_carry_the_unique_identity_pair_and_the_fulltext_index()
    {
        Assert.Contains(Entity<NewsArticle>().GetKeys(), k => !k.IsPrimaryKey()
            && k.Properties.Select(p => p.Name).SequenceEqual([nameof(NewsArticle.NewsArticleId), nameof(NewsArticle.NewsFeedId)]));

        var fullText = Assert.Single(Entity<NewsArticle>().GetIndexes(), i => i.FindAnnotation("MySql:FullTextIndex")?.Value is true);
        Assert.Equal([nameof(NewsArticle.Title), nameof(NewsArticle.Author), nameof(NewsArticle.Excerpt)], fullText.Properties.Select(p => p.Name));
    }

    /// <summary>§4: every long natural key is unique through a SHA-256 column, which is BINARY(32).</summary>
    [Theory]
    [InlineData(typeof(NewsFeed), nameof(NewsFeed.UrlKeyHash))]
    [InlineData(typeof(NewsFeedFetchState), nameof(NewsFeedFetchState.LastBodySha256))]
    [InlineData(typeof(NewsHostThrottle), nameof(NewsHostThrottle.HostKeyHash))]
    [InlineData(typeof(NewsArticle), nameof(NewsArticle.LinkKeyHash))]
    [InlineData(typeof(NewsArticle), nameof(NewsArticle.ContentHash))]
    [InlineData(typeof(NewsArticle), nameof(NewsArticle.IdentityHash))]
    [InlineData(typeof(NewsArticleIdentity), nameof(NewsArticleIdentity.IdentityKeyHash))]
    [InlineData(typeof(NewsArticleRevision), nameof(NewsArticleRevision.ContentHash))]
    [InlineData(typeof(NewsArticleRevision), nameof(NewsArticleRevision.IdentityHash))]
    [InlineData(typeof(NewsArticleFile), nameof(NewsArticleFile.Sha256))]
    public void Every_hash_column_is_binary_32(Type entity, string property) =>
        Assert.Equal("binary(32)", DesignModel.Value.FindEntityType(entity)!.FindProperty(property)!.GetColumnType());

    /// <summary>§3.2: the feature's layout, naming and release mode, as the spec writes them.</summary>
    [Fact]
    public void The_article_feature_is_opaque_graced_and_laid_out_by_feed_bucket_and_article()
    {
        var feature = NewsArticleStorage.Feature;
        Assert.Equal("NewsArticles", feature.Key);
        Assert.Equal("news", feature.RootFolder);
        Assert.Equal(StorageNamingMode.Opaque, feature.NamingMode);
        Assert.Equal(StorageReleaseMode.Graced, feature.ReleaseMode);

        var feed = Guid.NewGuid();
        var article = Guid.NewGuid();
        Assert.Equal([feed.ToString("N"), "12", article.ToString("N")], feature.LayoutFor(new NewsArticleLayout(feed, 12, article)));
        Assert.Throws<StoreContractViolationException>(() => feature.LayoutFor(Guid.NewGuid()));
    }
}
