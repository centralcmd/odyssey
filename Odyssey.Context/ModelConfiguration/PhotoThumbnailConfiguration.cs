using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Odyssey.Context.ModelConfiguration;

internal sealed class PhotoThumbnailConfiguration : IEntityTypeConfiguration<PhotoThumbnail>
{
    public void Configure(EntityTypeBuilder<PhotoThumbnail> entity)
    {
        // CASCADE: a thumbnail is derived from its photo. The stored object it names is not removed by the cascade;
        // the hourly unreferenced sweep collects it, as #346 requires of every consumer.
        entity.HasOne<Photo>()
            .WithMany()
            .HasForeignKey(t => t.PhotoId)
            .OnDelete(DeleteBehavior.Cascade);

        // RESTRICT (#346 consumer rule): an object is released through StageRelease, never deleted from under a row.
        entity.HasOne<StoredObject>()
            .WithMany()
            .HasForeignKey(t => t.StoredObjectId)
            .OnDelete(DeleteBehavior.Restrict);

        entity.Property(t => t.Size).HasConversion<byte>();
        entity.HasIndex(t => new { t.PhotoId, t.Size }).IsUnique();
        entity.HasIndex(t => t.StoredObjectId).IsUnique();

        entity.ToTable(tb =>
        {
            tb.HasCheckConstraint("CK_PhotoThumbnails_SizeRange", "`Size` IN (1, 2)");
            tb.HasCheckConstraint("CK_PhotoThumbnails_PixelsPositive", "`PixelWidth` > 0 AND `PixelHeight` > 0");
        });
    }
}

internal sealed class PhotoThumbnailJobConfiguration : IEntityTypeConfiguration<PhotoThumbnailJobEntry>
{
    public void Configure(EntityTypeBuilder<PhotoThumbnailJobEntry> entity)
    {
        entity.HasOne<Photo>()
            .WithOne()
            .HasForeignKey<PhotoThumbnailJobEntry>(j => j.PhotoId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.Property(j => j.State).HasConversion<byte>();
        entity.Property(j => j.Priority).HasConversion<byte>();
        entity.Property(j => j.FailureCode).IsUnicode(false);

        // The claim, and the recipe sweep (which needs no join to PhotoThumbnails).
        entity.HasIndex(j => new { j.State, j.Priority, j.NextAttemptAtUtc });
        entity.HasIndex(j => new { j.State, j.AttemptedRecipeVersion });

        entity.ToTable(tb =>
        {
            tb.HasCheckConstraint("CK_PhotoThumbnailJobs_StateRange", "`State` IN (0, 1, 2, 3, 4)");
            tb.HasCheckConstraint("CK_PhotoThumbnailJobs_PriorityRange", "`Priority` IN (0, 1)");
            tb.HasCheckConstraint("CK_PhotoThumbnailJobs_LeaseExpiriesNonNegative", "`LeaseExpiries` >= 0");
            tb.HasCheckConstraint("CK_PhotoThumbnailJobs_AttemptsNonNegative", "`Attempts` >= 0");
            tb.HasCheckConstraint("CK_PhotoThumbnailJobs_ManualRequeuesRange", "`ManualRequeues` IN (0, 1)");
            // Every exit from Running clears the run identity and a pending re-run.
            tb.HasCheckConstraint(
                "CK_PhotoThumbnailJobs_RunOnlyWhenRunning", "`State` = 1 OR (`RunId` IS NULL AND `RerunRequested` = 0)");
            tb.HasCheckConstraint("CK_PhotoThumbnailJobs_LeaseWhenRunning", "`State` <> 1 OR `LeaseUntilUtc` IS NOT NULL");
        });
    }
}

internal sealed class PhotoThumbnailBucketConfiguration : IEntityTypeConfiguration<PhotoThumbnailBucket>
{
    public void Configure(EntityTypeBuilder<PhotoThumbnailBucket> entity)
    {
        entity.Property(b => b.Id).ValueGeneratedNever();
        entity.ToTable(tb =>
        {
            tb.HasCheckConstraint("CK_PhotoThumbnailBuckets_Singleton", "`Id` = 1");
            tb.HasCheckConstraint("CK_PhotoThumbnailBuckets_CurrentBucketPositive", "`CurrentBucket` >= 1");
            tb.HasCheckConstraint("CK_PhotoThumbnailBuckets_PhotosWrittenRange", "`PhotosWritten` BETWEEN 0 AND 10000");
        });

        entity.HasData(new PhotoThumbnailBucket
        {
            Id = PhotoThumbnailBucket.SingletonId,
            CurrentBucket = 1,
            PhotosWritten = 0,
            UpdatedAtUtc = new DateTime(2026, 10, 31, 0, 0, 0, DateTimeKind.Utc),
        });
    }
}

internal sealed class PhotoThumbnailIncidentConfiguration : IEntityTypeConfiguration<PhotoThumbnailIncident>
{
    public void Configure(EntityTypeBuilder<PhotoThumbnailIncident> entity)
    {
        entity.Property(i => i.Kind).HasConversion<byte>();
        entity.Property(i => i.WorkerName).IsUnicode(false);
        entity.HasIndex(i => i.OccurredAtUtc);
        entity.ToTable(tb => tb.HasCheckConstraint("CK_PhotoThumbnailIncidents_KindRange", "`Kind` IN (1, 2, 3)"));
    }
}

internal sealed class PhotoThumbnailWorkerHeartbeatConfiguration : IEntityTypeConfiguration<PhotoThumbnailWorkerHeartbeat>
{
    public void Configure(EntityTypeBuilder<PhotoThumbnailWorkerHeartbeat> entity)
    {
        entity.Property(h => h.WorkerName).IsUnicode(false);
        entity.Property(h => h.State).HasConversion<byte>();
        entity.HasIndex(h => h.UpdatedAtUtc);
        entity.ToTable(tb => tb.HasCheckConstraint("CK_PhotoThumbnailWorkerHeartbeats_StateRange", "`State` IN (0, 1, 2, 3, 4)"));
    }
}
