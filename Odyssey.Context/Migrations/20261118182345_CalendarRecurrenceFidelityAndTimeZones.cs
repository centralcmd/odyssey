using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Odyssey.Context.Migrations
{
    /// <summary>
    /// Issue #519: occurrence keys, overrides and exclusions, and time zones on events and series. Additive. Every
    /// existing row stays floating (a null zone), exactly as it displays today, and every existing series stays un-keyed
    /// until the backfill job or its first write keys it. <c>Down()</c> drops the table, the columns and the checks:
    /// exclusions, override flags, keys and zones are lost, and rows the backfill detached stay detached — undoing that
    /// needs a backup.
    /// </summary>
    public partial class CalendarRecurrenceFidelityAndTimeZones : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_JobRuns_CountersNonNegative",
                table: "JobRuns");

            migrationBuilder.DropCheckConstraint(
                name: "CK_JobRuns_JobType",
                table: "JobRuns");

            migrationBuilder.DropCheckConstraint(
                name: "CK_JobHeartbeats_JobType",
                table: "JobHeartbeats");

            migrationBuilder.AddColumn<DateTime>(
                name: "KeysBackfilledAt",
                table: "RecurrencePatterns",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TimeZoneId",
                table: "RecurrencePatterns",
                type: "varchar(64)",
                maxLength: 64,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<Guid>(
                name: "Version",
                table: "RecurrencePatterns",
                type: "char(36)",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                collation: "ascii_general_ci");

            // Every existing series gets a version of its own (issue #519 §4), so no two share the all-zero token a
            // client could otherwise send as expectedVersion for any of them. KeysBackfilledAt stays NULL: the worker's
            // RecurrenceKeyBackfillJob keys these series, or their first write does.
            migrationBuilder.Sql("UPDATE `RecurrencePatterns` SET `Version` = UUID();");

            migrationBuilder.AddColumn<int>(
                name: "KeyRowsDetached",
                table: "JobRuns",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SeriesKeyFailures",
                table: "JobRuns",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SeriesKeyed",
                table: "JobRuns",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsOverride",
                table: "CalendarEvents",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "OccurrenceKey",
                table: "CalendarEvents",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TimeZoneId",
                table: "CalendarEvents",
                type: "varchar(64)",
                maxLength: 64,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "RecurrenceExclusions",
                columns: table => new
                {
                    RecurrenceExclusionId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    RecurrencePatternId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    OccurrenceKey = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    CreatedByUserId = table.Column<string>(type: "varchar(255)", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecurrenceExclusions", x => x.RecurrenceExclusionId);
                    table.ForeignKey(
                        name: "FK_RecurrenceExclusions_AspNetUsers_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_RecurrenceExclusions_RecurrencePatterns_RecurrencePatternId",
                        column: x => x.RecurrencePatternId,
                        principalTable: "RecurrencePatterns",
                        principalColumn: "RecurrencePatternId",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddCheckConstraint(
                name: "CK_RecurrencePatterns_AllDayHasNoZone",
                table: "RecurrencePatterns",
                sql: "`IsAllDay` = 0 OR `TimeZoneId` IS NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_JobRuns_CountersNonNegative",
                table: "JobRuns",
                sql: "COALESCE(`ItemsProcessed`, 0) >= 0 AND COALESCE(`ItemsFailed`, 0) >= 0 AND COALESCE(`DurationMs`, 0) >= 0 AND COALESCE(`RowsInserted`, 0) >= 0 AND COALESCE(`RowsSkipped`, 0) >= 0 AND COALESCE(`RowsRejected`, 0) >= 0 AND COALESCE(`RowsDeleted`, 0) >= 0 AND COALESCE(`PointsAttempted`, 0) >= 0 AND COALESCE(`PointsSkippedNotExpired`, 0) >= 0 AND COALESCE(`PointsFailed`, 0) >= 0 AND COALESCE(`ForecastsStored`, 0) >= 0 AND COALESCE(`ObservationDaysStored`, 0) >= 0 AND COALESCE(`FilesErased`, 0) >= 0 AND COALESCE(`AbandonedWritesSwept`, 0) >= 0 AND COALESCE(`ObjectsScrubbed`, 0) >= 0 AND COALESCE(`IntegrityFailures`, 0) >= 0 AND COALESCE(`OrphansFound`, 0) >= 0 AND COALESCE(`PhotosRendered`, 0) >= 0 AND COALESCE(`PhotosFailed`, 0) >= 0 AND COALESCE(`PhotosRequeued`, 0) >= 0 AND COALESCE(`Crashes`, 0) >= 0 AND COALESCE(`Hangs`, 0) >= 0 AND COALESCE(`PhotosMoved`, 0) >= 0 AND COALESCE(`PhotosCopiedShared`, 0) >= 0 AND COALESCE(`PhotosParked`, 0) >= 0 AND COALESCE(`Interruptions`, 0) >= 0 AND COALESCE(`AnalysesCompleted`, 0) >= 0 AND COALESCE(`AnalysesFailed`, 0) >= 0 AND COALESCE(`AnalysesRefused`, 0) >= 0 AND COALESCE(`FinanceFilesMoved`, 0) >= 0 AND COALESCE(`JournalFilesMoved`, 0) >= 0 AND COALESCE(`ContactAvatarsMoved`, 0) >= 0 AND COALESCE(`FilesParked`, 0) >= 0 AND COALESCE(`InstrumentsAttempted`, 0) >= 0 AND COALESCE(`InstrumentsSucceeded`, 0) >= 0 AND COALESCE(`InstrumentsFailed`, 0) >= 0 AND COALESCE(`PricesInserted`, 0) >= 0 AND COALESCE(`FallbacksUsed`, 0) >= 0 AND COALESCE(`RequestsMade`, 0) >= 0 AND COALESCE(`PricesDeleted`, 0) >= 0 AND COALESCE(`FeedsFetched`, 0) >= 0 AND COALESCE(`ItemsStored`, 0) >= 0 AND COALESCE(`ItemsRevised`, 0) >= 0 AND COALESCE(`ImagesStored`, 0) >= 0 AND COALESCE(`FeedsQuarantined`, 0) >= 0 AND COALESCE(`FeedsMarked`, 0) >= 0 AND COALESCE(`SeriesKeyed`, 0) >= 0 AND COALESCE(`SeriesKeyFailures`, 0) >= 0 AND COALESCE(`KeyRowsDetached`, 0) >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_JobRuns_JobType",
                table: "JobRuns",
                sql: "`JobType` IN (1, 2, 3, 4, 5, 6, 7, 8, 9, 10)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_JobHeartbeats_JobType",
                table: "JobHeartbeats",
                sql: "`JobType` IN (1, 2, 3, 4, 5, 6, 7, 8, 9, 10)");

            migrationBuilder.CreateIndex(
                name: "IX_CalendarEvents_RecurrencePatternId_OccurrenceKey",
                table: "CalendarEvents",
                columns: new[] { "RecurrencePatternId", "OccurrenceKey" },
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_CalendarEvents_AllDayHasNoZone",
                table: "CalendarEvents",
                sql: "`IsAllDay` = 0 OR `TimeZoneId` IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_RecurrenceExclusions_CreatedByUserId",
                table: "RecurrenceExclusions",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_RecurrenceExclusions_RecurrencePatternId_OccurrenceKey",
                table: "RecurrenceExclusions",
                columns: new[] { "RecurrencePatternId", "OccurrenceKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RecurrenceExclusions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_RecurrencePatterns_AllDayHasNoZone",
                table: "RecurrencePatterns");

            migrationBuilder.DropCheckConstraint(
                name: "CK_JobRuns_CountersNonNegative",
                table: "JobRuns");

            migrationBuilder.DropCheckConstraint(
                name: "CK_JobRuns_JobType",
                table: "JobRuns");

            migrationBuilder.DropCheckConstraint(
                name: "CK_JobHeartbeats_JobType",
                table: "JobHeartbeats");

            migrationBuilder.DropIndex(
                name: "IX_CalendarEvents_RecurrencePatternId_OccurrenceKey",
                table: "CalendarEvents");

            migrationBuilder.DropCheckConstraint(
                name: "CK_CalendarEvents_AllDayHasNoZone",
                table: "CalendarEvents");

            migrationBuilder.DropColumn(
                name: "KeysBackfilledAt",
                table: "RecurrencePatterns");

            migrationBuilder.DropColumn(
                name: "TimeZoneId",
                table: "RecurrencePatterns");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "RecurrencePatterns");

            migrationBuilder.DropColumn(
                name: "KeyRowsDetached",
                table: "JobRuns");

            migrationBuilder.DropColumn(
                name: "SeriesKeyFailures",
                table: "JobRuns");

            migrationBuilder.DropColumn(
                name: "SeriesKeyed",
                table: "JobRuns");

            migrationBuilder.DropColumn(
                name: "IsOverride",
                table: "CalendarEvents");

            migrationBuilder.DropColumn(
                name: "OccurrenceKey",
                table: "CalendarEvents");

            migrationBuilder.DropColumn(
                name: "TimeZoneId",
                table: "CalendarEvents");

            migrationBuilder.AddCheckConstraint(
                name: "CK_JobRuns_CountersNonNegative",
                table: "JobRuns",
                sql: "COALESCE(`ItemsProcessed`, 0) >= 0 AND COALESCE(`ItemsFailed`, 0) >= 0 AND COALESCE(`DurationMs`, 0) >= 0 AND COALESCE(`RowsInserted`, 0) >= 0 AND COALESCE(`RowsSkipped`, 0) >= 0 AND COALESCE(`RowsRejected`, 0) >= 0 AND COALESCE(`RowsDeleted`, 0) >= 0 AND COALESCE(`PointsAttempted`, 0) >= 0 AND COALESCE(`PointsSkippedNotExpired`, 0) >= 0 AND COALESCE(`PointsFailed`, 0) >= 0 AND COALESCE(`ForecastsStored`, 0) >= 0 AND COALESCE(`ObservationDaysStored`, 0) >= 0 AND COALESCE(`FilesErased`, 0) >= 0 AND COALESCE(`AbandonedWritesSwept`, 0) >= 0 AND COALESCE(`ObjectsScrubbed`, 0) >= 0 AND COALESCE(`IntegrityFailures`, 0) >= 0 AND COALESCE(`OrphansFound`, 0) >= 0 AND COALESCE(`PhotosRendered`, 0) >= 0 AND COALESCE(`PhotosFailed`, 0) >= 0 AND COALESCE(`PhotosRequeued`, 0) >= 0 AND COALESCE(`Crashes`, 0) >= 0 AND COALESCE(`Hangs`, 0) >= 0 AND COALESCE(`PhotosMoved`, 0) >= 0 AND COALESCE(`PhotosCopiedShared`, 0) >= 0 AND COALESCE(`PhotosParked`, 0) >= 0 AND COALESCE(`Interruptions`, 0) >= 0 AND COALESCE(`AnalysesCompleted`, 0) >= 0 AND COALESCE(`AnalysesFailed`, 0) >= 0 AND COALESCE(`AnalysesRefused`, 0) >= 0 AND COALESCE(`FinanceFilesMoved`, 0) >= 0 AND COALESCE(`JournalFilesMoved`, 0) >= 0 AND COALESCE(`ContactAvatarsMoved`, 0) >= 0 AND COALESCE(`FilesParked`, 0) >= 0 AND COALESCE(`InstrumentsAttempted`, 0) >= 0 AND COALESCE(`InstrumentsSucceeded`, 0) >= 0 AND COALESCE(`InstrumentsFailed`, 0) >= 0 AND COALESCE(`PricesInserted`, 0) >= 0 AND COALESCE(`FallbacksUsed`, 0) >= 0 AND COALESCE(`RequestsMade`, 0) >= 0 AND COALESCE(`PricesDeleted`, 0) >= 0 AND COALESCE(`FeedsFetched`, 0) >= 0 AND COALESCE(`ItemsStored`, 0) >= 0 AND COALESCE(`ItemsRevised`, 0) >= 0 AND COALESCE(`ImagesStored`, 0) >= 0 AND COALESCE(`FeedsQuarantined`, 0) >= 0 AND COALESCE(`FeedsMarked`, 0) >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_JobRuns_JobType",
                table: "JobRuns",
                sql: "`JobType` IN (1, 2, 3, 4, 5, 6, 7, 8, 9)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_JobHeartbeats_JobType",
                table: "JobHeartbeats",
                sql: "`JobType` IN (1, 2, 3, 4, 5, 6, 7, 8, 9)");
        }
    }
}
