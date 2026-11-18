using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Odyssey.Context.Migrations
{
    /// <inheritdoc />
    public partial class MoveFilesToObjectStore : Migration
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

            migrationBuilder.AddColumn<int>(
                name: "ContactAvatarsMoved",
                table: "JobRuns",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "FilesParked",
                table: "JobRuns",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "FinanceFilesMoved",
                table: "JobRuns",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "JournalFilesMoved",
                table: "JobRuns",
                type: "int",
                nullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "FileBlobId",
                table: "FileMetadata",
                type: "char(36)",
                nullable: true,
                collation: "ascii_general_ci",
                oldClrType: typeof(Guid),
                oldType: "char(36)")
                .OldAnnotation("Relational:Collation", "ascii_general_ci");

            migrationBuilder.AddColumn<Guid>(
                name: "ContactAvatarStoredObjectId",
                table: "FileMetadata",
                type: "char(36)",
                nullable: true,
                collation: "ascii_general_ci");

            migrationBuilder.AddColumn<Guid>(
                name: "FinanceStoredObjectId",
                table: "FileMetadata",
                type: "char(36)",
                nullable: true,
                collation: "ascii_general_ci");

            migrationBuilder.AddColumn<Guid>(
                name: "JournalStoredObjectId",
                table: "FileMetadata",
                type: "char(36)",
                nullable: true,
                collation: "ascii_general_ci");

            migrationBuilder.CreateTable(
                name: "FileBlobBackfillFailures",
                columns: table => new
                {
                    FileId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    Code = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    RecordedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FileBlobBackfillFailures", x => x.FileId);
                    table.CheckConstraint("CK_FileBlobBackfillFailures_Code", "`Code` IN ('hash', 'read', 'too-large', 'type', 'error')");
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "FileBlobBackfillLeases",
                columns: table => new
                {
                    Id = table.Column<byte>(type: "tinyint unsigned", nullable: false),
                    HolderId = table.Column<Guid>(type: "char(36)", nullable: true, collation: "ascii_general_ci"),
                    LeaseUntilUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    Version = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    ConsecutiveEnvironmentalFailures = table.Column<int>(type: "int", nullable: false),
                    NextAttemptAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    LastRunAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FileBlobBackfillLeases", x => x.Id);
                    table.CheckConstraint("CK_FileBlobBackfillLeases_Singleton", "`Id` = 1");
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "StoredFileBuckets",
                columns: table => new
                {
                    FeatureKey = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Year = table.Column<int>(type: "int", nullable: false),
                    CurrentBucket = table.Column<int>(type: "int", nullable: false),
                    ObjectsWritten = table.Column<int>(type: "int", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StoredFileBuckets", x => new { x.FeatureKey, x.Year });
                    table.CheckConstraint("CK_StoredFileBuckets_CurrentBucketPositive", "`CurrentBucket` >= 1");
                    table.CheckConstraint("CK_StoredFileBuckets_ObjectsWrittenRange", "`ObjectsWritten` BETWEEN 0 AND 10000");
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.InsertData(
                table: "FileBlobBackfillLeases",
                columns: new[] { "Id", "ConsecutiveEnvironmentalFailures", "HolderId", "LastRunAtUtc", "LeaseUntilUtc", "NextAttemptAtUtc", "Version" },
                values: new object[] { (byte)1, 0, null, null, null, null, new Guid("3c9b7e51-6a2f-4d08-b1e4-95f0a27c6d13") });

            migrationBuilder.AddCheckConstraint(
                name: "CK_JobRuns_CountersNonNegative",
                table: "JobRuns",
                sql: "COALESCE(`ItemsProcessed`, 0) >= 0 AND COALESCE(`ItemsFailed`, 0) >= 0 AND COALESCE(`DurationMs`, 0) >= 0 AND COALESCE(`RowsInserted`, 0) >= 0 AND COALESCE(`RowsSkipped`, 0) >= 0 AND COALESCE(`RowsRejected`, 0) >= 0 AND COALESCE(`RowsDeleted`, 0) >= 0 AND COALESCE(`PointsAttempted`, 0) >= 0 AND COALESCE(`PointsSkippedNotExpired`, 0) >= 0 AND COALESCE(`PointsFailed`, 0) >= 0 AND COALESCE(`ForecastsStored`, 0) >= 0 AND COALESCE(`ObservationDaysStored`, 0) >= 0 AND COALESCE(`FilesErased`, 0) >= 0 AND COALESCE(`AbandonedWritesSwept`, 0) >= 0 AND COALESCE(`ObjectsScrubbed`, 0) >= 0 AND COALESCE(`IntegrityFailures`, 0) >= 0 AND COALESCE(`OrphansFound`, 0) >= 0 AND COALESCE(`PhotosRendered`, 0) >= 0 AND COALESCE(`PhotosFailed`, 0) >= 0 AND COALESCE(`PhotosRequeued`, 0) >= 0 AND COALESCE(`Crashes`, 0) >= 0 AND COALESCE(`Hangs`, 0) >= 0 AND COALESCE(`PhotosMoved`, 0) >= 0 AND COALESCE(`PhotosCopiedShared`, 0) >= 0 AND COALESCE(`PhotosParked`, 0) >= 0 AND COALESCE(`Interruptions`, 0) >= 0 AND COALESCE(`AnalysesCompleted`, 0) >= 0 AND COALESCE(`AnalysesFailed`, 0) >= 0 AND COALESCE(`AnalysesRefused`, 0) >= 0 AND COALESCE(`FinanceFilesMoved`, 0) >= 0 AND COALESCE(`JournalFilesMoved`, 0) >= 0 AND COALESCE(`ContactAvatarsMoved`, 0) >= 0 AND COALESCE(`FilesParked`, 0) >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_JobRuns_JobType",
                table: "JobRuns",
                sql: "`JobType` IN (1, 2, 3, 4, 5, 6, 7)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_JobHeartbeats_JobType",
                table: "JobHeartbeats",
                sql: "`JobType` IN (1, 2, 3, 4, 5, 6, 7)");

            migrationBuilder.CreateIndex(
                name: "IX_FileMetadata_ContactAvatarStoredObjectId",
                table: "FileMetadata",
                column: "ContactAvatarStoredObjectId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FileMetadata_FinanceStoredObjectId",
                table: "FileMetadata",
                column: "FinanceStoredObjectId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FileMetadata_JournalStoredObjectId",
                table: "FileMetadata",
                column: "JournalStoredObjectId",
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_FileMetadata_ExactlyOneSource",
                table: "FileMetadata",
                sql: "(`FileBlobId` IS NOT NULL) + (`FinanceStoredObjectId` IS NOT NULL) + (`JournalStoredObjectId` IS NOT NULL) + (`ContactAvatarStoredObjectId` IS NOT NULL) = 1");

            migrationBuilder.AddForeignKey(
                name: "FK_FileMetadata_StoredObjects_ContactAvatarStoredObjectId",
                table: "FileMetadata",
                column: "ContactAvatarStoredObjectId",
                principalTable: "StoredObjects",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_FileMetadata_StoredObjects_FinanceStoredObjectId",
                table: "FileMetadata",
                column: "FinanceStoredObjectId",
                principalTable: "StoredObjects",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_FileMetadata_StoredObjects_JournalStoredObjectId",
                table: "FileMetadata",
                column: "JournalStoredObjectId",
                principalTable: "StoredObjects",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_FileMetadata_StoredObjects_ContactAvatarStoredObjectId",
                table: "FileMetadata");

            migrationBuilder.DropForeignKey(
                name: "FK_FileMetadata_StoredObjects_FinanceStoredObjectId",
                table: "FileMetadata");

            migrationBuilder.DropForeignKey(
                name: "FK_FileMetadata_StoredObjects_JournalStoredObjectId",
                table: "FileMetadata");

            migrationBuilder.DropTable(
                name: "FileBlobBackfillFailures");

            migrationBuilder.DropTable(
                name: "FileBlobBackfillLeases");

            migrationBuilder.DropTable(
                name: "StoredFileBuckets");

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
                name: "IX_FileMetadata_ContactAvatarStoredObjectId",
                table: "FileMetadata");

            migrationBuilder.DropIndex(
                name: "IX_FileMetadata_FinanceStoredObjectId",
                table: "FileMetadata");

            migrationBuilder.DropIndex(
                name: "IX_FileMetadata_JournalStoredObjectId",
                table: "FileMetadata");

            migrationBuilder.DropCheckConstraint(
                name: "CK_FileMetadata_ExactlyOneSource",
                table: "FileMetadata");

            migrationBuilder.DropColumn(
                name: "ContactAvatarsMoved",
                table: "JobRuns");

            migrationBuilder.DropColumn(
                name: "FilesParked",
                table: "JobRuns");

            migrationBuilder.DropColumn(
                name: "FinanceFilesMoved",
                table: "JobRuns");

            migrationBuilder.DropColumn(
                name: "JournalFilesMoved",
                table: "JobRuns");

            migrationBuilder.DropColumn(
                name: "ContactAvatarStoredObjectId",
                table: "FileMetadata");

            migrationBuilder.DropColumn(
                name: "FinanceStoredObjectId",
                table: "FileMetadata");

            migrationBuilder.DropColumn(
                name: "JournalStoredObjectId",
                table: "FileMetadata");

            migrationBuilder.AlterColumn<Guid>(
                name: "FileBlobId",
                table: "FileMetadata",
                type: "char(36)",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                collation: "ascii_general_ci",
                oldClrType: typeof(Guid),
                oldType: "char(36)",
                oldNullable: true)
                .OldAnnotation("Relational:Collation", "ascii_general_ci");

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
        }
    }
}
