using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Odyssey.Context.Migrations
{
    /// <summary>
    /// The shared background-job run log (issue #409 §11, migration 1 of 2): <c>JobRuns</c>, <c>JobHeartbeats</c>,
    /// <c>PhotoThumbnailJobs.ManualRequeues</c> and the <c>JobRunRetentionDays</c> setting, and the exchange-rate sync's
    /// run log copied across with its ids. <c>ExchangeRateSyncRuns</c> is dropped by the NEXT migration, after a
    /// parity check, because MariaDB DDL is not atomic (issue #468): an interruption here must leave the source intact.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Order.</b> The legacy rows are validated against every <c>JobRuns</c> constraint FIRST, before any DDL, so a
    /// row that cannot be copied aborts the migration naming its id with nothing created. The copy is
    /// <c>INSERT … SELECT … WHERE NOT EXISTS</c> on the id — idempotent on a replay and, unlike <c>INSERT IGNORE</c>,
    /// never silently skipping a bad row — listing its columns and omitting the two generated ones.
    /// </para>
    /// <para>
    /// <b><c>Down()</c> restores the schema only.</b> It drops <c>JobRuns</c>; the legacy table still holds every row
    /// until the next migration, so rolling back this one alone loses nothing, but once both have run the only way back
    /// to the old rows is a backup.
    /// </para>
    /// </remarks>
    public partial class AddJobRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ── 1. Pre-validation (A-N5): every legacy row must satisfy every JobRuns constraint ────────────────
            // SIGNAL is only valid in a compound statement outside a stored program, and its MESSAGE_TEXT takes a
            // simple value — hence the local variable. Each check names the first offending id.
            migrationBuilder.Sql("""
                BEGIN NOT ATOMIC
                    DECLARE bad CHAR(36) DEFAULT NULL;
                    DECLARE running INT DEFAULT 0;
                    DECLARE failure TEXT DEFAULT NULL;
                    SELECT `ExchangeRateSyncRunId` INTO bad FROM `ExchangeRateSyncRuns`
                    WHERE `ExchangeRateSyncRunId` IS NULL OR `StartedAtUtc` IS NULL
                       OR `Trigger` IS NULL OR `Trigger` NOT IN (0, 1, 2)
                       OR `Status` IS NULL OR `Status` NOT IN (0, 1, 2, 3, 4)
                       OR (`ErrorKind` IS NOT NULL AND `ErrorKind` NOT BETWEEN 1 AND 9)
                       OR (`Provider` IS NOT NULL AND `Provider` NOT IN (1))
                       OR (`Status` = 0 AND `FinishedAtUtc` IS NOT NULL)
                       OR (`Status` IN (1, 2, 3, 4) AND `FinishedAtUtc` IS NULL)
                       OR `RowsInserted` < 0 OR `RowsSkipped` < 0 OR `RowsRejected` < 0 OR `RowsDeleted` < 0
                       OR CHAR_LENGTH(`CurrencyCodes`) > 256
                       OR CHAR_LENGTH(`RequestedByUserId`) > 255
                    ORDER BY `StartedAtUtc`, `ExchangeRateSyncRunId`
                    LIMIT 1;
                    IF bad IS NOT NULL THEN
                        SET failure = CONCAT('AddJobRuns: exchange-rate sync run ', bad,
                            ' violates a JobRuns constraint and cannot be copied; repair or remove it, then re-run.');
                        SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = failure;
                    END IF;
                    SELECT COUNT(*) INTO running FROM `ExchangeRateSyncRuns` WHERE `Status` = 0;
                    IF running > 1 THEN
                        SIGNAL SQLSTATE '45000'
                            SET MESSAGE_TEXT = 'AddJobRuns: more than one exchange-rate sync run is Running; the exclusive guard would refuse the copy.';
                    END IF;
                END
                """);

            migrationBuilder.AddColumn<byte>(
                name: "ManualRequeues",
                table: "PhotoThumbnailJobs",
                type: "tinyint unsigned",
                nullable: false,
                defaultValue: (byte)0);

            migrationBuilder.CreateTable(
                name: "JobHeartbeats",
                columns: table => new
                {
                    JobType = table.Column<byte>(type: "tinyint unsigned", nullable: false),
                    WorkerId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    LastTickAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobHeartbeats", x => new { x.JobType, x.WorkerId });
                    table.CheckConstraint("CK_JobHeartbeats_JobType", "`JobType` IN (1, 2, 3, 4)");
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "JobRuns",
                columns: table => new
                {
                    JobRunId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    JobType = table.Column<byte>(type: "tinyint unsigned", nullable: false),
                    Trigger = table.Column<byte>(type: "tinyint unsigned", nullable: false),
                    Status = table.Column<byte>(type: "tinyint unsigned", nullable: false),
                    FailureCategory = table.Column<byte>(type: "tinyint unsigned", nullable: true),
                    HeldReason = table.Column<byte>(type: "tinyint unsigned", nullable: true),
                    StartedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    FinishedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    DurationMs = table.Column<long>(type: "bigint", nullable: true),
                    ClaimedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    LastActivityAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    Generation = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    WorkerId = table.Column<Guid>(type: "char(36)", nullable: true, collation: "ascii_general_ci"),
                    RequestedByUserId = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ItemsProcessed = table.Column<int>(type: "int", nullable: true),
                    ItemsFailed = table.Column<int>(type: "int", nullable: true),
                    OpenHeldGuard = table.Column<int>(type: "int", nullable: true, computedColumnSql: "IF(`Status` = 5 AND `FinishedAtUtc` IS NULL, 1, NULL)", stored: false),
                    ExclusiveGuard = table.Column<int>(type: "int", nullable: true, computedColumnSql: "IF(`Status` = 0 AND (`JobType` IN (1, 3) OR `Trigger` = 1), `JobType`, NULL)", stored: false),
                    Provider = table.Column<int>(type: "int", nullable: true),
                    ErrorKind = table.Column<int>(type: "int", nullable: true),
                    SpanFrom = table.Column<DateOnly>(type: "date", nullable: true),
                    SpanTo = table.Column<DateOnly>(type: "date", nullable: true),
                    CurrencyCodes = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    RowsInserted = table.Column<int>(type: "int", nullable: true),
                    RowsSkipped = table.Column<int>(type: "int", nullable: true),
                    RowsRejected = table.Column<int>(type: "int", nullable: true),
                    RowsDeleted = table.Column<int>(type: "int", nullable: true),
                    FilesErased = table.Column<int>(type: "int", nullable: true),
                    AbandonedWritesSwept = table.Column<int>(type: "int", nullable: true),
                    ObjectsScrubbed = table.Column<int>(type: "int", nullable: true),
                    IntegrityFailures = table.Column<int>(type: "int", nullable: true),
                    OrphansFound = table.Column<int>(type: "int", nullable: true),
                    HeldByCheck = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    PhotosRendered = table.Column<int>(type: "int", nullable: true),
                    PhotosFailed = table.Column<int>(type: "int", nullable: true),
                    PhotosRequeued = table.Column<int>(type: "int", nullable: true),
                    Crashes = table.Column<int>(type: "int", nullable: true),
                    Hangs = table.Column<int>(type: "int", nullable: true),
                    PointsAttempted = table.Column<int>(type: "int", nullable: true),
                    PointsSkippedNotExpired = table.Column<int>(type: "int", nullable: true),
                    PointsFailed = table.Column<int>(type: "int", nullable: true),
                    ForecastsStored = table.Column<int>(type: "int", nullable: true),
                    ObservationDaysStored = table.Column<int>(type: "int", nullable: true),
                    FailureKind = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobRuns", x => x.JobRunId);
                    table.CheckConstraint("CK_JobRuns_CountersNonNegative", "COALESCE(`ItemsProcessed`, 0) >= 0 AND COALESCE(`ItemsFailed`, 0) >= 0 AND COALESCE(`DurationMs`, 0) >= 0 AND COALESCE(`RowsInserted`, 0) >= 0 AND COALESCE(`RowsSkipped`, 0) >= 0 AND COALESCE(`RowsRejected`, 0) >= 0 AND COALESCE(`RowsDeleted`, 0) >= 0 AND COALESCE(`PointsAttempted`, 0) >= 0 AND COALESCE(`PointsSkippedNotExpired`, 0) >= 0 AND COALESCE(`PointsFailed`, 0) >= 0 AND COALESCE(`ForecastsStored`, 0) >= 0 AND COALESCE(`ObservationDaysStored`, 0) >= 0 AND COALESCE(`FilesErased`, 0) >= 0 AND COALESCE(`AbandonedWritesSwept`, 0) >= 0 AND COALESCE(`ObjectsScrubbed`, 0) >= 0 AND COALESCE(`IntegrityFailures`, 0) >= 0 AND COALESCE(`OrphansFound`, 0) >= 0 AND COALESCE(`PhotosRendered`, 0) >= 0 AND COALESCE(`PhotosFailed`, 0) >= 0 AND COALESCE(`PhotosRequeued`, 0) >= 0 AND COALESCE(`Crashes`, 0) >= 0 AND COALESCE(`Hangs`, 0) >= 0");
                    table.CheckConstraint("CK_JobRuns_ErrorKind", "`ErrorKind` IS NULL OR `ErrorKind` BETWEEN 1 AND 9");
                    table.CheckConstraint("CK_JobRuns_FailureCategory", "`FailureCategory` IS NULL OR `FailureCategory` BETWEEN 1 AND 10");
                    table.CheckConstraint("CK_JobRuns_FinishedMatchesStatus", "(`Status` = 0 AND `FinishedAtUtc` IS NULL) OR `Status` = 5 OR (`Status` IN (1, 2, 3, 4) AND `FinishedAtUtc` IS NOT NULL)");
                    table.CheckConstraint("CK_JobRuns_HeldHasWorkerAndReason", "`Status` <> 5 OR (`WorkerId` IS NOT NULL AND `HeldReason` IS NOT NULL)");
                    table.CheckConstraint("CK_JobRuns_HeldReason", "`HeldReason` IS NULL OR `HeldReason` BETWEEN 1 AND 5");
                    table.CheckConstraint("CK_JobRuns_HeldReasonOnlyOnHeld", "`HeldReason` IS NULL OR `Status` = 5");
                    table.CheckConstraint("CK_JobRuns_JobType", "`JobType` IN (1, 2, 3, 4)");
                    table.CheckConstraint("CK_JobRuns_Provider", "`Provider` IS NULL OR `Provider` IN (1)");
                    table.CheckConstraint("CK_JobRuns_Status", "`Status` IN (0, 1, 2, 3, 4, 5)");
                    table.CheckConstraint("CK_JobRuns_Trigger", "`Trigger` IN (0, 1, 2)");
                    table.ForeignKey(
                        name: "FK_JobRuns_AspNetUsers_RequestedByUserId",
                        column: x => x.RequestedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.InsertData(
                table: "SystemSettings",
                columns: new[] { "Key", "UpdatedAt", "UpdatedBy", "Value" },
                values: new object[] { "JobRunRetentionDays", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "90" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_PhotoThumbnailJobs_ManualRequeuesRange",
                table: "PhotoThumbnailJobs",
                sql: "`ManualRequeues` IN (0, 1)");

            migrationBuilder.CreateIndex(
                name: "IX_JobRuns_DurationMs",
                table: "JobRuns",
                column: "DurationMs");

            migrationBuilder.CreateIndex(
                name: "IX_JobRuns_ExclusiveGuard",
                table: "JobRuns",
                column: "ExclusiveGuard",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_JobRuns_JobType_StartedAtUtc",
                table: "JobRuns",
                columns: new[] { "JobType", "StartedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_JobRuns_JobType_WorkerId_OpenHeldGuard",
                table: "JobRuns",
                columns: new[] { "JobType", "WorkerId", "OpenHeldGuard" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_JobRuns_RequestedByUserId",
                table: "JobRuns",
                column: "RequestedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_JobRuns_StartedAtUtc",
                table: "JobRuns",
                column: "StartedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_JobRuns_Status_StartedAtUtc",
                table: "JobRuns",
                columns: new[] { "Status", "StartedAtUtc" });

            // ── 2. The copy (A17, A-N5, S-N4) ───────────────────────────────────────────────────────────────────
            // FailureCategory = ErrorKind: the ordinals are equal by design (§4.1.1). A fresh Generation per row, the
            // stale clock at the run's last known instant, the duration computed, and the headline counts written only
            // on a finished run — exactly what JobRun.Finish would have written. WorkerId stays NULL: the process that
            // ran a legacy run is not known, so a legacy Running row is left to the stale-run sweep.
            migrationBuilder.Sql("""
                INSERT INTO `JobRuns`
                    (`JobRunId`, `JobType`, `Trigger`, `Status`, `FailureCategory`, `HeldReason`,
                     `StartedAtUtc`, `FinishedAtUtc`, `DurationMs`, `ClaimedAtUtc`, `LastActivityAtUtc`,
                     `Generation`, `WorkerId`, `RequestedByUserId`, `ItemsProcessed`, `ItemsFailed`,
                     `Provider`, `ErrorKind`, `SpanFrom`, `SpanTo`, `CurrencyCodes`,
                     `RowsInserted`, `RowsSkipped`, `RowsRejected`, `RowsDeleted`)
                SELECT s.`ExchangeRateSyncRunId`, 1, s.`Trigger`, s.`Status`, s.`ErrorKind`, NULL,
                       s.`StartedAtUtc`, s.`FinishedAtUtc`,
                       CASE WHEN s.`FinishedAtUtc` IS NULL THEN NULL
                            ELSE GREATEST(0, TIMESTAMPDIFF(MICROSECOND, s.`StartedAtUtc`, s.`FinishedAtUtc`) DIV 1000) END,
                       s.`ClaimedAtUtc`, COALESCE(s.`FinishedAtUtc`, s.`StartedAtUtc`),
                       UUID(), NULL, s.`RequestedByUserId`,
                       CASE WHEN s.`FinishedAtUtc` IS NULL THEN NULL ELSE s.`RowsInserted` + s.`RowsDeleted` END,
                       CASE WHEN s.`FinishedAtUtc` IS NULL THEN NULL ELSE s.`RowsRejected` END,
                       s.`Provider`, s.`ErrorKind`, s.`SpanFrom`, s.`SpanTo`, s.`CurrencyCodes`,
                       s.`RowsInserted`, s.`RowsSkipped`, s.`RowsRejected`, s.`RowsDeleted`
                FROM `ExchangeRateSyncRuns` s
                WHERE NOT EXISTS (SELECT 1 FROM `JobRuns` j WHERE j.`JobRunId` = s.`ExchangeRateSyncRunId`);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "JobHeartbeats");

            migrationBuilder.DropTable(
                name: "JobRuns");

            migrationBuilder.DropCheckConstraint(
                name: "CK_PhotoThumbnailJobs_ManualRequeuesRange",
                table: "PhotoThumbnailJobs");

            migrationBuilder.DeleteData(
                table: "SystemSettings",
                keyColumn: "Key",
                keyValue: "JobRunRetentionDays");

            migrationBuilder.DropColumn(
                name: "ManualRequeues",
                table: "PhotoThumbnailJobs");
        }
    }
}
