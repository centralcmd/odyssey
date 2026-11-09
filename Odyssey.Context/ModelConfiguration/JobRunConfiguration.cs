using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Odyssey.Dtos.Application;

namespace Odyssey.Context.ModelConfiguration;

/// <summary>The shared run log (issue #409 §4.1, §8): the discriminator, the two generated guards and the CHECKs.</summary>
internal sealed class JobRunConfiguration : IEntityTypeConfiguration<JobRun>
{
    public void Configure(EntityTypeBuilder<JobRun> entity)
    {
        entity.HasDiscriminator(run => run.JobType)
            .HasValue<ExchangeRateSyncJobRun>(JobType.ExchangeRateSync)
            .HasValue<WeatherJobRun>(JobType.Weather)
            .HasValue<FileStorageMaintenanceJobRun>(JobType.FileStorageMaintenance)
            .HasValue<PhotoThumbnailJobRun>(JobType.PhotoThumbnails)
            .HasValue<PhotoOriginalBackfillJobRun>(JobType.PhotoOriginalBackfill)
            .HasValue<FileAnalysisJobRun>(JobType.FileAnalysis)
            .HasValue<FileBlobBackfillJobRun>(JobType.FileBlobBackfill)
            .HasValue<InstrumentPriceSyncJobRun>(JobType.InstrumentPriceSync)
            .IsComplete();

        entity.Property(run => run.OpenHeldGuard)
            .HasComputedColumnSql("IF(`Status` = 5 AND `FinishedAtUtc` IS NULL, 1, NULL)", stored: false);

        // Every exchange-rate sync and file-storage run is exclusive (a purge holds the sync's guard too); for the
        // work-driven jobs only a manual run is, so scheduled bursts of several processes can run side by side.
        entity.Property(run => run.ExclusiveGuard)
            .HasComputedColumnSql("IF(`Status` = 0 AND (`JobType` IN (1, 3, 8) OR `Trigger` = 1), `JobType`, NULL)", stored: false);

        entity.ToTable(tb =>
        {
            tb.HasCheckConstraint("CK_JobRuns_JobType", "`JobType` IN (1, 2, 3, 4, 5, 6, 7, 8)");
            tb.HasCheckConstraint("CK_JobRuns_Trigger", "`Trigger` IN (0, 1, 2)");
            tb.HasCheckConstraint("CK_JobRuns_Status", "`Status` IN (0, 1, 2, 3, 4, 5)");
            tb.HasCheckConstraint(
                "CK_JobRuns_FailureCategory", "`FailureCategory` IS NULL OR `FailureCategory` BETWEEN 1 AND 10");
            tb.HasCheckConstraint("CK_JobRuns_HeldReason", "`HeldReason` IS NULL OR `HeldReason` BETWEEN 1 AND 6");
            tb.HasCheckConstraint(
                "CK_JobRuns_CountersNonNegative",
                "COALESCE(`ItemsProcessed`, 0) >= 0 AND COALESCE(`ItemsFailed`, 0) >= 0 AND COALESCE(`DurationMs`, 0) >= 0"
                + " AND COALESCE(`RowsInserted`, 0) >= 0 AND COALESCE(`RowsSkipped`, 0) >= 0"
                + " AND COALESCE(`RowsRejected`, 0) >= 0 AND COALESCE(`RowsDeleted`, 0) >= 0"
                + " AND COALESCE(`PointsAttempted`, 0) >= 0 AND COALESCE(`PointsSkippedNotExpired`, 0) >= 0"
                + " AND COALESCE(`PointsFailed`, 0) >= 0 AND COALESCE(`ForecastsStored`, 0) >= 0"
                + " AND COALESCE(`ObservationDaysStored`, 0) >= 0 AND COALESCE(`FilesErased`, 0) >= 0"
                + " AND COALESCE(`AbandonedWritesSwept`, 0) >= 0 AND COALESCE(`ObjectsScrubbed`, 0) >= 0"
                + " AND COALESCE(`IntegrityFailures`, 0) >= 0 AND COALESCE(`OrphansFound`, 0) >= 0"
                + " AND COALESCE(`PhotosRendered`, 0) >= 0 AND COALESCE(`PhotosFailed`, 0) >= 0"
                + " AND COALESCE(`PhotosRequeued`, 0) >= 0 AND COALESCE(`Crashes`, 0) >= 0 AND COALESCE(`Hangs`, 0) >= 0"
                + " AND COALESCE(`PhotosMoved`, 0) >= 0 AND COALESCE(`PhotosCopiedShared`, 0) >= 0"
                + " AND COALESCE(`PhotosParked`, 0) >= 0 AND COALESCE(`Interruptions`, 0) >= 0"
                + " AND COALESCE(`AnalysesCompleted`, 0) >= 0 AND COALESCE(`AnalysesFailed`, 0) >= 0"
                + " AND COALESCE(`AnalysesRefused`, 0) >= 0"
                + " AND COALESCE(`FinanceFilesMoved`, 0) >= 0 AND COALESCE(`JournalFilesMoved`, 0) >= 0"
                + " AND COALESCE(`ContactAvatarsMoved`, 0) >= 0 AND COALESCE(`FilesParked`, 0) >= 0"
                + " AND COALESCE(`InstrumentsAttempted`, 0) >= 0 AND COALESCE(`InstrumentsSucceeded`, 0) >= 0"
                + " AND COALESCE(`InstrumentsFailed`, 0) >= 0 AND COALESCE(`PricesInserted`, 0) >= 0"
                + " AND COALESCE(`FallbacksUsed`, 0) >= 0 AND COALESCE(`RequestsMade`, 0) >= 0"
                + " AND COALESCE(`PricesDeleted`, 0) >= 0");
            // FinishedAtUtc is NULL only on a Running row or an open held period, and always NULL on a Running one.
            tb.HasCheckConstraint(
                "CK_JobRuns_FinishedMatchesStatus",
                "(`Status` = 0 AND `FinishedAtUtc` IS NULL) OR `Status` = 5 OR (`Status` IN (1, 2, 3, 4) AND `FinishedAtUtc` IS NOT NULL)");
            tb.HasCheckConstraint(
                "CK_JobRuns_HeldHasWorkerAndReason", "`Status` <> 5 OR (`WorkerId` IS NOT NULL AND `HeldReason` IS NOT NULL)");
            tb.HasCheckConstraint("CK_JobRuns_HeldReasonOnlyOnHeld", "`HeldReason` IS NULL OR `Status` = 5");
            // The exchange-rate sync's own CHECKs, carried over; NULL on every other job's rows.
            tb.HasCheckConstraint("CK_JobRuns_Provider", "`Provider` IS NULL OR `Provider` IN (1)");
            tb.HasCheckConstraint("CK_JobRuns_ErrorKind", "`ErrorKind` IS NULL OR `ErrorKind` BETWEEN 1 AND 9");
        });
    }
}

internal sealed class FileBlobBackfillJobRunConfiguration : IEntityTypeConfiguration<FileBlobBackfillJobRun>
{
    public void Configure(EntityTypeBuilder<FileBlobBackfillJobRun> entity) =>
        entity.Property(run => run.Interruptions).HasColumnName(nameof(FileBlobBackfillJobRun.Interruptions));
}

internal sealed class PhotoOriginalBackfillJobRunConfiguration : IEntityTypeConfiguration<PhotoOriginalBackfillJobRun>
{
    public void Configure(EntityTypeBuilder<PhotoOriginalBackfillJobRun> entity) =>
        entity.Property(run => run.Interruptions).HasColumnName(nameof(PhotoOriginalBackfillJobRun.Interruptions));
}

internal sealed class JobHeartbeatConfiguration : IEntityTypeConfiguration<JobHeartbeat>
{
    public void Configure(EntityTypeBuilder<JobHeartbeat> entity)
    {
        entity.ToTable(tb => tb.HasCheckConstraint("CK_JobHeartbeats_JobType", "`JobType` IN (1, 2, 3, 4, 5, 6, 7)"));
    }
}
