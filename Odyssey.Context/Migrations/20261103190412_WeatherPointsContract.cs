using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Odyssey.Context.Migrations
{
    /// <summary>
    /// Step 2 of 2 of issue #387 §11 (weather points): re-backfills late writers, deletes orphaned weather rows,
    /// re-keys every weather table from <c>LocationId</c> to <c>WeatherPointId</c> with the §4 keys, renames the
    /// two point-owned tables and the two settings keys, rewrites the weather preference's field name and drops
    /// <c>WeatherLocations</c>.
    /// </summary>
    /// <remarks>
    /// A <b>data</b> migration: it deletes orphaned rows and drops a table and columns. Not replay-safe, by product
    /// decision — a failure is fixed forward, with the pre-deploy backup as the fallback (see
    /// <c>docs/migration-history-drift.md</c>). <c>Down()</c> restores the schema only; undoing it means restoring
    /// the backup.
    /// </remarks>
    public partial class WeatherPointsContract : Migration
    {
        /// <summary>The settings keys issue #387 renamed: old → new. Value and <c>UpdatedBy</c> are kept.</summary>
        private static readonly (string Old, string New)[] RenamedSettings =
        [
            ("WeatherDefaultLocationId", "WeatherDefaultPointId"),
            ("WeatherMaxTrackedLocations", "WeatherMaxTrackedPoints"),
        ];

        /// <summary>The data tables, children first, so an orphan delete never trips a key.</summary>
        private static readonly string[] OrphanDeleteOrder =
        [
            "WeatherForecastSteps",
            "WeatherForecastRuns",
            "WeatherObservations",
            "WeatherDays",
            "WeatherStations",
            "WeatherLocationSchedules",
            "WeatherLocationSites",
        ];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1. Re-backfill (architect R2-4): rows an old API or worker wrote between the two migrations carry a
            //    null WeatherPointId, and a weather row created meanwhile has no point yet.
            WeatherPointsExpand.Backfill(migrationBuilder);

            // 2. Rows whose LocationId has no WeatherLocations row cannot take the new keys. An unconditional
            //    cleanup; the product owner accepts losing them (§11 Contract step 2).
            foreach (var table in OrphanDeleteOrder)
            {
                migrationBuilder.Sql($"""
                    DELETE FROM `{table}`
                    WHERE NOT EXISTS (SELECT 1 FROM `WeatherLocations` AS w WHERE w.`LocationId` = `{table}`.`LocationId`);
                    """);
            }

            // 3. The old keys: to Locations, to WeatherLocations, and the point→site composite key.
            migrationBuilder.DropForeignKey(name: "FK_WeatherDays_Locations_LocationId", table: "WeatherDays");
            migrationBuilder.DropForeignKey(name: "FK_WeatherForecastRuns_Locations_LocationId", table: "WeatherForecastRuns");
            migrationBuilder.DropForeignKey(name: "FK_WeatherObservations_Locations_LocationId", table: "WeatherObservations");
            migrationBuilder.DropForeignKey(name: "FK_WeatherStations_WeatherLocations_LocationId", table: "WeatherStations");
            migrationBuilder.DropForeignKey(name: "FK_WeatherLocationSchedules_WeatherLocations_LocationId", table: "WeatherLocationSchedules");
            migrationBuilder.DropForeignKey(name: "FK_WeatherLocationSites_Locations_LocationId", table: "WeatherLocationSites");
            migrationBuilder.DropForeignKey(name: "FK_WeatherLocationSites_AspNetUsers_AppliedByUserId", table: "WeatherLocationSites");
            migrationBuilder.DropForeignKey(name: "FK_WeatherLocations_WeatherLocationSites_LocationId_CurrentSite~", table: "WeatherLocations");

            // 4. The keys and indexes on LocationId, then the column; WeatherPointId becomes required (every row
            //    was backfilled above, so this fails loudly rather than defaulting if one was not).
            migrationBuilder.DropPrimaryKey(name: "PK_WeatherStations", table: "WeatherStations");
            migrationBuilder.DropPrimaryKey(name: "PK_WeatherDays", table: "WeatherDays");
            migrationBuilder.DropPrimaryKey(name: "PK_WeatherLocationSites", table: "WeatherLocationSites");
            migrationBuilder.DropPrimaryKey(name: "PK_WeatherLocationSchedules", table: "WeatherLocationSchedules");
            migrationBuilder.DropIndex(name: "IX_WeatherObservations_LocationId_LocalDate_SiteVersion_Fetched~", table: "WeatherObservations");
            migrationBuilder.DropIndex(name: "IX_WeatherForecastSteps_LocationId_TimeUtc_SiteVersion_RunProvi~", table: "WeatherForecastSteps");
            migrationBuilder.DropIndex(name: "IX_WeatherForecastRuns_LocationId_SiteVersion_ProviderUpdatedAt", table: "WeatherForecastRuns");
            migrationBuilder.DropIndex(name: "IX_WeatherLocationSites_AppliedByUserId", table: "WeatherLocationSites");
            migrationBuilder.DropCheckConstraint(name: "CK_WeatherLocationSites_VersionPositive", table: "WeatherLocationSites");
            migrationBuilder.DropColumn(name: "ArchivedSkipLogged", table: "WeatherLocationSchedules");

            foreach (var table in WeatherPointsExpand.KeyedTables)
            {
                migrationBuilder.DropColumn(name: "LocationId", table: table);
                migrationBuilder.AlterColumn<Guid>(
                    name: "WeatherPointId",
                    table: table,
                    type: "char(36)",
                    nullable: false,
                    collation: "ascii_general_ci",
                    oldClrType: typeof(Guid),
                    oldType: "char(36)",
                    oldNullable: true)
                    .OldAnnotation("Relational:Collation", "ascii_general_ci");
            }

            // 5. The two point-owned tables take their new names.
            migrationBuilder.RenameTable(name: "WeatherLocationSites", newName: "WeatherPointSites");
            migrationBuilder.RenameTable(name: "WeatherLocationSchedules", newName: "WeatherPointSchedules");

            // The new keys, indexes and foreign keys (§4). WeatherForecastSteps gains none: it is the largest
            // table and already cascades with its run.
            migrationBuilder.AddPrimaryKey(name: "PK_WeatherPointSites", table: "WeatherPointSites", columns: ["WeatherPointId", "SiteVersion"]);
            migrationBuilder.AddPrimaryKey(name: "PK_WeatherPointSchedules", table: "WeatherPointSchedules", column: "WeatherPointId");
            migrationBuilder.AddPrimaryKey(name: "PK_WeatherStations", table: "WeatherStations", columns: ["WeatherPointId", "Group"]);
            migrationBuilder.AddPrimaryKey(name: "PK_WeatherDays", table: "WeatherDays", columns: ["WeatherPointId", "LocalDate"]);
            migrationBuilder.AddCheckConstraint(name: "CK_WeatherPointSites_VersionPositive", table: "WeatherPointSites", sql: "`SiteVersion` >= 1");

            migrationBuilder.CreateIndex(
                name: "IX_WeatherObservations_WeatherPointId_LocalDate_SiteVersion_Fet~",
                table: "WeatherObservations",
                columns: ["WeatherPointId", "LocalDate", "SiteVersion", "FetchedAtUtc"]);
            migrationBuilder.CreateIndex(
                name: "IX_WeatherForecastSteps_WeatherPointId_TimeUtc_SiteVersion_RunP~",
                table: "WeatherForecastSteps",
                columns: ["WeatherPointId", "TimeUtc", "SiteVersion", "RunProviderUpdatedAt", "RunFetchedAtUtc"]);
            migrationBuilder.CreateIndex(
                name: "IX_WeatherForecastRuns_WeatherPointId_SiteVersion_ProviderUpdat~",
                table: "WeatherForecastRuns",
                columns: ["WeatherPointId", "SiteVersion", "ProviderUpdatedAt"],
                unique: true);
            migrationBuilder.CreateIndex(name: "IX_WeatherPointSites_AppliedByUserId", table: "WeatherPointSites", column: "AppliedByUserId");

            AddPointKey(migrationBuilder, "WeatherPointSites", ReferentialAction.Cascade);
            AddPointKey(migrationBuilder, "WeatherPointSchedules", ReferentialAction.Cascade);
            AddPointKey(migrationBuilder, "WeatherStations", ReferentialAction.Cascade);
            AddPointKey(migrationBuilder, "WeatherForecastRuns", ReferentialAction.Restrict);
            AddPointKey(migrationBuilder, "WeatherObservations", ReferentialAction.Restrict);
            AddPointKey(migrationBuilder, "WeatherDays", ReferentialAction.Restrict);
            migrationBuilder.AddForeignKey(
                name: "FK_WeatherPointSites_AspNetUsers_AppliedByUserId",
                table: "WeatherPointSites",
                column: "AppliedByUserId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            // 6. The settings keys, renamed in place (architect finding 3). Changing the key through HasData would
            //    make EF emit a delete + insert, losing the administrator's value and UpdatedBy; the model's
            //    HasData names the new keys, and this is what brings a real database to it.
            foreach (var (oldKey, newKey) in RenamedSettings)
            {
                migrationBuilder.Sql($"UPDATE `SystemSettings` SET `Key` = '{newKey}' WHERE `Key` = '{oldKey}';");
            }

            // 7. The weather preference's field, defaultLocationId → defaultPointId, value unchanged — only on a
            //    row that is valid JSON; a malformed row is left untouched and resolves as absent, as it does today.
            //    A string value is unquoted and re-set (MariaDB would otherwise store the quotes); a JSON null is
            //    simply dropped, which reads the same as an explicit null.
            migrationBuilder.Sql("""
                UPDATE `UserPreferences`
                SET `PreferencesJson` = JSON_REMOVE(
                        JSON_SET(`PreferencesJson`, '$.defaultPointId', JSON_UNQUOTE(JSON_EXTRACT(`PreferencesJson`, '$.defaultLocationId'))),
                        '$.defaultLocationId')
                WHERE `Key` = 'weather-preferences'
                  AND JSON_VALID(`PreferencesJson`)
                  AND JSON_TYPE(JSON_EXTRACT(`PreferencesJson`, '$.defaultLocationId')) = 'STRING';
                """);
            migrationBuilder.Sql("""
                UPDATE `UserPreferences`
                SET `PreferencesJson` = JSON_REMOVE(`PreferencesJson`, '$.defaultLocationId')
                WHERE `Key` = 'weather-preferences'
                  AND JSON_VALID(`PreferencesJson`)
                  AND JSON_CONTAINS_PATH(`PreferencesJson`, 'one', '$.defaultLocationId');
                """);

            // 8. The per-location weather row is gone; its points carry everything it held.
            migrationBuilder.DropTable(name: "WeatherLocations");
        }

        private static void AddPointKey(MigrationBuilder migrationBuilder, string table, ReferentialAction onDelete) =>
            migrationBuilder.AddForeignKey(
                name: $"FK_{table}_WeatherPoints_WeatherPointId",
                table: table,
                column: "WeatherPointId",
                principalTable: "WeatherPoints",
                principalColumn: "WeatherPointId",
                onDelete: onDelete);

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Schema only (§11 Operations): undoing this migration means restoring the pre-deploy backup. The
            // re-keyed rows cannot take the old location keys (a point created after the upgrade names no
            // location), so every weather data row and every place's link is removed first.
            migrationBuilder.Sql("UPDATE `Locations` SET `WeatherPointId` = NULL;");
            foreach (var table in new[] { "WeatherForecastSteps", "WeatherForecastRuns", "WeatherObservations", "WeatherDays", "WeatherStations", "WeatherPointSchedules", "WeatherPointSites" })
            {
                migrationBuilder.Sql($"DELETE FROM `{table}`;");
            }

            migrationBuilder.Sql("DELETE FROM `WeatherPoints`;");
            foreach (var (oldKey, newKey) in RenamedSettings)
            {
                migrationBuilder.Sql($"UPDATE `SystemSettings` SET `Key` = '{oldKey}' WHERE `Key` = '{newKey}';");
            }

            migrationBuilder.DropForeignKey(
                name: "FK_WeatherDays_WeatherPoints_WeatherPointId",
                table: "WeatherDays");

            migrationBuilder.DropForeignKey(
                name: "FK_WeatherForecastRuns_WeatherPoints_WeatherPointId",
                table: "WeatherForecastRuns");

            migrationBuilder.DropForeignKey(
                name: "FK_WeatherObservations_WeatherPoints_WeatherPointId",
                table: "WeatherObservations");

            migrationBuilder.DropForeignKey(
                name: "FK_WeatherStations_WeatherPoints_WeatherPointId",
                table: "WeatherStations");

            migrationBuilder.DropTable(
                name: "WeatherPointSchedules");

            migrationBuilder.DropTable(
                name: "WeatherPointSites");

            migrationBuilder.DropPrimaryKey(
                name: "PK_WeatherStations",
                table: "WeatherStations");

            migrationBuilder.DropIndex(
                name: "IX_WeatherObservations_WeatherPointId_LocalDate_SiteVersion_Fet~",
                table: "WeatherObservations");

            migrationBuilder.DropIndex(
                name: "IX_WeatherForecastSteps_WeatherPointId_TimeUtc_SiteVersion_RunP~",
                table: "WeatherForecastSteps");

            migrationBuilder.DropIndex(
                name: "IX_WeatherForecastRuns_WeatherPointId_SiteVersion_ProviderUpdat~",
                table: "WeatherForecastRuns");

            migrationBuilder.DropPrimaryKey(
                name: "PK_WeatherDays",
                table: "WeatherDays");

            migrationBuilder.AlterColumn<Guid>(
                name: "WeatherPointId",
                table: "WeatherStations",
                type: "char(36)",
                nullable: true,
                collation: "ascii_general_ci",
                oldClrType: typeof(Guid),
                oldType: "char(36)")
                .OldAnnotation("Relational:Collation", "ascii_general_ci");

            migrationBuilder.AddColumn<Guid>(
                name: "LocationId",
                table: "WeatherStations",
                type: "char(36)",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                collation: "ascii_general_ci");

            migrationBuilder.AlterColumn<Guid>(
                name: "WeatherPointId",
                table: "WeatherObservations",
                type: "char(36)",
                nullable: true,
                collation: "ascii_general_ci",
                oldClrType: typeof(Guid),
                oldType: "char(36)")
                .OldAnnotation("Relational:Collation", "ascii_general_ci");

            migrationBuilder.AddColumn<Guid>(
                name: "LocationId",
                table: "WeatherObservations",
                type: "char(36)",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                collation: "ascii_general_ci");

            migrationBuilder.AlterColumn<Guid>(
                name: "WeatherPointId",
                table: "WeatherForecastSteps",
                type: "char(36)",
                nullable: true,
                collation: "ascii_general_ci",
                oldClrType: typeof(Guid),
                oldType: "char(36)")
                .OldAnnotation("Relational:Collation", "ascii_general_ci");

            migrationBuilder.AddColumn<Guid>(
                name: "LocationId",
                table: "WeatherForecastSteps",
                type: "char(36)",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                collation: "ascii_general_ci");

            migrationBuilder.AlterColumn<Guid>(
                name: "WeatherPointId",
                table: "WeatherForecastRuns",
                type: "char(36)",
                nullable: true,
                collation: "ascii_general_ci",
                oldClrType: typeof(Guid),
                oldType: "char(36)")
                .OldAnnotation("Relational:Collation", "ascii_general_ci");

            migrationBuilder.AddColumn<Guid>(
                name: "LocationId",
                table: "WeatherForecastRuns",
                type: "char(36)",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                collation: "ascii_general_ci");

            migrationBuilder.AlterColumn<Guid>(
                name: "WeatherPointId",
                table: "WeatherDays",
                type: "char(36)",
                nullable: true,
                collation: "ascii_general_ci",
                oldClrType: typeof(Guid),
                oldType: "char(36)")
                .OldAnnotation("Relational:Collation", "ascii_general_ci");

            migrationBuilder.AddColumn<Guid>(
                name: "LocationId",
                table: "WeatherDays",
                type: "char(36)",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                collation: "ascii_general_ci");

            migrationBuilder.AddPrimaryKey(
                name: "PK_WeatherStations",
                table: "WeatherStations",
                columns: new[] { "LocationId", "Group" });

            migrationBuilder.AddPrimaryKey(
                name: "PK_WeatherDays",
                table: "WeatherDays",
                columns: new[] { "LocationId", "LocalDate" });

            migrationBuilder.CreateTable(
                name: "WeatherLocationSites",
                columns: table => new
                {
                    LocationId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    SiteVersion = table.Column<int>(type: "int", nullable: false),
                    AltitudeMeters = table.Column<int>(type: "int", nullable: true),
                    AppliedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    AppliedByUserId = table.Column<string>(type: "varchar(255)", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Latitude = table.Column<decimal>(type: "decimal(6,3)", precision: 6, scale: 3, nullable: false),
                    Longitude = table.Column<decimal>(type: "decimal(7,3)", precision: 7, scale: 3, nullable: false),
                    TimeZoneId = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    WeatherPointId = table.Column<Guid>(type: "char(36)", nullable: true, collation: "ascii_general_ci")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WeatherLocationSites", x => new { x.LocationId, x.SiteVersion });
                    table.CheckConstraint("CK_WeatherLocationSites_VersionPositive", "`SiteVersion` >= 1");
                    table.ForeignKey(
                        name: "FK_WeatherLocationSites_AspNetUsers_AppliedByUserId",
                        column: x => x.AppliedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_WeatherLocationSites_Locations_LocationId",
                        column: x => x.LocationId,
                        principalTable: "Locations",
                        principalColumn: "LocationId",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "WeatherLocations",
                columns: table => new
                {
                    LocationId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    CreatedByUserId = table.Column<string>(type: "varchar(255)", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    CurrentSiteVersion = table.Column<int>(type: "int", nullable: false),
                    EnabledAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    ForecastEnabled = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    HistoryEnabled = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    HistoryFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    IsTracked = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    PurgeStartedAt = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    StationResolvedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    UpdatedByUserId = table.Column<string>(type: "varchar(255)", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WeatherLocations", x => x.LocationId);
                    table.CheckConstraint("CK_WeatherLocations_CurrentSiteVersionPositive", "`CurrentSiteVersion` >= 1");
                    table.ForeignKey(
                        name: "FK_WeatherLocations_AspNetUsers_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_WeatherLocations_AspNetUsers_UpdatedByUserId",
                        column: x => x.UpdatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_WeatherLocations_Locations_LocationId",
                        column: x => x.LocationId,
                        principalTable: "Locations",
                        principalColumn: "LocationId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WeatherLocations_WeatherLocationSites_LocationId_CurrentSite~",
                        columns: x => new { x.LocationId, x.CurrentSiteVersion },
                        principalTable: "WeatherLocationSites",
                        principalColumns: new[] { "LocationId", "SiteVersion" },
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "WeatherLocationSchedules",
                columns: table => new
                {
                    LocationId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    ArchivedSkipLogged = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    ForecastConsecutiveFailures = table.Column<int>(type: "int", nullable: false),
                    ForecastExpiresAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    ForecastFailureKind = table.Column<int>(type: "int", nullable: true),
                    ForecastLastModified = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    LastForecastSuccessUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    LastObservationSuccessUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    LeaseUntilUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    NextForecastFetchAfterUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    NextObservationFetchAfterUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    ObservationConsecutiveFailures = table.Column<int>(type: "int", nullable: false),
                    ObservationFailureKind = table.Column<int>(type: "int", nullable: true),
                    ObservationsBackfilledFromDate = table.Column<DateOnly>(type: "date", nullable: true),
                    ObservationsCheckedThroughDate = table.Column<DateOnly>(type: "date", nullable: true),
                    Version = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    WeatherPointId = table.Column<Guid>(type: "char(36)", nullable: true, collation: "ascii_general_ci")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WeatherLocationSchedules", x => x.LocationId);
                    table.ForeignKey(
                        name: "FK_WeatherLocationSchedules_WeatherLocations_LocationId",
                        column: x => x.LocationId,
                        principalTable: "WeatherLocations",
                        principalColumn: "LocationId",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_WeatherObservations_LocationId_LocalDate_SiteVersion_Fetched~",
                table: "WeatherObservations",
                columns: new[] { "LocationId", "LocalDate", "SiteVersion", "FetchedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_WeatherForecastSteps_LocationId_TimeUtc_SiteVersion_RunProvi~",
                table: "WeatherForecastSteps",
                columns: new[] { "LocationId", "TimeUtc", "SiteVersion", "RunProviderUpdatedAt", "RunFetchedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_WeatherForecastRuns_LocationId_SiteVersion_ProviderUpdatedAt",
                table: "WeatherForecastRuns",
                columns: new[] { "LocationId", "SiteVersion", "ProviderUpdatedAt" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WeatherLocations_CreatedByUserId",
                table: "WeatherLocations",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_WeatherLocations_LocationId_CurrentSiteVersion",
                table: "WeatherLocations",
                columns: new[] { "LocationId", "CurrentSiteVersion" });

            migrationBuilder.CreateIndex(
                name: "IX_WeatherLocations_UpdatedByUserId",
                table: "WeatherLocations",
                column: "UpdatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_WeatherLocationSites_AppliedByUserId",
                table: "WeatherLocationSites",
                column: "AppliedByUserId");

            migrationBuilder.AddForeignKey(
                name: "FK_WeatherDays_Locations_LocationId",
                table: "WeatherDays",
                column: "LocationId",
                principalTable: "Locations",
                principalColumn: "LocationId",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_WeatherForecastRuns_Locations_LocationId",
                table: "WeatherForecastRuns",
                column: "LocationId",
                principalTable: "Locations",
                principalColumn: "LocationId",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_WeatherObservations_Locations_LocationId",
                table: "WeatherObservations",
                column: "LocationId",
                principalTable: "Locations",
                principalColumn: "LocationId",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_WeatherStations_WeatherLocations_LocationId",
                table: "WeatherStations",
                column: "LocationId",
                principalTable: "WeatherLocations",
                principalColumn: "LocationId",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
