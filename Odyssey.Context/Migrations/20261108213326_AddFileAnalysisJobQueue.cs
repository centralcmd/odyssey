using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Odyssey.Context.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// The file-analysis extraction queue (issue #444): the queue columns on FileAnalysisJobs, the composite indexes
    /// that replace its single-column AccountFileId and Status ones, the FileAnalysis job type (6) on the shared run
    /// log — both JobType CHECKs widened, three counter columns — and nothing seeded. Additive: no row is rewritten,
    /// and a legacy Running row is failed by the worker's sweep, not here.
    /// </summary>
    public partial class AddFileAnalysisJobQueue : Migration
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


            migrationBuilder.DropIndex(
                name: "IX_FileAnalysisJobs_Status",
                table: "FileAnalysisJobs");

            migrationBuilder.AddColumn<int>(
                name: "AnalysesCompleted",
                table: "JobRuns",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AnalysesFailed",
                table: "JobRuns",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AnalysesRefused",
                table: "JobRuns",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AnalyzerBaseUrlOrigin",
                table: "FileAnalysisJobs",
                type: "varchar(256)",
                maxLength: 256,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<DateTime>(
                name: "ClaimedAt",
                table: "FileAnalysisJobs",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ExtractionGeneration",
                table: "FileAnalysisJobs",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "FileSha256",
                table: "FileAnalysisJobs",
                type: "varchar(64)",
                maxLength: 64,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<DateTime>(
                name: "LeaseUntilUtc",
                table: "FileAnalysisJobs",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PromptTemplateHash",
                table: "FileAnalysisJobs",
                type: "varchar(64)",
                maxLength: 64,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<DateTime>(
                name: "QueuedAt",
                table: "FileAnalysisJobs",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "RunId",
                table: "FileAnalysisJobs",
                type: "char(36)",
                nullable: true,
                collation: "ascii_general_ci");

            migrationBuilder.AddColumn<DateTime>(
                name: "TransferStartedAt",
                table: "FileAnalysisJobs",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_JobRuns_CountersNonNegative",
                table: "JobRuns",
                sql: "COALESCE(`ItemsProcessed`, 0) >= 0 AND COALESCE(`ItemsFailed`, 0) >= 0 AND COALESCE(`DurationMs`, 0) >= 0 AND COALESCE(`RowsInserted`, 0) >= 0 AND COALESCE(`RowsSkipped`, 0) >= 0 AND COALESCE(`RowsRejected`, 0) >= 0 AND COALESCE(`RowsDeleted`, 0) >= 0 AND COALESCE(`PointsAttempted`, 0) >= 0 AND COALESCE(`PointsSkippedNotExpired`, 0) >= 0 AND COALESCE(`PointsFailed`, 0) >= 0 AND COALESCE(`ForecastsStored`, 0) >= 0 AND COALESCE(`ObservationDaysStored`, 0) >= 0 AND COALESCE(`FilesErased`, 0) >= 0 AND COALESCE(`AbandonedWritesSwept`, 0) >= 0 AND COALESCE(`ObjectsScrubbed`, 0) >= 0 AND COALESCE(`IntegrityFailures`, 0) >= 0 AND COALESCE(`OrphansFound`, 0) >= 0 AND COALESCE(`PhotosRendered`, 0) >= 0 AND COALESCE(`PhotosFailed`, 0) >= 0 AND COALESCE(`PhotosRequeued`, 0) >= 0 AND COALESCE(`Crashes`, 0) >= 0 AND COALESCE(`Hangs`, 0) >= 0 AND COALESCE(`PhotosMoved`, 0) >= 0 AND COALESCE(`PhotosCopiedShared`, 0) >= 0 AND COALESCE(`PhotosParked`, 0) >= 0 AND COALESCE(`Interruptions`, 0) >= 0 AND COALESCE(`AnalysesCompleted`, 0) >= 0 AND COALESCE(`AnalysesFailed`, 0) >= 0 AND COALESCE(`AnalysesRefused`, 0) >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_JobRuns_JobType",
                table: "JobRuns",
                sql: "`JobType` IN (1, 2, 3, 4, 5, 6)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_JobHeartbeats_JobType",
                table: "JobHeartbeats",
                sql: "`JobType` IN (1, 2, 3, 4, 5, 6)");

            migrationBuilder.CreateIndex(
                name: "IX_FileAnalysisJobs_AccountFileId_Status",
                table: "FileAnalysisJobs",
                columns: new[] { "AccountFileId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_FileAnalysisJobs_Status_QueuedAt",
                table: "FileAnalysisJobs",
                columns: new[] { "Status", "QueuedAt" });

            // Dropped LAST, after the composite that leads with AccountFileId exists: MariaDB refuses to drop the
            // only index a foreign key uses (error 1553), so the key must already have its replacement.
            migrationBuilder.DropIndex(
                name: "IX_FileAnalysisJobs_AccountFileId",
                table: "FileAnalysisJobs");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The narrowed JobType CHECKs re-added below would refuse the queue's run-log rows, so they go first.
            // Run-log rows are history, not data anyone restores from; the analysis jobs themselves are kept.
            migrationBuilder.Sql("DELETE FROM `JobRuns` WHERE `JobType` = 6;");
            migrationBuilder.Sql("DELETE FROM `JobHeartbeats` WHERE `JobType` = 6;");

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
                name: "IX_FileAnalysisJobs_Status_QueuedAt",
                table: "FileAnalysisJobs");

            migrationBuilder.DropColumn(
                name: "AnalysesCompleted",
                table: "JobRuns");

            migrationBuilder.DropColumn(
                name: "AnalysesFailed",
                table: "JobRuns");

            migrationBuilder.DropColumn(
                name: "AnalysesRefused",
                table: "JobRuns");

            migrationBuilder.DropColumn(
                name: "AnalyzerBaseUrlOrigin",
                table: "FileAnalysisJobs");

            migrationBuilder.DropColumn(
                name: "ClaimedAt",
                table: "FileAnalysisJobs");

            migrationBuilder.DropColumn(
                name: "ExtractionGeneration",
                table: "FileAnalysisJobs");

            migrationBuilder.DropColumn(
                name: "FileSha256",
                table: "FileAnalysisJobs");

            migrationBuilder.DropColumn(
                name: "LeaseUntilUtc",
                table: "FileAnalysisJobs");

            migrationBuilder.DropColumn(
                name: "PromptTemplateHash",
                table: "FileAnalysisJobs");

            migrationBuilder.DropColumn(
                name: "QueuedAt",
                table: "FileAnalysisJobs");

            migrationBuilder.DropColumn(
                name: "RunId",
                table: "FileAnalysisJobs");

            migrationBuilder.DropColumn(
                name: "TransferStartedAt",
                table: "FileAnalysisJobs");

            migrationBuilder.AddCheckConstraint(
                name: "CK_JobRuns_CountersNonNegative",
                table: "JobRuns",
                sql: "COALESCE(`ItemsProcessed`, 0) >= 0 AND COALESCE(`ItemsFailed`, 0) >= 0 AND COALESCE(`DurationMs`, 0) >= 0 AND COALESCE(`RowsInserted`, 0) >= 0 AND COALESCE(`RowsSkipped`, 0) >= 0 AND COALESCE(`RowsRejected`, 0) >= 0 AND COALESCE(`RowsDeleted`, 0) >= 0 AND COALESCE(`PointsAttempted`, 0) >= 0 AND COALESCE(`PointsSkippedNotExpired`, 0) >= 0 AND COALESCE(`PointsFailed`, 0) >= 0 AND COALESCE(`ForecastsStored`, 0) >= 0 AND COALESCE(`ObservationDaysStored`, 0) >= 0 AND COALESCE(`FilesErased`, 0) >= 0 AND COALESCE(`AbandonedWritesSwept`, 0) >= 0 AND COALESCE(`ObjectsScrubbed`, 0) >= 0 AND COALESCE(`IntegrityFailures`, 0) >= 0 AND COALESCE(`OrphansFound`, 0) >= 0 AND COALESCE(`PhotosRendered`, 0) >= 0 AND COALESCE(`PhotosFailed`, 0) >= 0 AND COALESCE(`PhotosRequeued`, 0) >= 0 AND COALESCE(`Crashes`, 0) >= 0 AND COALESCE(`Hangs`, 0) >= 0 AND COALESCE(`PhotosMoved`, 0) >= 0 AND COALESCE(`PhotosCopiedShared`, 0) >= 0 AND COALESCE(`PhotosParked`, 0) >= 0 AND COALESCE(`Interruptions`, 0) >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_JobRuns_JobType",
                table: "JobRuns",
                sql: "`JobType` IN (1, 2, 3, 4, 5)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_JobHeartbeats_JobType",
                table: "JobHeartbeats",
                sql: "`JobType` IN (1, 2, 3, 4, 5)");

            migrationBuilder.CreateIndex(
                name: "IX_FileAnalysisJobs_AccountFileId",
                table: "FileAnalysisJobs",
                column: "AccountFileId");

            migrationBuilder.CreateIndex(
                name: "IX_FileAnalysisJobs_Status",
                table: "FileAnalysisJobs",
                column: "Status");

            // Dropped LAST, once the single-column index exists again to serve the AccountFileId foreign key.
            migrationBuilder.DropIndex(
                name: "IX_FileAnalysisJobs_AccountFileId_Status",
                table: "FileAnalysisJobs");
        }
    }
}
