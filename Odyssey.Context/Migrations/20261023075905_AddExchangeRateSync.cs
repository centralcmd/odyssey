using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Odyssey.Context.Migrations
{
    /// <inheritdoc />
    public partial class AddExchangeRateSync : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Issue #349. Additive for data: every existing rate becomes manual (Provider NULL).
            migrationBuilder.AddColumn<int>(
                name: "Provider",
                table: "ExchangeRates",
                type: "int",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ExchangeRateSyncCurrencies",
                columns: table => new
                {
                    CurrencyCode = table.Column<string>(type: "varchar(3)", maxLength: 3, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Enabled = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    HistoryStartDate = table.Column<DateOnly>(type: "date", nullable: true),
                    BackfilledFrom = table.Column<DateOnly>(type: "date", nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    UpdatedByUserId = table.Column<string>(type: "varchar(255)", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExchangeRateSyncCurrencies", x => x.CurrencyCode);
                    table.ForeignKey(
                        name: "FK_ExchangeRateSyncCurrencies_AspNetUsers_UpdatedByUserId",
                        column: x => x.UpdatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ExchangeRateSyncCurrencies_Currencies_CurrencyCode",
                        column: x => x.CurrencyCode,
                        principalTable: "Currencies",
                        principalColumn: "CurrencyCode",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "ExchangeRateSyncRuns",
                columns: table => new
                {
                    ExchangeRateSyncRunId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    Trigger = table.Column<int>(type: "int", nullable: false),
                    RequestedByUserId = table.Column<string>(type: "varchar(255)", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Provider = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    ErrorKind = table.Column<int>(type: "int", nullable: true),
                    StartedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    FinishedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    SpanFrom = table.Column<DateOnly>(type: "date", nullable: true),
                    SpanTo = table.Column<DateOnly>(type: "date", nullable: true),
                    CurrencyCodes = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    RowsInserted = table.Column<int>(type: "int", nullable: false),
                    RowsSkipped = table.Column<int>(type: "int", nullable: false),
                    RowsRejected = table.Column<int>(type: "int", nullable: false),
                    RowsDeleted = table.Column<int>(type: "int", nullable: false),
                    RunningGuard = table.Column<int>(type: "int", nullable: true, computedColumnSql: "IF(`Status` = 0, 1, NULL)", stored: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExchangeRateSyncRuns", x => x.ExchangeRateSyncRunId);
                    table.CheckConstraint("CK_ExchangeRateSyncRuns_ErrorKind", "`ErrorKind` IS NULL OR `ErrorKind` BETWEEN 1 AND 8");
                    table.CheckConstraint("CK_ExchangeRateSyncRuns_Provider", "`Provider` IN (1)");
                    table.CheckConstraint("CK_ExchangeRateSyncRuns_Status", "`Status` IN (0, 1, 2, 3, 4)");
                    table.CheckConstraint("CK_ExchangeRateSyncRuns_Trigger", "`Trigger` IN (0, 1, 2)");
                    table.ForeignKey(
                        name: "FK_ExchangeRateSyncRuns_AspNetUsers_RequestedByUserId",
                        column: x => x.RequestedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.InsertData(
                table: "SystemSettings",
                columns: new[] { "Key", "UpdatedAt", "UpdatedBy", "Value" },
                values: new object[,]
                {
                    { "ExchangeRateHistoryStartDate", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "" },
                    { "ExchangeRateSyncIntervalHours", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "24" }
                });

            migrationBuilder.CreateIndex(
                name: "IX_ExchangeRates_AsOf",
                table: "ExchangeRates",
                column: "AsOf");

            migrationBuilder.CreateIndex(
                name: "IX_ExchangeRates_FromCurrencyCode_ToCurrencyCode_AsOf_Provider",
                table: "ExchangeRates",
                columns: new[] { "FromCurrencyCode", "ToCurrencyCode", "AsOf", "Provider" },
                unique: true);

            // Dropped only AFTER its replacement exists: it is the index MariaDB uses for the
            // FromCurrencyCode foreign key, and an FK's only index cannot be dropped (errno 1553). The
            // new unique index has it as its prefix, so it serves both the FK and the latest-rate lookup.
            migrationBuilder.DropIndex(
                name: "IX_ExchangeRates_FromCurrencyCode_ToCurrencyCode_AsOf",
                table: "ExchangeRates");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ExchangeRates_Provider",
                table: "ExchangeRates",
                sql: "`Provider` IS NULL OR `Provider` IN (1)");

            migrationBuilder.CreateIndex(
                name: "IX_ExchangeRateSyncCurrencies_UpdatedByUserId",
                table: "ExchangeRateSyncCurrencies",
                column: "UpdatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ExchangeRateSyncRuns_RequestedByUserId",
                table: "ExchangeRateSyncRuns",
                column: "RequestedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ExchangeRateSyncRuns_RunningGuard",
                table: "ExchangeRateSyncRuns",
                column: "RunningGuard",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExchangeRateSyncRuns_StartedAtUtc",
                table: "ExchangeRateSyncRuns",
                column: "StartedAtUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ExchangeRateSyncCurrencies");

            migrationBuilder.DropTable(
                name: "ExchangeRateSyncRuns");

            migrationBuilder.DropIndex(
                name: "IX_ExchangeRates_AsOf",
                table: "ExchangeRates");

            // The old index first, for the same foreign-key reason as in Up. Fetched rows then read as
            // manual: Down restores schema, not provenance.
            migrationBuilder.CreateIndex(
                name: "IX_ExchangeRates_FromCurrencyCode_ToCurrencyCode_AsOf",
                table: "ExchangeRates",
                columns: new[] { "FromCurrencyCode", "ToCurrencyCode", "AsOf" });

            migrationBuilder.DropIndex(
                name: "IX_ExchangeRates_FromCurrencyCode_ToCurrencyCode_AsOf_Provider",
                table: "ExchangeRates");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ExchangeRates_Provider",
                table: "ExchangeRates");

            migrationBuilder.DeleteData(
                table: "SystemSettings",
                keyColumn: "Key",
                keyValue: "ExchangeRateHistoryStartDate");

            migrationBuilder.DeleteData(
                table: "SystemSettings",
                keyColumn: "Key",
                keyValue: "ExchangeRateSyncIntervalHours");

            migrationBuilder.DropColumn(
                name: "Provider",
                table: "ExchangeRates");

        }
    }
}
