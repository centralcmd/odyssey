using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Odyssey.Context.Migrations
{
    /// <inheritdoc />
    public partial class MovePhotoOriginalsToObjectStore : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "FileId",
                table: "Photos",
                type: "char(36)",
                nullable: true,
                collation: "ascii_general_ci",
                oldClrType: typeof(Guid),
                oldType: "char(36)")
                .OldAnnotation("Relational:Collation", "ascii_general_ci");

            migrationBuilder.AddColumn<string>(
                name: "ContentType",
                table: "Photos",
                type: "varchar(256)",
                unicode: false,
                maxLength: 256,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "FileName",
                table: "Photos",
                type: "varchar(256)",
                maxLength: 256,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<Guid>(
                name: "LegacyFileId",
                table: "Photos",
                type: "char(36)",
                nullable: true,
                collation: "ascii_general_ci");

            migrationBuilder.AddColumn<string>(
                name: "Sha256Hash",
                table: "Photos",
                type: "char(64)",
                unicode: false,
                fixedLength: true,
                maxLength: 64,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<long>(
                name: "SizeBytes",
                table: "Photos",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "StoredObjectId",
                table: "Photos",
                type: "char(36)",
                nullable: true,
                collation: "ascii_general_ci");

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
                    table.CheckConstraint("CK_PhotoOriginalBackfillFailures_Code", "`Code` IN ('hash', 'read', 'too-large')");
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
                    HolderId = table.Column<Guid>(type: "char(36)", nullable: true, collation: "ascii_general_ci"),
                    LeaseUntilUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    Version = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    ConsecutiveEnvironmentalFailures = table.Column<int>(type: "int", nullable: false),
                    NextAttemptAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    LastRunAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    CopiedSharedCount = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PhotoOriginalBackfillLeases", x => x.Id);
                    table.CheckConstraint("CK_PhotoOriginalBackfillLeases_Singleton", "`Id` = 1");
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "PhotoOriginalBuckets",
                columns: table => new
                {
                    Year = table.Column<int>(type: "int", nullable: false),
                    CurrentBucket = table.Column<int>(type: "int", nullable: false),
                    PhotosWritten = table.Column<int>(type: "int", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PhotoOriginalBuckets", x => x.Year);
                    table.CheckConstraint("CK_PhotoOriginalBuckets_CurrentBucketPositive", "`CurrentBucket` >= 1");
                    table.CheckConstraint("CK_PhotoOriginalBuckets_PhotosWrittenRange", "`PhotosWritten` BETWEEN 0 AND 10000");
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.InsertData(
                table: "PhotoOriginalBackfillLeases",
                columns: new[] { "Id", "ConsecutiveEnvironmentalFailures", "CopiedSharedCount", "HolderId", "LastRunAtUtc", "LeaseUntilUtc", "NextAttemptAtUtc", "Version" },
                values: new object[] { (byte)1, 0, 0, null, null, null, null, new Guid("8a1e6f2c-0b4d-4c7a-9e35-71d2c8f04b96") });

            migrationBuilder.CreateIndex(
                name: "IX_Photos_LegacyFileId",
                table: "Photos",
                column: "LegacyFileId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Photos_Sha256Hash_SizeBytes",
                table: "Photos",
                columns: new[] { "Sha256Hash", "SizeBytes" });

            migrationBuilder.CreateIndex(
                name: "IX_Photos_StoredObjectId",
                table: "Photos",
                column: "StoredObjectId",
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_Photos_ExactlyOneSource",
                table: "Photos",
                sql: "(`FileId` IS NULL) <> (`StoredObjectId` IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Photos_OriginalComplete",
                table: "Photos",
                sql: "`StoredObjectId` IS NULL OR (`FileName` IS NOT NULL AND `ContentType` IS NOT NULL AND `SizeBytes` IS NOT NULL AND `Sha256Hash` IS NOT NULL)");

            migrationBuilder.AddForeignKey(
                name: "FK_Photos_StoredObjects_StoredObjectId",
                table: "Photos",
                column: "StoredObjectId",
                principalTable: "StoredObjects",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        // Issue #428 §11: Down() restores schema, not data. Once any photo owns its original (an upload or the backfill),
        // Photos.FileId holds NULLs and the final ALTER back to NOT NULL fails — rolling back after that is a backup
        // restore, not this method.
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Photos_StoredObjects_StoredObjectId",
                table: "Photos");

            migrationBuilder.DropTable(
                name: "PhotoOriginalBackfillFailures");

            migrationBuilder.DropTable(
                name: "PhotoOriginalBackfillLeases");

            migrationBuilder.DropTable(
                name: "PhotoOriginalBuckets");

            migrationBuilder.DropIndex(
                name: "IX_Photos_LegacyFileId",
                table: "Photos");

            migrationBuilder.DropIndex(
                name: "IX_Photos_Sha256Hash_SizeBytes",
                table: "Photos");

            migrationBuilder.DropIndex(
                name: "IX_Photos_StoredObjectId",
                table: "Photos");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Photos_ExactlyOneSource",
                table: "Photos");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Photos_OriginalComplete",
                table: "Photos");

            migrationBuilder.DropColumn(
                name: "ContentType",
                table: "Photos");

            migrationBuilder.DropColumn(
                name: "FileName",
                table: "Photos");

            migrationBuilder.DropColumn(
                name: "LegacyFileId",
                table: "Photos");

            migrationBuilder.DropColumn(
                name: "Sha256Hash",
                table: "Photos");

            migrationBuilder.DropColumn(
                name: "SizeBytes",
                table: "Photos");

            migrationBuilder.DropColumn(
                name: "StoredObjectId",
                table: "Photos");

            migrationBuilder.AlterColumn<Guid>(
                name: "FileId",
                table: "Photos",
                type: "char(36)",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                collation: "ascii_general_ci",
                oldClrType: typeof(Guid),
                oldType: "char(36)",
                oldNullable: true)
                .OldAnnotation("Relational:Collation", "ascii_general_ci");
        }
    }
}
