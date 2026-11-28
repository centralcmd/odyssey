using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Odyssey.Context.Migrations
{
    /// <inheritdoc />
    public partial class AddRecipeImports : Migration
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
                name: "ImportFilesReleased",
                table: "JobRuns",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ImportsCompleted",
                table: "JobRuns",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ImportsFailed",
                table: "JobRuns",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ImportsPurged",
                table: "JobRuns",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ImportsRefused",
                table: "JobRuns",
                type: "int",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "RecipeImports",
                columns: table => new
                {
                    RecipeImportId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    Status = table.Column<int>(type: "int", nullable: false),
                    ClientRequestId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    RequestedByUserId = table.Column<string>(type: "varchar(255)", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    QueuedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    ClaimedAt = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    TransferStartedAt = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    FinishedAt = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    LeaseExpiresAt = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    RunId = table.Column<Guid>(type: "char(36)", nullable: true, collation: "ascii_general_ci"),
                    Generation = table.Column<int>(type: "int", nullable: false),
                    FailureCode = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    FailureMessage = table.Column<string>(type: "varchar(1024)", maxLength: 1024, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    AnalyzerModel = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    PromptVersion = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    MaxTokensInForce = table.Column<int>(type: "int", nullable: true),
                    AnalyzerBaseUrlOrigin = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    AnalyzerBaseUrlHost = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ProcessorInForce = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ProcessorRegionInForce = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    LawfulBasis = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ConsentRecorded = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    ConsentMethod = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ConsentText = table.Column<string>(type: "varchar(1024)", maxLength: 1024, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    DisclosureVersionInForce = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    SendsTagNames = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    TagVocabularyCount = table.Column<int>(type: "int", nullable: true),
                    TagVocabularyReduced = table.Column<bool>(type: "tinyint(1)", nullable: true),
                    CancelledByUserId = table.Column<string>(type: "varchar(255)", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    CancelledAt = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    DeletedByUserId = table.Column<string>(type: "varchar(255)", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    DeletedAt = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    ContentPurgedAt = table.Column<DateTime>(type: "datetime(6)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecipeImports", x => x.RecipeImportId);
                    table.ForeignKey(
                        name: "FK_RecipeImports_AspNetUsers_CancelledByUserId",
                        column: x => x.CancelledByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_RecipeImports_AspNetUsers_DeletedByUserId",
                        column: x => x.DeletedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_RecipeImports_AspNetUsers_RequestedByUserId",
                        column: x => x.RequestedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "RecipeImportDrafts",
                columns: table => new
                {
                    RecipeImportDraftId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    RecipeImportId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    Ordinal = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    Title = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    DraftJson = table.Column<string>(type: "longtext", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Warnings = table.Column<string>(type: "text", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    AcceptedRecipeId = table.Column<Guid>(type: "char(36)", nullable: true, collation: "ascii_general_ci"),
                    UpdatedByUserId = table.Column<string>(type: "varchar(255)", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    UpdatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    AcceptedByUserId = table.Column<string>(type: "varchar(255)", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    AcceptedAt = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    DiscardedByUserId = table.Column<string>(type: "varchar(255)", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    DiscardedAt = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    Version = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecipeImportDrafts", x => x.RecipeImportDraftId);
                    table.ForeignKey(
                        name: "FK_RecipeImportDrafts_AspNetUsers_AcceptedByUserId",
                        column: x => x.AcceptedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_RecipeImportDrafts_AspNetUsers_DiscardedByUserId",
                        column: x => x.DiscardedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_RecipeImportDrafts_AspNetUsers_UpdatedByUserId",
                        column: x => x.UpdatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_RecipeImportDrafts_RecipeImports_RecipeImportId",
                        column: x => x.RecipeImportId,
                        principalTable: "RecipeImports",
                        principalColumn: "RecipeImportId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_RecipeImportDrafts_Recipes_AcceptedRecipeId",
                        column: x => x.AcceptedRecipeId,
                        principalTable: "Recipes",
                        principalColumn: "RecipeId",
                        onDelete: ReferentialAction.SetNull);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "RecipeImportSources",
                columns: table => new
                {
                    RecipeImportSourceId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    RecipeImportId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    Ordinal = table.Column<int>(type: "int", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    Text = table.Column<string>(type: "longtext", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    FileId = table.Column<Guid>(type: "char(36)", nullable: true, collation: "ascii_general_ci"),
                    ImportOwned = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    ContentType = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecipeImportSources", x => x.RecipeImportSourceId);
                    table.ForeignKey(
                        name: "FK_RecipeImportSources_FileMetadata_FileId",
                        column: x => x.FileId,
                        principalTable: "FileMetadata",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_RecipeImportSources_RecipeImports_RecipeImportId",
                        column: x => x.RecipeImportId,
                        principalTable: "RecipeImports",
                        principalColumn: "RecipeImportId",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.InsertData(
                table: "SystemSettings",
                columns: new[] { "Key", "UpdatedAt", "UpdatedBy", "Value" },
                values: new object[,]
                {
                    { "RecipeImportEnabled", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "false" },
                    { "RecipeImportRetentionDays", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "30" }
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_JobRuns_CountersNonNegative",
                table: "JobRuns",
                sql: "COALESCE(`ItemsProcessed`, 0) >= 0 AND COALESCE(`ItemsFailed`, 0) >= 0 AND COALESCE(`DurationMs`, 0) >= 0 AND COALESCE(`RowsInserted`, 0) >= 0 AND COALESCE(`RowsSkipped`, 0) >= 0 AND COALESCE(`RowsRejected`, 0) >= 0 AND COALESCE(`RowsDeleted`, 0) >= 0 AND COALESCE(`PointsAttempted`, 0) >= 0 AND COALESCE(`PointsSkippedNotExpired`, 0) >= 0 AND COALESCE(`PointsFailed`, 0) >= 0 AND COALESCE(`ForecastsStored`, 0) >= 0 AND COALESCE(`ObservationDaysStored`, 0) >= 0 AND COALESCE(`FilesErased`, 0) >= 0 AND COALESCE(`AbandonedWritesSwept`, 0) >= 0 AND COALESCE(`ObjectsScrubbed`, 0) >= 0 AND COALESCE(`IntegrityFailures`, 0) >= 0 AND COALESCE(`OrphansFound`, 0) >= 0 AND COALESCE(`PhotosRendered`, 0) >= 0 AND COALESCE(`PhotosFailed`, 0) >= 0 AND COALESCE(`PhotosRequeued`, 0) >= 0 AND COALESCE(`Crashes`, 0) >= 0 AND COALESCE(`Hangs`, 0) >= 0 AND COALESCE(`AnalysesCompleted`, 0) >= 0 AND COALESCE(`AnalysesFailed`, 0) >= 0 AND COALESCE(`AnalysesRefused`, 0) >= 0 AND COALESCE(`InstrumentsAttempted`, 0) >= 0 AND COALESCE(`InstrumentsSucceeded`, 0) >= 0 AND COALESCE(`InstrumentsFailed`, 0) >= 0 AND COALESCE(`PricesInserted`, 0) >= 0 AND COALESCE(`FallbacksUsed`, 0) >= 0 AND COALESCE(`RequestsMade`, 0) >= 0 AND COALESCE(`PricesDeleted`, 0) >= 0 AND COALESCE(`InstrumentsDeferred`, 0) >= 0 AND COALESCE(`FeedsFetched`, 0) >= 0 AND COALESCE(`ItemsStored`, 0) >= 0 AND COALESCE(`ItemsRevised`, 0) >= 0 AND COALESCE(`ImagesStored`, 0) >= 0 AND COALESCE(`FeedsQuarantined`, 0) >= 0 AND COALESCE(`FeedsMarked`, 0) >= 0 AND COALESCE(`SeriesKeyed`, 0) >= 0 AND COALESCE(`SeriesKeyFailures`, 0) >= 0 AND COALESCE(`KeyRowsDetached`, 0) >= 0 AND COALESCE(`ArtworkStored`, 0) >= 0 AND COALESCE(`ArtworkFailed`, 0) >= 0 AND COALESCE(`ChaptersFetched`, 0) >= 0 AND COALESCE(`DownloadsStored`, 0) >= 0 AND COALESCE(`DownloadsFailed`, 0) >= 0 AND COALESCE(`DownloadsDeferred`, 0) >= 0 AND COALESCE(`DownloadsRecovered`, 0) >= 0 AND COALESCE(`ReservationsRefused`, 0) >= 0 AND COALESCE(`AudioBytesStored`, 0) >= 0 AND COALESCE(`AudioReleased`, 0) >= 0 AND COALESCE(`AudioBytesReleased`, 0) >= 0 AND COALESCE(`ImportsCompleted`, 0) >= 0 AND COALESCE(`ImportsFailed`, 0) >= 0 AND COALESCE(`ImportsRefused`, 0) >= 0 AND COALESCE(`ImportsPurged`, 0) >= 0 AND COALESCE(`ImportFilesReleased`, 0) >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_JobRuns_JobType",
                table: "JobRuns",
                sql: "`JobType` IN (1, 2, 3, 4, 6, 8, 9, 10, 11, 12, 13, 14)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_JobHeartbeats_JobType",
                table: "JobHeartbeats",
                sql: "`JobType` IN (1, 2, 3, 4, 6, 8, 9, 10, 11, 12, 13, 14)");

            migrationBuilder.CreateIndex(
                name: "IX_RecipeImportDrafts_AcceptedByUserId",
                table: "RecipeImportDrafts",
                column: "AcceptedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_RecipeImportDrafts_AcceptedRecipeId",
                table: "RecipeImportDrafts",
                column: "AcceptedRecipeId");

            migrationBuilder.CreateIndex(
                name: "IX_RecipeImportDrafts_DiscardedByUserId",
                table: "RecipeImportDrafts",
                column: "DiscardedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_RecipeImportDrafts_RecipeImportId_Ordinal",
                table: "RecipeImportDrafts",
                columns: new[] { "RecipeImportId", "Ordinal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RecipeImportDrafts_RecipeImportId_Status",
                table: "RecipeImportDrafts",
                columns: new[] { "RecipeImportId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_RecipeImportDrafts_UpdatedByUserId",
                table: "RecipeImportDrafts",
                column: "UpdatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_RecipeImports_CancelledByUserId",
                table: "RecipeImports",
                column: "CancelledByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_RecipeImports_DeletedAt",
                table: "RecipeImports",
                column: "DeletedAt");

            migrationBuilder.CreateIndex(
                name: "IX_RecipeImports_DeletedByUserId",
                table: "RecipeImports",
                column: "DeletedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_RecipeImports_RequestedByUserId_ClientRequestId",
                table: "RecipeImports",
                columns: new[] { "RequestedByUserId", "ClientRequestId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RecipeImports_RequestedByUserId_Status",
                table: "RecipeImports",
                columns: new[] { "RequestedByUserId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_RecipeImports_Status_QueuedAt",
                table: "RecipeImports",
                columns: new[] { "Status", "QueuedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_RecipeImportSources_FileId",
                table: "RecipeImportSources",
                column: "FileId");

            migrationBuilder.CreateIndex(
                name: "IX_RecipeImportSources_RecipeImportId_Ordinal",
                table: "RecipeImportSources",
                columns: new[] { "RecipeImportId", "Ordinal" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RecipeImportDrafts");

            migrationBuilder.DropTable(
                name: "RecipeImportSources");

            migrationBuilder.DropTable(
                name: "RecipeImports");

            migrationBuilder.DropCheckConstraint(
                name: "CK_JobRuns_CountersNonNegative",
                table: "JobRuns");

            migrationBuilder.DropCheckConstraint(
                name: "CK_JobRuns_JobType",
                table: "JobRuns");

            migrationBuilder.DropCheckConstraint(
                name: "CK_JobHeartbeats_JobType",
                table: "JobHeartbeats");

            migrationBuilder.DeleteData(
                table: "SystemSettings",
                keyColumn: "Key",
                keyValue: "RecipeImportEnabled");

            migrationBuilder.DeleteData(
                table: "SystemSettings",
                keyColumn: "Key",
                keyValue: "RecipeImportRetentionDays");

            migrationBuilder.DropColumn(
                name: "ImportFilesReleased",
                table: "JobRuns");

            migrationBuilder.DropColumn(
                name: "ImportsCompleted",
                table: "JobRuns");

            migrationBuilder.DropColumn(
                name: "ImportsFailed",
                table: "JobRuns");

            migrationBuilder.DropColumn(
                name: "ImportsPurged",
                table: "JobRuns");

            migrationBuilder.DropColumn(
                name: "ImportsRefused",
                table: "JobRuns");

            migrationBuilder.AddCheckConstraint(
                name: "CK_JobRuns_CountersNonNegative",
                table: "JobRuns",
                sql: "COALESCE(`ItemsProcessed`, 0) >= 0 AND COALESCE(`ItemsFailed`, 0) >= 0 AND COALESCE(`DurationMs`, 0) >= 0 AND COALESCE(`RowsInserted`, 0) >= 0 AND COALESCE(`RowsSkipped`, 0) >= 0 AND COALESCE(`RowsRejected`, 0) >= 0 AND COALESCE(`RowsDeleted`, 0) >= 0 AND COALESCE(`PointsAttempted`, 0) >= 0 AND COALESCE(`PointsSkippedNotExpired`, 0) >= 0 AND COALESCE(`PointsFailed`, 0) >= 0 AND COALESCE(`ForecastsStored`, 0) >= 0 AND COALESCE(`ObservationDaysStored`, 0) >= 0 AND COALESCE(`FilesErased`, 0) >= 0 AND COALESCE(`AbandonedWritesSwept`, 0) >= 0 AND COALESCE(`ObjectsScrubbed`, 0) >= 0 AND COALESCE(`IntegrityFailures`, 0) >= 0 AND COALESCE(`OrphansFound`, 0) >= 0 AND COALESCE(`PhotosRendered`, 0) >= 0 AND COALESCE(`PhotosFailed`, 0) >= 0 AND COALESCE(`PhotosRequeued`, 0) >= 0 AND COALESCE(`Crashes`, 0) >= 0 AND COALESCE(`Hangs`, 0) >= 0 AND COALESCE(`AnalysesCompleted`, 0) >= 0 AND COALESCE(`AnalysesFailed`, 0) >= 0 AND COALESCE(`AnalysesRefused`, 0) >= 0 AND COALESCE(`InstrumentsAttempted`, 0) >= 0 AND COALESCE(`InstrumentsSucceeded`, 0) >= 0 AND COALESCE(`InstrumentsFailed`, 0) >= 0 AND COALESCE(`PricesInserted`, 0) >= 0 AND COALESCE(`FallbacksUsed`, 0) >= 0 AND COALESCE(`RequestsMade`, 0) >= 0 AND COALESCE(`PricesDeleted`, 0) >= 0 AND COALESCE(`InstrumentsDeferred`, 0) >= 0 AND COALESCE(`FeedsFetched`, 0) >= 0 AND COALESCE(`ItemsStored`, 0) >= 0 AND COALESCE(`ItemsRevised`, 0) >= 0 AND COALESCE(`ImagesStored`, 0) >= 0 AND COALESCE(`FeedsQuarantined`, 0) >= 0 AND COALESCE(`FeedsMarked`, 0) >= 0 AND COALESCE(`SeriesKeyed`, 0) >= 0 AND COALESCE(`SeriesKeyFailures`, 0) >= 0 AND COALESCE(`KeyRowsDetached`, 0) >= 0 AND COALESCE(`ArtworkStored`, 0) >= 0 AND COALESCE(`ArtworkFailed`, 0) >= 0 AND COALESCE(`ChaptersFetched`, 0) >= 0 AND COALESCE(`DownloadsStored`, 0) >= 0 AND COALESCE(`DownloadsFailed`, 0) >= 0 AND COALESCE(`DownloadsDeferred`, 0) >= 0 AND COALESCE(`DownloadsRecovered`, 0) >= 0 AND COALESCE(`ReservationsRefused`, 0) >= 0 AND COALESCE(`AudioBytesStored`, 0) >= 0 AND COALESCE(`AudioReleased`, 0) >= 0 AND COALESCE(`AudioBytesReleased`, 0) >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_JobRuns_JobType",
                table: "JobRuns",
                sql: "`JobType` IN (1, 2, 3, 4, 6, 8, 9, 10, 11, 12)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_JobHeartbeats_JobType",
                table: "JobHeartbeats",
                sql: "`JobType` IN (1, 2, 3, 4, 6, 8, 9, 10, 11, 12)");
        }
    }
}
