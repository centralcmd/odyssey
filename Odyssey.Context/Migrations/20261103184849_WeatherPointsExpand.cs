using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Odyssey.Context.Migrations
{
    /// <summary>
    /// Step 1 of 2 of issue #387 §11 (weather points): adds <c>WeatherPoints</c>, a nullable
    /// <c>WeatherPointId</c> beside <c>LocationId</c> on every weather table, and the nullable
    /// <c>Locations.WeatherPointId</c> link; then turns each <c>WeatherLocations</c> row into a point under the
    /// <b>same GUID</b>, backfills every <c>WeatherPointId</c> and links each converted location to its point.
    /// Additive: the old schema still works until <c>WeatherPointsContract</c>.
    /// </summary>
    /// <remarks>
    /// A <b>data</b> migration, and not replay-safe by product decision: MariaDB commits DDL implicitly, so a
    /// failure is fixed forward, with the pre-deploy backup as the fallback. <c>Down()</c> restores the schema
    /// only.
    /// </remarks>
    public partial class WeatherPointsExpand : Migration
    {
        /// <summary>
        /// The tables keyed on a location that carry weather data; each gains <c>WeatherPointId</c> here.
        /// </summary>
        internal static readonly string[] KeyedTables =
        [
            "WeatherLocationSites",
            "WeatherLocationSchedules",
            "WeatherStations",
            "WeatherForecastRuns",
            "WeatherForecastSteps",
            "WeatherObservations",
            "WeatherDays",
        ];

        /// <summary>
        /// The conversion, shared with the contract's re-backfill (§11 Contract step 1): one point per weather row
        /// that has none yet, named after its location, holding the mode of any purge in progress
        /// (<c>HistoryOnly</c> = 1 while the forecast is on, else <c>Full</c> = 0); every null
        /// <c>WeatherPointId</c> set to its <c>LocationId</c>; and every unlinked converted location linked.
        /// <c>Locations.Name</c> is unique and at most 100 characters, so no name can collide or overflow.
        /// </summary>
        internal static void Backfill(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                INSERT INTO `WeatherPoints` (`WeatherPointId`, `Name`, `ForecastEnabled`, `HistoryEnabled`, `IsTracked`,
                    `HistoryFrom`, `EnabledAt`, `CurrentSiteVersion`, `PurgeStartedAt`, `PurgeMode`, `StationResolvedAtUtc`,
                    `SettingsVersion`, `CreatedByUserId`, `UpdatedByUserId`, `CreatedAt`, `UpdatedAt`)
                SELECT w.`LocationId`, l.`Name`, w.`ForecastEnabled`, w.`HistoryEnabled`, w.`IsTracked`,
                    w.`HistoryFrom`, w.`EnabledAt`, w.`CurrentSiteVersion`, w.`PurgeStartedAt`,
                    CASE WHEN w.`PurgeStartedAt` IS NULL THEN NULL WHEN w.`ForecastEnabled` THEN 1 ELSE 0 END,
                    w.`StationResolvedAtUtc`, UUID(), w.`CreatedByUserId`, w.`UpdatedByUserId`, w.`CreatedAt`, w.`UpdatedAt`
                FROM `WeatherLocations` AS w
                JOIN `Locations` AS l ON l.`LocationId` = w.`LocationId`
                WHERE NOT EXISTS (SELECT 1 FROM `WeatherPoints` AS p WHERE p.`WeatherPointId` = w.`LocationId`);
                """);

            foreach (var table in KeyedTables)
            {
                migrationBuilder.Sql(
                    $"UPDATE `{table}` SET `WeatherPointId` = `LocationId` WHERE `WeatherPointId` IS NULL;");
            }

            migrationBuilder.Sql("""
                UPDATE `Locations` AS l
                JOIN `WeatherPoints` AS p ON p.`WeatherPointId` = l.`LocationId`
                SET l.`WeatherPointId` = p.`WeatherPointId`
                WHERE l.`WeatherPointId` IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "WeatherPointId",
                table: "WeatherStations",
                type: "char(36)",
                nullable: true,
                collation: "ascii_general_ci");

            migrationBuilder.AddColumn<Guid>(
                name: "WeatherPointId",
                table: "WeatherObservations",
                type: "char(36)",
                nullable: true,
                collation: "ascii_general_ci");

            migrationBuilder.AddColumn<Guid>(
                name: "WeatherPointId",
                table: "WeatherLocationSites",
                type: "char(36)",
                nullable: true,
                collation: "ascii_general_ci");

            migrationBuilder.AddColumn<Guid>(
                name: "WeatherPointId",
                table: "WeatherLocationSchedules",
                type: "char(36)",
                nullable: true,
                collation: "ascii_general_ci");

            migrationBuilder.AddColumn<Guid>(
                name: "WeatherPointId",
                table: "WeatherForecastSteps",
                type: "char(36)",
                nullable: true,
                collation: "ascii_general_ci");

            migrationBuilder.AddColumn<Guid>(
                name: "WeatherPointId",
                table: "WeatherForecastRuns",
                type: "char(36)",
                nullable: true,
                collation: "ascii_general_ci");

            migrationBuilder.AddColumn<Guid>(
                name: "WeatherPointId",
                table: "WeatherDays",
                type: "char(36)",
                nullable: true,
                collation: "ascii_general_ci");

            migrationBuilder.AddColumn<Guid>(
                name: "WeatherPointId",
                table: "Locations",
                type: "char(36)",
                nullable: true,
                collation: "ascii_general_ci");

            migrationBuilder.CreateTable(
                name: "WeatherPoints",
                columns: table => new
                {
                    WeatherPointId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    Name = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ForecastEnabled = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    HistoryEnabled = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    IsTracked = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    HistoryFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    EnabledAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    CurrentSiteVersion = table.Column<int>(type: "int", nullable: false),
                    PurgeStartedAt = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    PurgeMode = table.Column<int>(type: "int", nullable: true),
                    StationResolvedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    SettingsVersion = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    CreatedByUserId = table.Column<string>(type: "varchar(255)", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    UpdatedByUserId = table.Column<string>(type: "varchar(255)", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WeatherPoints", x => x.WeatherPointId);
                    table.CheckConstraint("CK_WeatherPoints_CurrentSiteVersionPositive", "`CurrentSiteVersion` >= 1");
                    table.CheckConstraint("CK_WeatherPoints_PurgeModeWithFence", "(`PurgeStartedAt` IS NULL) = (`PurgeMode` IS NULL)");
                    table.ForeignKey(
                        name: "FK_WeatherPoints_AspNetUsers_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_WeatherPoints_AspNetUsers_UpdatedByUserId",
                        column: x => x.UpdatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_Locations_WeatherPointId",
                table: "Locations",
                column: "WeatherPointId");

            migrationBuilder.CreateIndex(
                name: "IX_WeatherPoints_CreatedByUserId",
                table: "WeatherPoints",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_WeatherPoints_Name",
                table: "WeatherPoints",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WeatherPoints_UpdatedByUserId",
                table: "WeatherPoints",
                column: "UpdatedByUserId");

            migrationBuilder.AddForeignKey(
                name: "FK_Locations_WeatherPoints_WeatherPointId",
                table: "Locations",
                column: "WeatherPointId",
                principalTable: "WeatherPoints",
                principalColumn: "WeatherPointId",
                onDelete: ReferentialAction.SetNull);

            Backfill(migrationBuilder);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Locations_WeatherPoints_WeatherPointId",
                table: "Locations");

            migrationBuilder.DropTable(
                name: "WeatherPoints");

            migrationBuilder.DropIndex(
                name: "IX_Locations_WeatherPointId",
                table: "Locations");

            migrationBuilder.DropColumn(
                name: "WeatherPointId",
                table: "WeatherStations");

            migrationBuilder.DropColumn(
                name: "WeatherPointId",
                table: "WeatherObservations");

            migrationBuilder.DropColumn(
                name: "WeatherPointId",
                table: "WeatherLocationSites");

            migrationBuilder.DropColumn(
                name: "WeatherPointId",
                table: "WeatherLocationSchedules");

            migrationBuilder.DropColumn(
                name: "WeatherPointId",
                table: "WeatherForecastSteps");

            migrationBuilder.DropColumn(
                name: "WeatherPointId",
                table: "WeatherForecastRuns");

            migrationBuilder.DropColumn(
                name: "WeatherPointId",
                table: "WeatherDays");

            migrationBuilder.DropColumn(
                name: "WeatherPointId",
                table: "Locations");
        }
    }
}
