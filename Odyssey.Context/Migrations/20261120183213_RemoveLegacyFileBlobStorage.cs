using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Odyssey.Context.Migrations
{
    /// <inheritdoc />
    public partial class RemoveLegacyFileBlobStorage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Refuse before anything changes. MariaDB commits DDL implicitly, so a migration interrupted after its
            // first drop cannot be rolled back: the guard has to run first, and nothing below may run if it fails.
            // A database that still holds a file in the old shape — a photo wrapping a Files-store file, a file whose
            // bytes are in FileBlob, or a file the move parked — has not finished the two moves (issue #428 / #448)
            // that the release before this one ran, and dropping their columns would destroy those bytes. MESSAGE_TEXT is
            // capped at 128 characters, so each message names the column or table and the remedy, nothing more.
            migrationBuilder.Sql(
                """
                BEGIN NOT ATOMIC
                    IF EXISTS (SELECT 1 FROM `Photos` WHERE `FileId` IS NOT NULL) THEN
                        SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT =
                            'RemoveLegacyFileBlobStorage refused: Photos.FileId is set. Finish the photo-original move on the previous release first.';
                    END IF;
                    IF EXISTS (SELECT 1 FROM `FileMetadata` WHERE `FileBlobId` IS NOT NULL) THEN
                        SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT =
                            'RemoveLegacyFileBlobStorage refused: FileMetadata.FileBlobId is set. Finish the file move on the previous release first.';
                    END IF;
                    IF EXISTS (SELECT 1 FROM `FileBlobBackfillFailures`) THEN
                        SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT =
                            'RemoveLegacyFileBlobStorage refused: FileBlobBackfillFailures has rows. Clear them on the previous release first.';
                    END IF;
                END
                """);

            // The two moves' run history goes before anything that depends on it: JobType 5 and 7 are retired
            // ordinals with no TPH branch left, so a remaining row would fail to materialise, and the re-added
            // CK_JobRuns_JobType / CK_JobHeartbeats_JobType / CK_JobRuns_HeldReason below would refuse it. No table
            // references either (no foreign key to JobRuns or JobHeartbeats exists).
            migrationBuilder.Sql("DELETE FROM `JobRuns` WHERE `JobType` IN (5, 7);");
            migrationBuilder.Sql("DELETE FROM `JobHeartbeats` WHERE `JobType` IN (5, 7);");

            migrationBuilder.DropForeignKey(
                name: "FK_FileMetadata_FileBlob_FileBlobId",
                table: "FileMetadata");

            migrationBuilder.DropForeignKey(
                name: "FK_Photos_FileMetadata_FileId",
                table: "Photos");

            migrationBuilder.DropTable(
                name: "FileBlob");

            migrationBuilder.DropTable(
                name: "FileBlobBackfillFailures");

            migrationBuilder.DropTable(
                name: "FileBlobBackfillLeases");

            migrationBuilder.DropTable(
                name: "PhotoOriginalBackfillFailures");

            migrationBuilder.DropTable(
                name: "PhotoOriginalBackfillLeases");

            migrationBuilder.DropIndex(
                name: "IX_Photos_FileId",
                table: "Photos");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Photos_ExactlyOneSource",
                table: "Photos");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Photos_OriginalComplete",
                table: "Photos");

            migrationBuilder.DropCheckConstraint(
                name: "CK_JobRuns_CountersNonNegative",
                table: "JobRuns");

            migrationBuilder.DropCheckConstraint(
                name: "CK_JobRuns_HeldReason",
                table: "JobRuns");

            migrationBuilder.DropCheckConstraint(
                name: "CK_JobRuns_JobType",
                table: "JobRuns");

            migrationBuilder.DropCheckConstraint(
                name: "CK_JobHeartbeats_JobType",
                table: "JobHeartbeats");

            migrationBuilder.DropIndex(
                name: "IX_FileMetadata_FileBlobId",
                table: "FileMetadata");

            migrationBuilder.DropCheckConstraint(
                name: "CK_FileMetadata_ExactlyOneSource",
                table: "FileMetadata");

            migrationBuilder.DropColumn(
                name: "FileId",
                table: "Photos");

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
                name: "Interruptions",
                table: "JobRuns");

            migrationBuilder.DropColumn(
                name: "JournalFilesMoved",
                table: "JobRuns");

            migrationBuilder.DropColumn(
                name: "PhotosCopiedShared",
                table: "JobRuns");

            migrationBuilder.DropColumn(
                name: "PhotosMoved",
                table: "JobRuns");

            migrationBuilder.DropColumn(
                name: "PhotosParked",
                table: "JobRuns");

            migrationBuilder.DropColumn(
                name: "FileBlobId",
                table: "FileMetadata");

            migrationBuilder.AlterColumn<Guid>(
                name: "StoredObjectId",
                table: "Photos",
                type: "char(36)",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                collation: "ascii_general_ci",
                oldClrType: typeof(Guid),
                oldType: "char(36)",
                oldNullable: true)
                .OldAnnotation("Relational:Collation", "ascii_general_ci");

            migrationBuilder.AlterColumn<long>(
                name: "SizeBytes",
                table: "Photos",
                type: "bigint",
                nullable: false,
                defaultValue: 0L,
                oldClrType: typeof(long),
                oldType: "bigint",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Sha256Hash",
                table: "Photos",
                type: "char(64)",
                unicode: false,
                fixedLength: true,
                maxLength: 64,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "char(64)",
                oldUnicode: false,
                oldFixedLength: true,
                oldMaxLength: 64,
                oldNullable: true)
                .Annotation("MySql:CharSet", "utf8mb4")
                .OldAnnotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AlterColumn<string>(
                name: "FileName",
                table: "Photos",
                type: "varchar(256)",
                maxLength: 256,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "varchar(256)",
                oldMaxLength: 256,
                oldNullable: true)
                .Annotation("MySql:CharSet", "utf8mb4")
                .OldAnnotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AlterColumn<string>(
                name: "ContentType",
                table: "Photos",
                type: "varchar(256)",
                unicode: false,
                maxLength: 256,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "varchar(256)",
                oldUnicode: false,
                oldMaxLength: 256,
                oldNullable: true)
                .Annotation("MySql:CharSet", "utf8mb4")
                .OldAnnotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddCheckConstraint(
                name: "CK_JobRuns_CountersNonNegative",
                table: "JobRuns",
                sql: "COALESCE(`ItemsProcessed`, 0) >= 0 AND COALESCE(`ItemsFailed`, 0) >= 0 AND COALESCE(`DurationMs`, 0) >= 0 AND COALESCE(`RowsInserted`, 0) >= 0 AND COALESCE(`RowsSkipped`, 0) >= 0 AND COALESCE(`RowsRejected`, 0) >= 0 AND COALESCE(`RowsDeleted`, 0) >= 0 AND COALESCE(`PointsAttempted`, 0) >= 0 AND COALESCE(`PointsSkippedNotExpired`, 0) >= 0 AND COALESCE(`PointsFailed`, 0) >= 0 AND COALESCE(`ForecastsStored`, 0) >= 0 AND COALESCE(`ObservationDaysStored`, 0) >= 0 AND COALESCE(`FilesErased`, 0) >= 0 AND COALESCE(`AbandonedWritesSwept`, 0) >= 0 AND COALESCE(`ObjectsScrubbed`, 0) >= 0 AND COALESCE(`IntegrityFailures`, 0) >= 0 AND COALESCE(`OrphansFound`, 0) >= 0 AND COALESCE(`PhotosRendered`, 0) >= 0 AND COALESCE(`PhotosFailed`, 0) >= 0 AND COALESCE(`PhotosRequeued`, 0) >= 0 AND COALESCE(`Crashes`, 0) >= 0 AND COALESCE(`Hangs`, 0) >= 0 AND COALESCE(`AnalysesCompleted`, 0) >= 0 AND COALESCE(`AnalysesFailed`, 0) >= 0 AND COALESCE(`AnalysesRefused`, 0) >= 0 AND COALESCE(`InstrumentsAttempted`, 0) >= 0 AND COALESCE(`InstrumentsSucceeded`, 0) >= 0 AND COALESCE(`InstrumentsFailed`, 0) >= 0 AND COALESCE(`PricesInserted`, 0) >= 0 AND COALESCE(`FallbacksUsed`, 0) >= 0 AND COALESCE(`RequestsMade`, 0) >= 0 AND COALESCE(`PricesDeleted`, 0) >= 0 AND COALESCE(`FeedsFetched`, 0) >= 0 AND COALESCE(`ItemsStored`, 0) >= 0 AND COALESCE(`ItemsRevised`, 0) >= 0 AND COALESCE(`ImagesStored`, 0) >= 0 AND COALESCE(`FeedsQuarantined`, 0) >= 0 AND COALESCE(`FeedsMarked`, 0) >= 0 AND COALESCE(`SeriesKeyed`, 0) >= 0 AND COALESCE(`SeriesKeyFailures`, 0) >= 0 AND COALESCE(`KeyRowsDetached`, 0) >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_JobRuns_HeldReason",
                table: "JobRuns",
                sql: "`HeldReason` IS NULL OR `HeldReason` BETWEEN 1 AND 5");

            migrationBuilder.AddCheckConstraint(
                name: "CK_JobRuns_JobType",
                table: "JobRuns",
                sql: "`JobType` IN (1, 2, 3, 4, 6, 8, 9, 10)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_JobHeartbeats_JobType",
                table: "JobHeartbeats",
                sql: "`JobType` IN (1, 2, 3, 4, 6, 8, 9, 10)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_FileMetadata_ExactlyOneSource",
                table: "FileMetadata",
                sql: "(`FinanceStoredObjectId` IS NOT NULL) + (`JournalStoredObjectId` IS NOT NULL) + (`ContactAvatarStoredObjectId` IS NOT NULL) = 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Schema only. Up() destroyed no file bytes (its guard refuses while any are left in the database), but it
            // did delete the two moves' run history, and the restored FileBlob table and columns come back empty.
            // The only undo of the data is restoring a backup taken before the upgrade.
            migrationBuilder.DropCheckConstraint(
                name: "CK_JobRuns_CountersNonNegative",
                table: "JobRuns");

            migrationBuilder.DropCheckConstraint(
                name: "CK_JobRuns_HeldReason",
                table: "JobRuns");

            migrationBuilder.DropCheckConstraint(
                name: "CK_JobRuns_JobType",
                table: "JobRuns");

            migrationBuilder.DropCheckConstraint(
                name: "CK_JobHeartbeats_JobType",
                table: "JobHeartbeats");

            migrationBuilder.DropCheckConstraint(
                name: "CK_FileMetadata_ExactlyOneSource",
                table: "FileMetadata");

            migrationBuilder.AlterColumn<Guid>(
                name: "StoredObjectId",
                table: "Photos",
                type: "char(36)",
                nullable: true,
                collation: "ascii_general_ci",
                oldClrType: typeof(Guid),
                oldType: "char(36)")
                .OldAnnotation("Relational:Collation", "ascii_general_ci");

            migrationBuilder.AlterColumn<long>(
                name: "SizeBytes",
                table: "Photos",
                type: "bigint",
                nullable: true,
                oldClrType: typeof(long),
                oldType: "bigint");

            migrationBuilder.AlterColumn<string>(
                name: "Sha256Hash",
                table: "Photos",
                type: "char(64)",
                unicode: false,
                fixedLength: true,
                maxLength: 64,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "char(64)",
                oldUnicode: false,
                oldFixedLength: true,
                oldMaxLength: 64)
                .Annotation("MySql:CharSet", "utf8mb4")
                .OldAnnotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AlterColumn<string>(
                name: "FileName",
                table: "Photos",
                type: "varchar(256)",
                maxLength: 256,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "varchar(256)",
                oldMaxLength: 256)
                .Annotation("MySql:CharSet", "utf8mb4")
                .OldAnnotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AlterColumn<string>(
                name: "ContentType",
                table: "Photos",
                type: "varchar(256)",
                unicode: false,
                maxLength: 256,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "varchar(256)",
                oldUnicode: false,
                oldMaxLength: 256)
                .Annotation("MySql:CharSet", "utf8mb4")
                .OldAnnotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<Guid>(
                name: "FileId",
                table: "Photos",
                type: "char(36)",
                nullable: true,
                collation: "ascii_general_ci");

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
                name: "Interruptions",
                table: "JobRuns",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "JournalFilesMoved",
                table: "JobRuns",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PhotosCopiedShared",
                table: "JobRuns",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PhotosMoved",
                table: "JobRuns",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PhotosParked",
                table: "JobRuns",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "FileBlobId",
                table: "FileMetadata",
                type: "char(36)",
                nullable: true,
                collation: "ascii_general_ci");

            migrationBuilder.CreateTable(
                name: "FileBlob",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    Content = table.Column<byte[]>(type: "longblob", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FileBlob", x => x.Id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

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
                    ConsecutiveEnvironmentalFailures = table.Column<int>(type: "int", nullable: false),
                    HolderId = table.Column<Guid>(type: "char(36)", nullable: true, collation: "ascii_general_ci"),
                    LastRunAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    LeaseUntilUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    NextAttemptAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    Version = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FileBlobBackfillLeases", x => x.Id);
                    table.CheckConstraint("CK_FileBlobBackfillLeases_Singleton", "`Id` = 1");
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "PhotoOriginalBackfillFailures",
                columns: table => new
                {
                    PhotoId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    Code = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    RecordedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PhotoOriginalBackfillFailures", x => x.PhotoId);
                    table.CheckConstraint("CK_PhotoOriginalBackfillFailures_Code", "`Code` IN ('hash', 'read', 'too-large', 'error')");
                    table.ForeignKey(
                        name: "FK_PhotoOriginalBackfillFailures_Photos_PhotoId",
                        column: x => x.PhotoId,
                        principalTable: "Photos",
                        principalColumn: "PhotoId",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "PhotoOriginalBackfillLeases",
                columns: table => new
                {
                    Id = table.Column<byte>(type: "tinyint unsigned", nullable: false),
                    ConsecutiveEnvironmentalFailures = table.Column<int>(type: "int", nullable: false),
                    CopiedSharedCount = table.Column<int>(type: "int", nullable: false),
                    HolderId = table.Column<Guid>(type: "char(36)", nullable: true, collation: "ascii_general_ci"),
                    LastRunAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    LeaseUntilUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    NextAttemptAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    Version = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PhotoOriginalBackfillLeases", x => x.Id);
                    table.CheckConstraint("CK_PhotoOriginalBackfillLeases_Singleton", "`Id` = 1");
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.InsertData(
                table: "FileBlobBackfillLeases",
                columns: new[] { "Id", "ConsecutiveEnvironmentalFailures", "HolderId", "LastRunAtUtc", "LeaseUntilUtc", "NextAttemptAtUtc", "Version" },
                values: new object[] { (byte)1, 0, null, null, null, null, new Guid("3c9b7e51-6a2f-4d08-b1e4-95f0a27c6d13") });

            migrationBuilder.InsertData(
                table: "PhotoOriginalBackfillLeases",
                columns: new[] { "Id", "ConsecutiveEnvironmentalFailures", "CopiedSharedCount", "HolderId", "LastRunAtUtc", "LeaseUntilUtc", "NextAttemptAtUtc", "Version" },
                values: new object[] { (byte)1, 0, 0, null, null, null, null, new Guid("8a1e6f2c-0b4d-4c7a-9e35-71d2c8f04b96") });

            migrationBuilder.CreateIndex(
                name: "IX_Photos_FileId",
                table: "Photos",
                column: "FileId",
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_Photos_ExactlyOneSource",
                table: "Photos",
                sql: "(`FileId` IS NULL) <> (`StoredObjectId` IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Photos_OriginalComplete",
                table: "Photos",
                sql: "`StoredObjectId` IS NULL OR (`FileName` IS NOT NULL AND `ContentType` IS NOT NULL AND `SizeBytes` IS NOT NULL AND `Sha256Hash` IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_JobRuns_CountersNonNegative",
                table: "JobRuns",
                sql: "COALESCE(`ItemsProcessed`, 0) >= 0 AND COALESCE(`ItemsFailed`, 0) >= 0 AND COALESCE(`DurationMs`, 0) >= 0 AND COALESCE(`RowsInserted`, 0) >= 0 AND COALESCE(`RowsSkipped`, 0) >= 0 AND COALESCE(`RowsRejected`, 0) >= 0 AND COALESCE(`RowsDeleted`, 0) >= 0 AND COALESCE(`PointsAttempted`, 0) >= 0 AND COALESCE(`PointsSkippedNotExpired`, 0) >= 0 AND COALESCE(`PointsFailed`, 0) >= 0 AND COALESCE(`ForecastsStored`, 0) >= 0 AND COALESCE(`ObservationDaysStored`, 0) >= 0 AND COALESCE(`FilesErased`, 0) >= 0 AND COALESCE(`AbandonedWritesSwept`, 0) >= 0 AND COALESCE(`ObjectsScrubbed`, 0) >= 0 AND COALESCE(`IntegrityFailures`, 0) >= 0 AND COALESCE(`OrphansFound`, 0) >= 0 AND COALESCE(`PhotosRendered`, 0) >= 0 AND COALESCE(`PhotosFailed`, 0) >= 0 AND COALESCE(`PhotosRequeued`, 0) >= 0 AND COALESCE(`Crashes`, 0) >= 0 AND COALESCE(`Hangs`, 0) >= 0 AND COALESCE(`PhotosMoved`, 0) >= 0 AND COALESCE(`PhotosCopiedShared`, 0) >= 0 AND COALESCE(`PhotosParked`, 0) >= 0 AND COALESCE(`Interruptions`, 0) >= 0 AND COALESCE(`AnalysesCompleted`, 0) >= 0 AND COALESCE(`AnalysesFailed`, 0) >= 0 AND COALESCE(`AnalysesRefused`, 0) >= 0 AND COALESCE(`FinanceFilesMoved`, 0) >= 0 AND COALESCE(`JournalFilesMoved`, 0) >= 0 AND COALESCE(`ContactAvatarsMoved`, 0) >= 0 AND COALESCE(`FilesParked`, 0) >= 0 AND COALESCE(`InstrumentsAttempted`, 0) >= 0 AND COALESCE(`InstrumentsSucceeded`, 0) >= 0 AND COALESCE(`InstrumentsFailed`, 0) >= 0 AND COALESCE(`PricesInserted`, 0) >= 0 AND COALESCE(`FallbacksUsed`, 0) >= 0 AND COALESCE(`RequestsMade`, 0) >= 0 AND COALESCE(`PricesDeleted`, 0) >= 0 AND COALESCE(`FeedsFetched`, 0) >= 0 AND COALESCE(`ItemsStored`, 0) >= 0 AND COALESCE(`ItemsRevised`, 0) >= 0 AND COALESCE(`ImagesStored`, 0) >= 0 AND COALESCE(`FeedsQuarantined`, 0) >= 0 AND COALESCE(`FeedsMarked`, 0) >= 0 AND COALESCE(`SeriesKeyed`, 0) >= 0 AND COALESCE(`SeriesKeyFailures`, 0) >= 0 AND COALESCE(`KeyRowsDetached`, 0) >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_JobRuns_HeldReason",
                table: "JobRuns",
                sql: "`HeldReason` IS NULL OR `HeldReason` BETWEEN 1 AND 6");

            migrationBuilder.AddCheckConstraint(
                name: "CK_JobRuns_JobType",
                table: "JobRuns",
                sql: "`JobType` IN (1, 2, 3, 4, 5, 6, 7, 8, 9, 10)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_JobHeartbeats_JobType",
                table: "JobHeartbeats",
                sql: "`JobType` IN (1, 2, 3, 4, 5, 6, 7, 8, 9, 10)");

            migrationBuilder.CreateIndex(
                name: "IX_FileMetadata_FileBlobId",
                table: "FileMetadata",
                column: "FileBlobId",
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_FileMetadata_ExactlyOneSource",
                table: "FileMetadata",
                sql: "(`FileBlobId` IS NOT NULL) + (`FinanceStoredObjectId` IS NOT NULL) + (`JournalStoredObjectId` IS NOT NULL) + (`ContactAvatarStoredObjectId` IS NOT NULL) = 1");

            migrationBuilder.AddForeignKey(
                name: "FK_FileMetadata_FileBlob_FileBlobId",
                table: "FileMetadata",
                column: "FileBlobId",
                principalTable: "FileBlob",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Photos_FileMetadata_FileId",
                table: "Photos",
                column: "FileId",
                principalTable: "FileMetadata",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
