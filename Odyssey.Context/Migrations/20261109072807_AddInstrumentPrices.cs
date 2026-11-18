using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Odyssey.Context.Migrations
{
    /// <inheritdoc />
    public partial class AddInstrumentPrices : Migration
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
                name: "FallbacksUsed",
                table: "JobRuns",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "InstrumentsAttempted",
                table: "JobRuns",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "InstrumentsFailed",
                table: "JobRuns",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "InstrumentsSucceeded",
                table: "JobRuns",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PricesDeleted",
                table: "JobRuns",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PricesInserted",
                table: "JobRuns",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RequestsMade",
                table: "JobRuns",
                type: "int",
                nullable: true);

            // The guard's unique index is dropped and recreated around the expression change rather than leaving
            // MariaDB to modify an indexed generated column in place (issue #446 §4.6).
            migrationBuilder.DropIndex(
                name: "IX_JobRuns_ExclusiveGuard",
                table: "JobRuns");

            migrationBuilder.AlterColumn<int>(
                name: "ExclusiveGuard",
                table: "JobRuns",
                type: "int",
                nullable: true,
                computedColumnSql: "IF(`Status` = 0 AND (`JobType` IN (1, 3, 8) OR `Trigger` = 1), `JobType`, NULL)",
                stored: false,
                oldClrType: typeof(int),
                oldType: "int",
                oldNullable: true,
                oldComputedColumnSql: "IF(`Status` = 0 AND (`JobType` IN (1, 3) OR `Trigger` = 1), `JobType`, NULL)",
                oldStored: false);

            migrationBuilder.CreateIndex(
                name: "IX_JobRuns_ExclusiveGuard",
                table: "JobRuns",
                column: "ExclusiveGuard",
                unique: true);

            migrationBuilder.CreateTable(
                name: "InstrumentPriceSourceStates",
                columns: table => new
                {
                    SourceKey = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    BackOffUntilUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    BackOffReason = table.Column<byte>(type: "tinyint unsigned", nullable: true),
                    RequestDay = table.Column<DateOnly>(type: "date", nullable: true),
                    RequestCount = table.Column<int>(type: "int", nullable: false),
                    Version = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InstrumentPriceSourceStates", x => x.SourceKey);
                    table.CheckConstraint("CK_InstrumentPriceSourceStates_RequestCount", "`RequestCount` >= 0");
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "Instruments",
                columns: table => new
                {
                    InstrumentId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    Symbol = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    MicCode = table.Column<string>(type: "varchar(4)", maxLength: 4, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Isin = table.Column<string>(type: "varchar(12)", maxLength: 12, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Name = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Type = table.Column<int>(type: "int", nullable: false),
                    CurrencyCode = table.Column<string>(type: "varchar(3)", maxLength: 3, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ListingKey = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ShowOnDashboard = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    Archived = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    CreatedByUserId = table.Column<string>(type: "varchar(255)", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    UpdatedByUserId = table.Column<string>(type: "varchar(255)", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Instruments", x => x.InstrumentId);
                    table.CheckConstraint("CK_Instruments_Type", "`Type` IN (1, 2, 3, 9)");
                    table.ForeignKey(
                        name: "FK_Instruments_AspNetUsers_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Instruments_AspNetUsers_UpdatedByUserId",
                        column: x => x.UpdatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Instruments_Currencies_CurrencyCode",
                        column: x => x.CurrencyCode,
                        principalTable: "Currencies",
                        principalColumn: "CurrencyCode",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "InstrumentPrices",
                columns: table => new
                {
                    InstrumentPriceId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    InstrumentId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    TradingDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Close = table.Column<decimal>(type: "decimal(18,8)", precision: 18, scale: 8, nullable: false),
                    SourceKey = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    SourceKeyGuard = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false, computedColumnSql: "COALESCE(`SourceKey`, '')", stored: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    CreatedByUserId = table.Column<string>(type: "varchar(255)", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InstrumentPrices", x => x.InstrumentPriceId);
                    table.CheckConstraint("CK_InstrumentPrices_ClosePositive", "`Close` > 0");
                    table.CheckConstraint("CK_InstrumentPrices_SourceKeyFormat", "`SourceKey` IS NULL OR `SourceKey` REGEXP '^[a-z0-9-]{1,32}\\\\z'");
                    table.ForeignKey(
                        name: "FK_InstrumentPrices_AspNetUsers_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_InstrumentPrices_Instruments_InstrumentId",
                        column: x => x.InstrumentId,
                        principalTable: "Instruments",
                        principalColumn: "InstrumentId",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "InstrumentPriceSourceBindings",
                columns: table => new
                {
                    InstrumentId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    SourceKey = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    SourceSymbol = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Priority = table.Column<int>(type: "int", nullable: false),
                    Enabled = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    HistoryStartDate = table.Column<DateOnly>(type: "date", nullable: true),
                    NewestFetched = table.Column<DateOnly>(type: "date", nullable: true),
                    BackfilledFrom = table.Column<DateOnly>(type: "date", nullable: true),
                    LastSuccessUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    LastFailure = table.Column<byte>(type: "tinyint unsigned", nullable: true),
                    LastFailureUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    ConsecutiveFailures = table.Column<int>(type: "int", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    UpdatedByUserId = table.Column<string>(type: "varchar(255)", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InstrumentPriceSourceBindings", x => new { x.InstrumentId, x.SourceKey });
                    table.CheckConstraint("CK_InstrumentPriceSourceBindings_ConsecutiveFailures", "`ConsecutiveFailures` >= 0");
                    table.CheckConstraint("CK_InstrumentPriceSourceBindings_Priority", "`Priority` BETWEEN 1 AND 9");
                    table.CheckConstraint("CK_InstrumentPriceSourceBindings_SourceKeyFormat", "`SourceKey` REGEXP '^[a-z0-9-]{1,32}\\\\z'");
                    table.ForeignKey(
                        name: "FK_InstrumentPriceSourceBindings_AspNetUsers_UpdatedByUserId",
                        column: x => x.UpdatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_InstrumentPriceSourceBindings_Instruments_InstrumentId",
                        column: x => x.InstrumentId,
                        principalTable: "Instruments",
                        principalColumn: "InstrumentId",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.InsertData(
                table: "SystemSettings",
                columns: new[] { "Key", "UpdatedAt", "UpdatedBy", "Value" },
                values: new object[,]
                {
                    { "InstrumentPriceStaleAfterDays", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "5" },
                    { "InstrumentPriceSyncHistoryDays", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "365" },
                    { "InstrumentPriceSyncIntervalHours", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "24" }
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_JobRuns_CountersNonNegative",
                table: "JobRuns",
                sql: "COALESCE(`ItemsProcessed`, 0) >= 0 AND COALESCE(`ItemsFailed`, 0) >= 0 AND COALESCE(`DurationMs`, 0) >= 0 AND COALESCE(`RowsInserted`, 0) >= 0 AND COALESCE(`RowsSkipped`, 0) >= 0 AND COALESCE(`RowsRejected`, 0) >= 0 AND COALESCE(`RowsDeleted`, 0) >= 0 AND COALESCE(`PointsAttempted`, 0) >= 0 AND COALESCE(`PointsSkippedNotExpired`, 0) >= 0 AND COALESCE(`PointsFailed`, 0) >= 0 AND COALESCE(`ForecastsStored`, 0) >= 0 AND COALESCE(`ObservationDaysStored`, 0) >= 0 AND COALESCE(`FilesErased`, 0) >= 0 AND COALESCE(`AbandonedWritesSwept`, 0) >= 0 AND COALESCE(`ObjectsScrubbed`, 0) >= 0 AND COALESCE(`IntegrityFailures`, 0) >= 0 AND COALESCE(`OrphansFound`, 0) >= 0 AND COALESCE(`PhotosRendered`, 0) >= 0 AND COALESCE(`PhotosFailed`, 0) >= 0 AND COALESCE(`PhotosRequeued`, 0) >= 0 AND COALESCE(`Crashes`, 0) >= 0 AND COALESCE(`Hangs`, 0) >= 0 AND COALESCE(`PhotosMoved`, 0) >= 0 AND COALESCE(`PhotosCopiedShared`, 0) >= 0 AND COALESCE(`PhotosParked`, 0) >= 0 AND COALESCE(`Interruptions`, 0) >= 0 AND COALESCE(`AnalysesCompleted`, 0) >= 0 AND COALESCE(`AnalysesFailed`, 0) >= 0 AND COALESCE(`AnalysesRefused`, 0) >= 0 AND COALESCE(`FinanceFilesMoved`, 0) >= 0 AND COALESCE(`JournalFilesMoved`, 0) >= 0 AND COALESCE(`ContactAvatarsMoved`, 0) >= 0 AND COALESCE(`FilesParked`, 0) >= 0 AND COALESCE(`InstrumentsAttempted`, 0) >= 0 AND COALESCE(`InstrumentsSucceeded`, 0) >= 0 AND COALESCE(`InstrumentsFailed`, 0) >= 0 AND COALESCE(`PricesInserted`, 0) >= 0 AND COALESCE(`FallbacksUsed`, 0) >= 0 AND COALESCE(`RequestsMade`, 0) >= 0 AND COALESCE(`PricesDeleted`, 0) >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_JobRuns_JobType",
                table: "JobRuns",
                sql: "`JobType` IN (1, 2, 3, 4, 5, 6, 7, 8)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_JobHeartbeats_JobType",
                table: "JobHeartbeats",
                sql: "`JobType` IN (1, 2, 3, 4, 5, 6, 7, 8)");

            migrationBuilder.CreateIndex(
                name: "IX_InstrumentPrices_CreatedByUserId",
                table: "InstrumentPrices",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_InstrumentPrices_InstrumentId_TradingDate_SourceKeyGuard",
                table: "InstrumentPrices",
                columns: new[] { "InstrumentId", "TradingDate", "SourceKeyGuard" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InstrumentPrices_TradingDate",
                table: "InstrumentPrices",
                column: "TradingDate");

            migrationBuilder.CreateIndex(
                name: "IX_InstrumentPriceSourceBindings_InstrumentId_Priority",
                table: "InstrumentPriceSourceBindings",
                columns: new[] { "InstrumentId", "Priority" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InstrumentPriceSourceBindings_UpdatedByUserId",
                table: "InstrumentPriceSourceBindings",
                column: "UpdatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Instruments_Archived",
                table: "Instruments",
                column: "Archived");

            migrationBuilder.CreateIndex(
                name: "IX_Instruments_CreatedByUserId",
                table: "Instruments",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Instruments_CurrencyCode",
                table: "Instruments",
                column: "CurrencyCode");

            migrationBuilder.CreateIndex(
                name: "IX_Instruments_ListingKey",
                table: "Instruments",
                column: "ListingKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Instruments_UpdatedByUserId",
                table: "Instruments",
                column: "UpdatedByUserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InstrumentPrices");

            migrationBuilder.DropTable(
                name: "InstrumentPriceSourceBindings");

            migrationBuilder.DropTable(
                name: "InstrumentPriceSourceStates");

            migrationBuilder.DropTable(
                name: "Instruments");

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
                keyValue: "InstrumentPriceStaleAfterDays");

            migrationBuilder.DeleteData(
                table: "SystemSettings",
                keyColumn: "Key",
                keyValue: "InstrumentPriceSyncHistoryDays");

            migrationBuilder.DeleteData(
                table: "SystemSettings",
                keyColumn: "Key",
                keyValue: "InstrumentPriceSyncIntervalHours");

            migrationBuilder.DropColumn(
                name: "FallbacksUsed",
                table: "JobRuns");

            migrationBuilder.DropColumn(
                name: "InstrumentsAttempted",
                table: "JobRuns");

            migrationBuilder.DropColumn(
                name: "InstrumentsFailed",
                table: "JobRuns");

            migrationBuilder.DropColumn(
                name: "InstrumentsSucceeded",
                table: "JobRuns");

            migrationBuilder.DropColumn(
                name: "PricesDeleted",
                table: "JobRuns");

            migrationBuilder.DropColumn(
                name: "PricesInserted",
                table: "JobRuns");

            migrationBuilder.DropColumn(
                name: "RequestsMade",
                table: "JobRuns");

            // The guard's unique index is dropped and recreated around the expression change rather than leaving
            // MariaDB to modify an indexed generated column in place (issue #446 §4.6).
            migrationBuilder.DropIndex(
                name: "IX_JobRuns_ExclusiveGuard",
                table: "JobRuns");

            migrationBuilder.AlterColumn<int>(
                name: "ExclusiveGuard",
                table: "JobRuns",
                type: "int",
                nullable: true,
                computedColumnSql: "IF(`Status` = 0 AND (`JobType` IN (1, 3) OR `Trigger` = 1), `JobType`, NULL)",
                stored: false,
                oldClrType: typeof(int),
                oldType: "int",
                oldNullable: true,
                oldComputedColumnSql: "IF(`Status` = 0 AND (`JobType` IN (1, 3, 8) OR `Trigger` = 1), `JobType`, NULL)",
                oldStored: false);

            migrationBuilder.CreateIndex(
                name: "IX_JobRuns_ExclusiveGuard",
                table: "JobRuns",
                column: "ExclusiveGuard",
                unique: true);

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
        }
    }
}
