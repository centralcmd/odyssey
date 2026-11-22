using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Odyssey.Dtos;

namespace Odyssey.Context.ModelConfiguration;

// Podcasts in the news reader (issue #556 §4). Everything cascades from the feed or the episode, so the batched news
// purge finds little left when it deletes a feed row; the only RESTRICT keys are the two to StoredObjects (#346
// consumer rule). Artwork links are SET NULL: an episode survives its image.

internal sealed class PodcastFeedDetailsConfiguration : IEntityTypeConfiguration<PodcastFeedDetails>
{
    public void Configure(EntityTypeBuilder<PodcastFeedDetails> entity)
    {
        entity.HasOne<NewsFeed>()
            .WithOne()
            .HasForeignKey<PodcastFeedDetails>(d => d.NewsFeedId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasOne<PodcastArtwork>()
            .WithMany()
            .HasForeignKey(d => d.ArtworkId)
            .OnDelete(DeleteBehavior.SetNull);

        entity.Property(d => d.PodcastType).HasConversion<byte?>();

        entity.ToTable(tb =>
        {
            tb.HasCheckConstraint("CK_PodcastFeedDetails_PodcastType", "`PodcastType` IS NULL OR `PodcastType` IN (1, 2)");
            tb.HasCheckConstraint(
                "CK_PodcastFeedDetails_CountersNonNegative", "`AutoDownloadsToday` >= 0 AND `ArtworkIncidentCount` >= 0");
        });
    }
}

internal sealed class PodcastEpisodeConfiguration : IEntityTypeConfiguration<PodcastEpisode>
{
    public void Configure(EntityTypeBuilder<PodcastEpisode> entity)
    {
        // The NewsArticleIdentities shape: the composite key pins the episode to its article's feed.
        entity.HasOne<NewsArticle>()
            .WithOne()
            .HasForeignKey<PodcastEpisode>(e => new { e.NewsArticleId, e.NewsFeedId })
            .HasPrincipalKey<NewsArticle>(a => new { a.NewsArticleId, a.NewsFeedId })
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasOne<PodcastArtwork>()
            .WithMany()
            .HasForeignKey(e => e.ArtworkId)
            .OnDelete(DeleteBehavior.SetNull);

        entity.Property(e => e.EpisodeType).HasConversion<byte>();
        entity.Property(e => e.ChaptersFetchState).HasConversion<byte>();
        entity.Property(e => e.EnclosureDeclaredType).IsUnicode(false);
        entity.Property(e => e.ChaptersUrlKeyHash).Hash();
        entity.Property(e => e.EpisodeHash).Hash();

        entity.HasIndex(e => new { e.NewsFeedId, e.SortAtUtc, e.NewsArticleId })
            .IsDescending(false, true, false)
            .HasDatabaseName("IX_PodcastEpisodes_Feed_Sort");
        entity.HasIndex(e => new { e.ChaptersFetchState, e.ChaptersNextAttemptAtUtc })
            .HasDatabaseName("IX_PodcastEpisodes_ChaptersQueue");

        entity.ToTable(tb =>
        {
            tb.HasCheckConstraint("CK_PodcastEpisodes_EpisodeType", "`EpisodeType` IN (0, 1, 2)");
            tb.HasCheckConstraint("CK_PodcastEpisodes_ChaptersFetchState", "`ChaptersFetchState` IN (0, 1, 2, 3)");
            tb.HasCheckConstraint(
                "CK_PodcastEpisodes_Ranges",
                $"(`DurationSeconds` IS NULL OR `DurationSeconds` BETWEEN 0 AND {PodcastLimits.MaxDurationSeconds})"
                + $" AND (`SeasonNumber` IS NULL OR `SeasonNumber` BETWEEN 0 AND {PodcastLimits.MaxEpisodeNumber})"
                + $" AND (`EpisodeNumber` IS NULL OR `EpisodeNumber` BETWEEN 0 AND {PodcastLimits.MaxEpisodeNumber})"
                + " AND (`EnclosureDeclaredLength` IS NULL OR `EnclosureDeclaredLength` >= 0)"
                + " AND (`PlaybackPositionSeconds` IS NULL OR `PlaybackPositionSeconds` >= 0)");
        });
    }
}

internal sealed class PodcastEpisodeAudioConfiguration : IEntityTypeConfiguration<PodcastEpisodeAudio>
{
    public void Configure(EntityTypeBuilder<PodcastEpisodeAudio> entity)
    {
        entity.HasOne<PodcastEpisode>()
            .WithOne()
            .HasForeignKey<PodcastEpisodeAudio>(a => a.NewsArticleId)
            .OnDelete(DeleteBehavior.Cascade);

        // RESTRICT (#346 consumer rule): released through StageRelease in the save that nulls this column.
        entity.HasOne<StoredObject>()
            .WithMany()
            .HasForeignKey(a => a.StoredObjectId)
            .OnDelete(DeleteBehavior.Restrict);
        entity.HasIndex(a => a.StoredObjectId).IsUnique();

        entity.Property(a => a.State).HasConversion<byte>();
        entity.Property(a => a.FailureKind).HasConversion<byte?>();
        entity.Property(a => a.DeferredReason).HasConversion<byte?>();
        entity.Property(a => a.RequestedBy).HasConversion<byte?>();
        entity.Property(a => a.ContentType).IsUnicode(false);
        entity.Property(a => a.EnclosureHostKeyHash).Hash();
        entity.Property(a => a.Sha256).Hash();

        // At most one download per feed and per host across every replica (§3.6 step 2): the JobRuns.ExclusiveGuard
        // pattern. Only real MariaDB enforces them; the in-memory tiers rely on the claim's read-side exclusion.
        // RTRIM: MariaDB rejects an indexed generated column that reads a CHAR column as is (its value would depend on
        // PAD_CHAR_TO_FULL_LENGTH); stripping the padding makes the expression deterministic. Verified on 11.4.
        entity.Property(a => a.FeedDownloadGuard)
            .HasColumnType("varchar(36)")
            .UseCollation("ascii_general_ci")
            .HasComputedColumnSql("IF(`State` = 2, RTRIM(`NewsFeedId`), NULL)", stored: false);
        entity.Property(a => a.HostDownloadGuard)
            .Hash()
            .HasComputedColumnSql("IF(`State` = 2, `EnclosureHostKeyHash`, NULL)", stored: false);
        entity.HasIndex(a => a.FeedDownloadGuard).IsUnique().HasDatabaseName("UX_PodcastEpisodeAudio_FeedDownloadGuard");
        entity.HasIndex(a => a.HostDownloadGuard).IsUnique().HasDatabaseName("UX_PodcastEpisodeAudio_HostDownloadGuard");

        entity.HasIndex(a => new { a.State, a.NextAttemptAtUtc }).HasDatabaseName("IX_PodcastEpisodeAudio_Queue");
        entity.HasIndex(a => new { a.NewsFeedId, a.State }).HasDatabaseName("IX_PodcastEpisodeAudio_Feed_State");
        entity.HasIndex(a => a.LeaseExpiresAtUtc).HasDatabaseName("IX_PodcastEpisodeAudio_Lease");
        entity.HasIndex(a => new { a.State, a.StoredAtUtc }).HasDatabaseName("IX_PodcastEpisodeAudio_Stored");

        entity.ToTable(tb =>
        {
            tb.HasCheckConstraint("CK_PodcastEpisodeAudio_State", "`State` IN (0, 1, 2, 3, 4, 5)");
            tb.HasCheckConstraint("CK_PodcastEpisodeAudio_StoredHasObject", "(`State` = 3) = (`StoredObjectId` IS NOT NULL)");
            tb.HasCheckConstraint("CK_PodcastEpisodeAudio_FailureKind", "`FailureKind` IS NULL OR `FailureKind` BETWEEN 1 AND 6");
            tb.HasCheckConstraint("CK_PodcastEpisodeAudio_DeferredReason", "`DeferredReason` IS NULL OR `DeferredReason` IN (1, 2)");
            tb.HasCheckConstraint("CK_PodcastEpisodeAudio_RequestedBy", "`RequestedBy` IS NULL OR `RequestedBy` IN (1, 2)");
            tb.HasCheckConstraint(
                "CK_PodcastEpisodeAudio_Sizes", "`ReservedBytes` >= 0 AND (`SizeBytes` IS NULL OR `SizeBytes` >= 0)");
        });
    }
}

internal sealed class PodcastChapterConfiguration : IEntityTypeConfiguration<PodcastChapter>
{
    public void Configure(EntityTypeBuilder<PodcastChapter> entity)
    {
        entity.HasOne<PodcastEpisode>()
            .WithMany()
            .HasForeignKey(c => c.NewsArticleId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.Property(c => c.Source).HasConversion<byte>();
        entity.HasIndex(c => new { c.NewsArticleId, c.Source, c.Position })
            .IsUnique()
            .HasDatabaseName("UX_PodcastChapters_Episode_Source_Position");

        entity.ToTable(tb =>
        {
            tb.HasCheckConstraint("CK_PodcastChapters_Source", "`Source` IN (1, 2, 3)");
            tb.HasCheckConstraint(
                "CK_PodcastChapters_Times",
                $"`StartMs` BETWEEN 0 AND {PodcastLimits.MaxChapterMilliseconds}"
                + $" AND (`EndMs` IS NULL OR (`EndMs` >= `StartMs` AND `EndMs` <= {PodcastLimits.MaxChapterMilliseconds}))");
        });
    }
}

internal sealed class PodcastArtworkConfiguration : IEntityTypeConfiguration<PodcastArtwork>
{
    public void Configure(EntityTypeBuilder<PodcastArtwork> entity)
    {
        entity.HasOne<NewsFeed>()
            .WithMany()
            .HasForeignKey(a => a.NewsFeedId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.Property(a => a.State).HasConversion<byte>();
        entity.Property(a => a.RejectedReason).HasConversion<byte?>();
        entity.Property(a => a.SourceUrlKeyHash).Hash();
        entity.HasIndex(a => new { a.NewsFeedId, a.SourceUrlKeyHash }).IsUnique().HasDatabaseName("UX_PodcastArtwork_Feed_Url");
        entity.HasIndex(a => new { a.State, a.NextAttemptAtUtc }).HasDatabaseName("IX_PodcastArtwork_Queue");

        entity.ToTable(tb =>
        {
            tb.HasCheckConstraint("CK_PodcastArtwork_State", "`State` IN (0, 1, 2, 3, 4)");
            tb.HasCheckConstraint("CK_PodcastArtwork_DeferredReason", "`DeferredReason` IS NULL OR `DeferredReason` IN (1)");
            tb.HasCheckConstraint("CK_PodcastArtwork_RejectedReason", "`RejectedReason` IS NULL OR `RejectedReason` IN (1, 2, 3)");
        });
    }
}

internal sealed class PodcastArtworkFileConfiguration : IEntityTypeConfiguration<PodcastArtworkFile>
{
    public void Configure(EntityTypeBuilder<PodcastArtworkFile> entity)
    {
        entity.HasOne<PodcastArtwork>()
            .WithMany()
            .HasForeignKey(f => f.PodcastArtworkId)
            .OnDelete(DeleteBehavior.Cascade);

        // RESTRICT (#346 consumer rule). A cascade-deleted file row is caught by the store's hourly unreferenced sweep.
        entity.HasOne<StoredObject>()
            .WithMany()
            .HasForeignKey(f => f.StoredObjectId)
            .OnDelete(DeleteBehavior.Restrict);
        entity.HasIndex(f => f.StoredObjectId).IsUnique();

        entity.Property(f => f.Kind).HasConversion<byte>();
        entity.Property(f => f.ContentType).IsUnicode(false);
        entity.Property(f => f.Sha256).Hash();
        entity.HasIndex(f => new { f.PodcastArtworkId, f.Kind }).IsUnique();

        entity.ToTable(tb => tb.HasCheckConstraint("CK_PodcastArtworkFiles_Kind", "`Kind` IN (1, 2, 3)"));
    }
}

internal sealed class PodcastArtworkIncidentConfiguration : IEntityTypeConfiguration<PodcastArtworkIncident>
{
    public void Configure(EntityTypeBuilder<PodcastArtworkIncident> entity)
    {
        entity.Property(i => i.Kind).HasConversion<byte>();
        entity.HasIndex(i => i.DetectedAtUtc);
        entity.ToTable(tb => tb.HasCheckConstraint("CK_PodcastArtworkIncidents_Kind", "`Kind` IN (1, 2)"));
    }
}
