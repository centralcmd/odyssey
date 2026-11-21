using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Odyssey.Dtos;

namespace Odyssey.Context.ModelConfiguration;

// The news reader (issue #470 §4, §16). Everything below a feed cascades from it, so the batched purge can remove a
// feed's rows chunk by chunk and the final feed delete finds little left; the one RESTRICT key on this side of the
// model is the files' key to StoredObjects (#346 consumer rule), and the two tag links RESTRICT their tag, so a tag in
// use answers 409 rather than vanishing from records.

internal static class NewsColumns
{
    /// <summary>A SHA-256 key column: <c>BINARY(32)</c> on MariaDB.</summary>
    public static PropertyBuilder<T> Hash<T>(this PropertyBuilder<T> property) =>
        property.HasMaxLength(NewsLimits.HashLength).IsFixedLength();
}

internal sealed class NewsFeedConfiguration : IEntityTypeConfiguration<NewsFeed>
{
    public void Configure(EntityTypeBuilder<NewsFeed> entity)
    {
        entity.Property(f => f.UrlKeyHash).Hash();
        entity.HasIndex(f => f.UrlKeyHash).IsUnique();

        // Podcasts (issue #556 §4.1): every existing feed becomes an article feed, and an existing row's auto-delete
        // days start at the default the rule takes when it is first turned on.
        entity.Property(f => f.Kind).HasConversion<byte>();
        entity.Property(f => f.AutoDeleteAfterDays).HasDefaultValue(PodcastLimits.DefaultAutoDeleteDays);
        entity.ToTable(tb =>
        {
            tb.HasCheckConstraint("CK_NewsFeeds_Kind", "`Kind` IN (0, 1)");
            tb.HasCheckConstraint(
                "CK_NewsFeeds_AutoDeleteDays",
                $"`AutoDeleteAfterDays` BETWEEN {PodcastLimits.MinAutoDeleteDays} AND {PodcastLimits.MaxAutoDeleteDays}");
        });
    }
}

internal sealed class NewsFeedFetchStateConfiguration : IEntityTypeConfiguration<NewsFeedFetchState>
{
    public void Configure(EntityTypeBuilder<NewsFeedFetchState> entity)
    {
        entity.HasOne<NewsFeed>()
            .WithOne()
            .HasForeignKey<NewsFeedFetchState>(s => s.NewsFeedId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.Property(s => s.Format).HasConversion<byte>();
        entity.Property(s => s.LastErrorKind).HasConversion<byte>();
        entity.Property(s => s.QuotaReason).HasConversion<byte?>();
        entity.Property(s => s.ETag).IsUnicode(false);
        entity.Property(s => s.LastModified).IsUnicode(false);
        entity.Property(s => s.LastBodySha256).Hash();

        // Claiming due feeds (the NewsFeeds conditions are read through the join) and crash detection.
        entity.HasIndex(s => new { s.QuarantinedAtUtc, s.NextFetchAtUtc });
        entity.HasIndex(s => s.RefreshRequestedAtUtc);
        entity.HasIndex(s => s.LeaseExpiresAtUtc);

        entity.ToTable(tb => tb.HasCheckConstraint(
            "CK_NewsFeedFetchStates_QuotaReason",
            "(`QuotaReachedAtUtc` IS NULL) = (`QuotaReason` IS NULL) AND (`QuotaReason` IS NULL OR `QuotaReason` IN (1, 2))"));
    }
}

internal sealed class NewsFeedCounterConfiguration : IEntityTypeConfiguration<NewsFeedCounter>
{
    public void Configure(EntityTypeBuilder<NewsFeedCounter> entity) =>
        entity.HasOne<NewsFeed>()
            .WithOne()
            .HasForeignKey<NewsFeedCounter>(c => c.NewsFeedId)
            .OnDelete(DeleteBehavior.Cascade);
}

internal sealed class NewsStorageTotalConfiguration : IEntityTypeConfiguration<NewsStorageTotal>
{
    public void Configure(EntityTypeBuilder<NewsStorageTotal> entity)
    {
        entity.ToTable(tb =>
        {
            tb.HasCheckConstraint("CK_NewsStorageTotals_SingleRow", "`Id` = 1");
            tb.HasCheckConstraint(
                "CK_NewsStorageTotals_PodcastBytesNonNegative",
                "`AudioBytes` >= 0 AND `AudioPendingReleaseBytes` >= 0 AND `AudioReservedBytes` >= 0 AND `ArtworkBytes` >= 0");
        });
        entity.HasData(new NewsStorageTotal { Id = NewsStorageTotal.SingletonId, ImageBytes = 0, TextAndSourceBytes = 0 });
    }
}

internal sealed class NewsFeedBucketConfiguration : IEntityTypeConfiguration<NewsFeedBucket>
{
    public void Configure(EntityTypeBuilder<NewsFeedBucket> entity)
    {
        entity.HasOne<NewsFeed>()
            .WithOne()
            .HasForeignKey<NewsFeedBucket>(b => b.NewsFeedId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasIndex(b => b.NewsFeedId).IsUnique();
    }
}

internal sealed class NewsHostThrottleConfiguration : IEntityTypeConfiguration<NewsHostThrottle>
{
    public void Configure(EntityTypeBuilder<NewsHostThrottle> entity) =>
        entity.Property(t => t.HostKeyHash).Hash();
}

internal sealed class NewsArticleConfiguration : IEntityTypeConfiguration<NewsArticle>
{
    public void Configure(EntityTypeBuilder<NewsArticle> entity)
    {
        entity.HasOne<NewsFeed>()
            .WithMany()
            .HasForeignKey(a => a.NewsFeedId)
            .OnDelete(DeleteBehavior.Cascade);

        // The principal of the identities' composite key (§4.8): it is what pins an identity to its article's feed.
        entity.HasAlternateKey(a => new { a.NewsArticleId, a.NewsFeedId });

        entity.Property(a => a.BodyOmittedReason).HasConversion<byte?>();
        entity.Property(a => a.LinkKeyHash).Hash();
        entity.Property(a => a.ContentHash).Hash();
        entity.Property(a => a.IdentityHash).Hash();

        // Unread per feed (and the per-feed list branches and purge chunks); the global unread / newest list; the
        // capped-expansion scan; the mark-read watermark (§16). MariaDB has no partial index, so the IsRead-leading
        // composites stand in for partial unread indexes.
        entity.HasIndex(a => new { a.NewsFeedId, a.IsRead, a.SortAtUtc, a.NewsArticleId })
            .IsDescending(false, false, true, false)
            .HasDatabaseName("IX_NewsArticles_Feed_Read_Sort");
        entity.HasIndex(a => new { a.IsRead, a.FeedVisible, a.SortAtUtc, a.NewsArticleId })
            .IsDescending(false, false, true, false)
            .HasDatabaseName("IX_NewsArticles_Read_Visible_Sort");
        entity.HasIndex(a => new { a.SortAtUtc, a.NewsArticleId })
            .IsDescending(true, false)
            .HasDatabaseName("IX_NewsArticles_Sort");
        entity.HasIndex(a => new { a.NewsFeedId, a.IsRead, a.FirstSeenAtUtc })
            .HasDatabaseName("IX_NewsArticles_Feed_Read_FirstSeen");

        // Podcasts (issue #556 §4.8, §4.9): the article stream's and the episode list's no-feed-filter plan — an equality
        // on FeedKind, then the sort — so excluding the other kind stops after `limit` rows. FeedKind is an index aid: the
        // readers add the feeds still being rewritten by id (NewsFeedKindRewrite). FeedVisible is left out of the key on
        // purpose: neither reader can use it as an equality, because its own rewrite lags an archive in the same way.
        entity.Property(a => a.FeedKind).HasConversion<byte>();
        entity.HasIndex(a => new { a.FeedKind, a.SortAtUtc, a.NewsArticleId })
            .IsDescending(false, true, false)
            .HasDatabaseName("IX_NewsArticles_Kind_Sort");

        // Search (§8.4): part of the model, so the migration and the drift guard both see it.
        entity.HasIndex(a => new { a.Title, a.Author, a.Excerpt })
            .IsFullText()
            .HasDatabaseName("FT_NewsArticles_Title_Author_Excerpt");

        entity.ToTable(tb =>
        {
            tb.HasCheckConstraint(
                "CK_NewsArticles_ReadState",
                "(`IsRead` = 0 AND `ReadAtUtc` IS NULL) OR (`IsRead` = 1 AND `ReadAtUtc` IS NOT NULL)");
            tb.HasCheckConstraint(
                "CK_NewsArticles_BodyOmittedReason", "`BodyOmittedReason` IS NULL OR `BodyOmittedReason` IN (1, 2)");
            tb.HasCheckConstraint("CK_NewsArticles_FeedKind", "`FeedKind` IN (0, 1)");
        });
    }
}

internal sealed class NewsArticleIdentityConfiguration : IEntityTypeConfiguration<NewsArticleIdentity>
{
    public void Configure(EntityTypeBuilder<NewsArticleIdentity> entity)
    {
        // The only key (§4.8, decision 12): CASCADE with the article. A second key to NewsFeeds would make a cascading
        // feed delete depend on InnoDB's constraint-check order, and the article already cascades from its feed.
        entity.HasOne<NewsArticle>()
            .WithMany()
            .HasForeignKey(i => new { i.NewsArticleId, i.NewsFeedId })
            .HasPrincipalKey(a => new { a.NewsArticleId, a.NewsFeedId })
            .OnDelete(DeleteBehavior.Cascade);

        entity.Property(i => i.Kind).HasConversion<byte>();
        entity.Property(i => i.IdentityKeyHash).Hash();
        entity.HasIndex(i => new { i.NewsFeedId, i.IdentityKeyHash }).IsUnique();
    }
}

internal sealed class NewsArticleRevisionConfiguration : IEntityTypeConfiguration<NewsArticleRevision>
{
    public void Configure(EntityTypeBuilder<NewsArticleRevision> entity)
    {
        entity.HasOne<NewsArticle>()
            .WithMany()
            .HasForeignKey(r => r.NewsArticleId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.Property(r => r.BodyOmittedReason).HasConversion<byte?>();
        entity.Property(r => r.ContentHash).Hash();
        entity.Property(r => r.IdentityHash).Hash();
        entity.HasIndex(r => new { r.NewsArticleId, r.RevisionNumber }).IsUnique();

        entity.ToTable(tb => tb.HasCheckConstraint(
            "CK_NewsArticleRevisions_BodyOmittedReason", "`BodyOmittedReason` IS NULL OR `BodyOmittedReason` IN (1, 2)"));
    }
}

internal sealed class NewsArticleFileConfiguration : IEntityTypeConfiguration<NewsArticleFile>
{
    public void Configure(EntityTypeBuilder<NewsArticleFile> entity)
    {
        entity.HasOne<NewsArticle>()
            .WithMany()
            .HasForeignKey(f => f.NewsArticleId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasOne<NewsArticleRevision>()
            .WithMany()
            .HasForeignKey(f => f.NewsArticleRevisionId)
            .OnDelete(DeleteBehavior.Cascade);

        // RESTRICT (#346 consumer rule): an object is released through StageRelease, never deleted from under a row. A
        // cascade-deleted file row is caught by the store's hourly unreferenced sweep.
        entity.HasOne<StoredObject>()
            .WithMany()
            .HasForeignKey(f => f.StoredObjectId)
            .OnDelete(DeleteBehavior.Restrict);

        entity.Property(f => f.Kind).HasConversion<byte>();
        entity.Property(f => f.ContentType).IsUnicode(false);
        entity.Property(f => f.Sha256).Hash();
        entity.HasIndex(f => f.StoredObjectId).IsUnique();
        entity.HasIndex(f => new { f.NewsArticleId, f.NewsArticleRevisionId, f.Kind });

        // A revision keeps only its source and its text; images always belong to the article.
        entity.ToTable(tb => tb.HasCheckConstraint(
            "CK_NewsArticleFiles_RevisionKind", "`NewsArticleRevisionId` IS NULL OR `Kind` IN (1, 2)"));
    }
}

internal sealed class NewsArticleImageConfiguration : IEntityTypeConfiguration<NewsArticleImage>
{
    public void Configure(EntityTypeBuilder<NewsArticleImage> entity)
    {
        entity.HasOne<NewsArticle>()
            .WithMany()
            .HasForeignKey(i => i.NewsArticleId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasOne<NewsArticleFile>()
            .WithOne()
            .HasForeignKey<NewsArticleImage>(i => i.NewsArticleFileId)
            .OnDelete(DeleteBehavior.SetNull);

        entity.Property(i => i.Origin).HasConversion<byte>();
        entity.Property(i => i.State).HasConversion<byte>();
        entity.Property(i => i.DeferredReason).HasConversion<byte?>();

        entity.HasIndex(i => new { i.NewsArticleId, i.Position }).IsUnique();
        entity.HasIndex(i => new { i.NewsArticleId, i.DisplayOrder });
        entity.HasIndex(i => new { i.State, i.NextAttemptAtUtc });
        entity.HasIndex(i => i.NewsArticleFileId).IsUnique();
    }
}

internal sealed class NewsTagConfiguration : IEntityTypeConfiguration<NewsTag>
{
    public void Configure(EntityTypeBuilder<NewsTag> entity)
    {
        entity.HasIndex(t => t.NameKey).IsUnique();
        entity.HasIndex(t => t.ArchivedAtUtc);
    }
}

internal sealed class NewsFeedTagConfiguration : IEntityTypeConfiguration<NewsFeedTag>
{
    public void Configure(EntityTypeBuilder<NewsFeedTag> entity)
    {
        entity.HasOne<NewsFeed>()
            .WithMany()
            .HasForeignKey(l => l.NewsFeedId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasOne<NewsTag>()
            .WithMany()
            .HasForeignKey(l => l.NewsTagId)
            .OnDelete(DeleteBehavior.Restrict);

        entity.HasIndex(l => new { l.NewsFeedId, l.NewsTagId }).IsUnique();
        entity.HasIndex(l => new { l.NewsTagId, l.NewsFeedId });
    }
}

internal sealed class NewsArticleTagConfiguration : IEntityTypeConfiguration<NewsArticleTag>
{
    public void Configure(EntityTypeBuilder<NewsArticleTag> entity)
    {
        entity.HasOne<NewsArticle>()
            .WithMany()
            .HasForeignKey(l => l.NewsArticleId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasOne<NewsTag>()
            .WithMany()
            .HasForeignKey(l => l.NewsTagId)
            .OnDelete(DeleteBehavior.Restrict);

        entity.HasIndex(l => new { l.NewsArticleId, l.NewsTagId }).IsUnique();
        entity.HasIndex(l => new { l.NewsTagId, l.NewsArticleId });
        // The own-tag branches of the list query (§5.3).
        entity.HasIndex(l => new { l.NewsTagId, l.SortAtUtc, l.NewsArticleId })
            .IsDescending(false, true, false)
            .HasDatabaseName("IX_NewsArticleTags_Tag_Sort");
    }
}

internal sealed class NewsFeedCrashEventConfiguration : IEntityTypeConfiguration<NewsFeedCrashEvent>
{
    public void Configure(EntityTypeBuilder<NewsFeedCrashEvent> entity) =>
        entity.HasIndex(e => e.DetectedAtUtc);
}
