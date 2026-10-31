using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Odyssey.Context.Migrations
{
    /// <inheritdoc />
    public partial class AddPhotoThumbnails : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PhotoThumbnailBuckets",
                columns: table => new
                {
                    Id = table.Column<byte>(type: "tinyint unsigned", nullable: false),
                    CurrentBucket = table.Column<int>(type: "int", nullable: false),
                    PhotosWritten = table.Column<int>(type: "int", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PhotoThumbnailBuckets", x => x.Id);
                    table.CheckConstraint("CK_PhotoThumbnailBuckets_CurrentBucketPositive", "`CurrentBucket` >= 1");
                    table.CheckConstraint("CK_PhotoThumbnailBuckets_PhotosWrittenRange", "`PhotosWritten` BETWEEN 0 AND 10000");
                    table.CheckConstraint("CK_PhotoThumbnailBuckets_Singleton", "`Id` = 1");
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "PhotoThumbnailIncidents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    Kind = table.Column<byte>(type: "tinyint unsigned", nullable: false),
                    OccurredAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    WorkerName = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PhotoThumbnailIncidents", x => x.Id);
                    table.CheckConstraint("CK_PhotoThumbnailIncidents_KindRange", "`Kind` IN (1, 2, 3)");
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "PhotoThumbnailJobs",
                columns: table => new
                {
                    PhotoId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    State = table.Column<byte>(type: "tinyint unsigned", nullable: false),
                    Generation = table.Column<int>(type: "int", nullable: false),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    LeaseExpiries = table.Column<int>(type: "int", nullable: false),
                    RunId = table.Column<Guid>(type: "char(36)", nullable: true, collation: "ascii_general_ci"),
                    ReclaimedRunId = table.Column<Guid>(type: "char(36)", nullable: true, collation: "ascii_general_ci"),
                    ReclaimIncidentId = table.Column<long>(type: "bigint", nullable: true),
                    RerunRequested = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    Priority = table.Column<byte>(type: "tinyint unsigned", nullable: false),
                    LastClaimedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    AttemptedRecipeVersion = table.Column<int>(type: "int", nullable: false),
                    NextAttemptAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    LeaseUntilUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    FailureCode = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PhotoThumbnailJobs", x => x.PhotoId);
                    table.CheckConstraint("CK_PhotoThumbnailJobs_AttemptsNonNegative", "`Attempts` >= 0");
                    table.CheckConstraint("CK_PhotoThumbnailJobs_LeaseExpiriesNonNegative", "`LeaseExpiries` >= 0");
                    table.CheckConstraint("CK_PhotoThumbnailJobs_LeaseWhenRunning", "`State` <> 1 OR `LeaseUntilUtc` IS NOT NULL");
                    table.CheckConstraint("CK_PhotoThumbnailJobs_PriorityRange", "`Priority` IN (0, 1)");
                    table.CheckConstraint("CK_PhotoThumbnailJobs_RunOnlyWhenRunning", "`State` = 1 OR (`RunId` IS NULL AND `RerunRequested` = 0)");
                    table.CheckConstraint("CK_PhotoThumbnailJobs_StateRange", "`State` IN (0, 1, 2, 3, 4)");
                    table.ForeignKey(
                        name: "FK_PhotoThumbnailJobs_Photos_PhotoId",
                        column: x => x.PhotoId,
                        principalTable: "Photos",
                        principalColumn: "PhotoId",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "PhotoThumbnails",
                columns: table => new
                {
                    PhotoThumbnailId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    PhotoId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    Size = table.Column<byte>(type: "tinyint unsigned", nullable: false),
                    StoredObjectId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    PixelWidth = table.Column<int>(type: "int", nullable: false),
                    PixelHeight = table.Column<int>(type: "int", nullable: false),
                    RecipeVersion = table.Column<int>(type: "int", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PhotoThumbnails", x => x.PhotoThumbnailId);
                    table.CheckConstraint("CK_PhotoThumbnails_PixelsPositive", "`PixelWidth` > 0 AND `PixelHeight` > 0");
                    table.CheckConstraint("CK_PhotoThumbnails_SizeRange", "`Size` IN (1, 2)");
                    table.ForeignKey(
                        name: "FK_PhotoThumbnails_Photos_PhotoId",
                        column: x => x.PhotoId,
                        principalTable: "Photos",
                        principalColumn: "PhotoId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PhotoThumbnails_StoredObjects_StoredObjectId",
                        column: x => x.StoredObjectId,
                        principalTable: "StoredObjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "PhotoThumbnailWorkerHeartbeats",
                columns: table => new
                {
                    WorkerName = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    State = table.Column<byte>(type: "tinyint unsigned", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PhotoThumbnailWorkerHeartbeats", x => x.WorkerName);
                    table.CheckConstraint("CK_PhotoThumbnailWorkerHeartbeats_StateRange", "`State` IN (0, 1, 2, 3, 4)");
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.InsertData(
                table: "PhotoThumbnailBuckets",
                columns: new[] { "Id", "CurrentBucket", "PhotosWritten", "UpdatedAtUtc" },
                values: new object[] { (byte)1, 1, 0, new DateTime(2026, 10, 31, 0, 0, 0, 0, DateTimeKind.Utc) });

            migrationBuilder.CreateIndex(
                name: "IX_PhotoThumbnailIncidents_OccurredAtUtc",
                table: "PhotoThumbnailIncidents",
                column: "OccurredAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_PhotoThumbnailJobs_State_AttemptedRecipeVersion",
                table: "PhotoThumbnailJobs",
                columns: new[] { "State", "AttemptedRecipeVersion" });

            migrationBuilder.CreateIndex(
                name: "IX_PhotoThumbnailJobs_State_Priority_NextAttemptAtUtc",
                table: "PhotoThumbnailJobs",
                columns: new[] { "State", "Priority", "NextAttemptAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_PhotoThumbnails_PhotoId_Size",
                table: "PhotoThumbnails",
                columns: new[] { "PhotoId", "Size" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PhotoThumbnails_StoredObjectId",
                table: "PhotoThumbnails",
                column: "StoredObjectId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PhotoThumbnailWorkerHeartbeats_UpdatedAtUtc",
                table: "PhotoThumbnailWorkerHeartbeats",
                column: "UpdatedAtUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PhotoThumbnailBuckets");

            migrationBuilder.DropTable(
                name: "PhotoThumbnailIncidents");

            migrationBuilder.DropTable(
                name: "PhotoThumbnailJobs");

            migrationBuilder.DropTable(
                name: "PhotoThumbnails");

            migrationBuilder.DropTable(
                name: "PhotoThumbnailWorkerHeartbeats");
        }
    }
}
