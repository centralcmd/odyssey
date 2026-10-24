using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Odyssey.Context.Migrations
{
    /// <inheritdoc />
    public partial class AddWeather : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "UserWeatherPreferences",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    DefaultLocationId = table.Column<Guid>(type: "char(36)", nullable: true, collation: "ascii_general_ci"),
                    UpdatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserWeatherPreferences", x => x.UserId);
                    table.ForeignKey(
                        name: "FK_UserWeatherPreferences_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_UserWeatherPreferences_Locations_DefaultLocationId",
                        column: x => x.DefaultLocationId,
                        principalTable: "Locations",
                        principalColumn: "LocationId",
                        onDelete: ReferentialAction.SetNull);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "WeatherDays",
                columns: table => new
                {
                    LocationId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    LocalDate = table.Column<DateOnly>(type: "date", nullable: false),
                    ForecastSymbolCode = table.Column<string>(type: "varchar(40)", maxLength: 40, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ForecastMinTempC = table.Column<decimal>(type: "decimal(4,1)", precision: 4, scale: 1, nullable: true),
                    ForecastMaxTempC = table.Column<decimal>(type: "decimal(4,1)", precision: 4, scale: 1, nullable: true),
                    ForecastMeanWindSpeedMps = table.Column<decimal>(type: "decimal(4,1)", precision: 4, scale: 1, nullable: true),
                    ForecastMaxWindGustMps = table.Column<decimal>(type: "decimal(4,1)", precision: 4, scale: 1, nullable: true),
                    ForecastPrecipitationMm = table.Column<decimal>(type: "decimal(5,1)", precision: 5, scale: 1, nullable: true),
                    ForecastCoverageHours = table.Column<int>(type: "int", nullable: true),
                    ForecastIssuedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    ForecastSiteVersion = table.Column<int>(type: "int", nullable: true),
                    ObservedMinTempC = table.Column<decimal>(type: "decimal(4,1)", precision: 4, scale: 1, nullable: true),
                    ObservedMaxTempC = table.Column<decimal>(type: "decimal(4,1)", precision: 4, scale: 1, nullable: true),
                    ObservedMeanTempC = table.Column<decimal>(type: "decimal(4,1)", precision: 4, scale: 1, nullable: true),
                    ObservedMeanWindSpeedMps = table.Column<decimal>(type: "decimal(4,1)", precision: 4, scale: 1, nullable: true),
                    ObservedMaxWindGustMps = table.Column<decimal>(type: "decimal(4,1)", precision: 4, scale: 1, nullable: true),
                    ObservedPrecipitationMm = table.Column<decimal>(type: "decimal(5,1)", precision: 5, scale: 1, nullable: true),
                    ObservedFetchedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    ObservedSiteVersion = table.Column<int>(type: "int", nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WeatherDays", x => new { x.LocationId, x.LocalDate });
                    table.CheckConstraint("CK_WeatherDays_HasData", "`ForecastCoverageHours` IS NOT NULL OR `ObservedFetchedAtUtc` IS NOT NULL");
                    table.ForeignKey(
                        name: "FK_WeatherDays_Locations_LocationId",
                        column: x => x.LocationId,
                        principalTable: "Locations",
                        principalColumn: "LocationId",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "WeatherForecastRuns",
                columns: table => new
                {
                    WeatherForecastRunId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    LocationId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    SiteVersion = table.Column<int>(type: "int", nullable: false),
                    Provider = table.Column<int>(type: "int", nullable: false),
                    ProviderUpdatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    FetchedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    Latitude = table.Column<decimal>(type: "decimal(6,3)", precision: 6, scale: 3, nullable: false),
                    Longitude = table.Column<decimal>(type: "decimal(7,3)", precision: 7, scale: 3, nullable: false),
                    AltitudeMeters = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WeatherForecastRuns", x => x.WeatherForecastRunId);
                    table.CheckConstraint("CK_WeatherForecastRuns_ProviderKnown", "`Provider` IN (1)");
                    table.ForeignKey(
                        name: "FK_WeatherForecastRuns_Locations_LocationId",
                        column: x => x.LocationId,
                        principalTable: "Locations",
                        principalColumn: "LocationId",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "WeatherLocationSites",
                columns: table => new
                {
                    LocationId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    SiteVersion = table.Column<int>(type: "int", nullable: false),
                    Latitude = table.Column<decimal>(type: "decimal(6,3)", precision: 6, scale: 3, nullable: false),
                    Longitude = table.Column<decimal>(type: "decimal(7,3)", precision: 7, scale: 3, nullable: false),
                    AltitudeMeters = table.Column<int>(type: "int", nullable: true),
                    TimeZoneId = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    AppliedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    AppliedByUserId = table.Column<string>(type: "varchar(255)", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4")
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
                name: "WeatherObservations",
                columns: table => new
                {
                    WeatherObservationId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    LocationId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    SiteVersion = table.Column<int>(type: "int", nullable: false),
                    Provider = table.Column<int>(type: "int", nullable: false),
                    StationId = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    LocalDate = table.Column<DateOnly>(type: "date", nullable: false),
                    MeanTempC = table.Column<decimal>(type: "decimal(4,1)", precision: 4, scale: 1, nullable: true),
                    MinTempC = table.Column<decimal>(type: "decimal(4,1)", precision: 4, scale: 1, nullable: true),
                    MaxTempC = table.Column<decimal>(type: "decimal(4,1)", precision: 4, scale: 1, nullable: true),
                    MeanWindSpeedMps = table.Column<decimal>(type: "decimal(4,1)", precision: 4, scale: 1, nullable: true),
                    MaxWindGustMps = table.Column<decimal>(type: "decimal(4,1)", precision: 4, scale: 1, nullable: true),
                    PrecipitationMm = table.Column<decimal>(type: "decimal(5,1)", precision: 5, scale: 1, nullable: true),
                    FetchedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WeatherObservations", x => x.WeatherObservationId);
                    table.ForeignKey(
                        name: "FK_WeatherObservations_Locations_LocationId",
                        column: x => x.LocationId,
                        principalTable: "Locations",
                        principalColumn: "LocationId",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "WeatherForecastSteps",
                columns: table => new
                {
                    WeatherForecastStepId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    WeatherForecastRunId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    LocationId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    SiteVersion = table.Column<int>(type: "int", nullable: false),
                    RunProviderUpdatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    RunFetchedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    TimeUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    PeriodHours = table.Column<byte>(type: "tinyint unsigned", nullable: false),
                    AirTemperatureC = table.Column<decimal>(type: "decimal(4,1)", precision: 4, scale: 1, nullable: true),
                    RelativeHumidityPct = table.Column<decimal>(type: "decimal(4,1)", precision: 4, scale: 1, nullable: true),
                    CloudAreaFractionPct = table.Column<decimal>(type: "decimal(4,1)", precision: 4, scale: 1, nullable: true),
                    AirPressureHpa = table.Column<decimal>(type: "decimal(5,1)", precision: 5, scale: 1, nullable: true),
                    WindSpeedMps = table.Column<decimal>(type: "decimal(4,1)", precision: 4, scale: 1, nullable: true),
                    WindGustMps = table.Column<decimal>(type: "decimal(4,1)", precision: 4, scale: 1, nullable: true),
                    WindFromDirectionDeg = table.Column<decimal>(type: "decimal(4,1)", precision: 4, scale: 1, nullable: true),
                    UvIndexClearSky = table.Column<decimal>(type: "decimal(3,1)", precision: 3, scale: 1, nullable: true),
                    SymbolCode = table.Column<string>(type: "varchar(40)", maxLength: 40, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    PrecipitationMm = table.Column<decimal>(type: "decimal(5,1)", precision: 5, scale: 1, nullable: true),
                    PrecipitationProbabilityPct = table.Column<decimal>(type: "decimal(4,1)", precision: 4, scale: 1, nullable: true),
                    TempMinC = table.Column<decimal>(type: "decimal(4,1)", precision: 4, scale: 1, nullable: true),
                    TempMaxC = table.Column<decimal>(type: "decimal(4,1)", precision: 4, scale: 1, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WeatherForecastSteps", x => x.WeatherForecastStepId);
                    table.ForeignKey(
                        name: "FK_WeatherForecastSteps_WeatherForecastRuns_WeatherForecastRunId",
                        column: x => x.WeatherForecastRunId,
                        principalTable: "WeatherForecastRuns",
                        principalColumn: "WeatherForecastRunId",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "WeatherLocations",
                columns: table => new
                {
                    LocationId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    Enabled = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    IsTracked = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    HistoryFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    EnabledAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    CurrentSiteVersion = table.Column<int>(type: "int", nullable: false),
                    PurgeStartedAt = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    FrostStationId = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    StationResolvedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    CreatedByUserId = table.Column<string>(type: "varchar(255)", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    UpdatedByUserId = table.Column<string>(type: "varchar(255)", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
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
                    Version = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    LeaseUntilUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    NextForecastFetchAfterUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    ForecastLastModified = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ForecastExpiresAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    LastForecastSuccessUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    ForecastFailureKind = table.Column<int>(type: "int", nullable: true),
                    ForecastConsecutiveFailures = table.Column<int>(type: "int", nullable: false),
                    NextObservationFetchAfterUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    LastObservationSuccessUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    ObservationsCheckedThroughDate = table.Column<DateOnly>(type: "date", nullable: true),
                    ObservationsBackfilledFromDate = table.Column<DateOnly>(type: "date", nullable: true),
                    ObservationFailureKind = table.Column<int>(type: "int", nullable: true),
                    ObservationConsecutiveFailures = table.Column<int>(type: "int", nullable: false),
                    ArchivedSkipLogged = table.Column<bool>(type: "tinyint(1)", nullable: false)
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

            migrationBuilder.InsertData(
                table: "SystemSettings",
                columns: new[] { "Key", "UpdatedAt", "UpdatedBy", "Value" },
                values: new object[,]
                {
                    { "WeatherDefaultLocationId", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "" },
                    { "WeatherMaxTrackedLocations", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "5" },
                    { "WeatherRefreshIntervalMinutes", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "1440" },
                    { "WeatherUserAgentContact", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "" }
                });

            migrationBuilder.CreateIndex(
                name: "IX_UserWeatherPreferences_DefaultLocationId",
                table: "UserWeatherPreferences",
                column: "DefaultLocationId");

            migrationBuilder.CreateIndex(
                name: "IX_WeatherForecastRuns_LocationId_SiteVersion_ProviderUpdatedAt",
                table: "WeatherForecastRuns",
                columns: new[] { "LocationId", "SiteVersion", "ProviderUpdatedAt" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WeatherForecastSteps_LocationId_TimeUtc_SiteVersion_RunProvi~",
                table: "WeatherForecastSteps",
                columns: new[] { "LocationId", "TimeUtc", "SiteVersion", "RunProviderUpdatedAt", "RunFetchedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_WeatherForecastSteps_WeatherForecastRunId_TimeUtc",
                table: "WeatherForecastSteps",
                columns: new[] { "WeatherForecastRunId", "TimeUtc" },
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

            migrationBuilder.CreateIndex(
                name: "IX_WeatherObservations_LocationId_LocalDate_SiteVersion_Fetched~",
                table: "WeatherObservations",
                columns: new[] { "LocationId", "LocalDate", "SiteVersion", "FetchedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UserWeatherPreferences");

            migrationBuilder.DropTable(
                name: "WeatherDays");

            migrationBuilder.DropTable(
                name: "WeatherForecastSteps");

            migrationBuilder.DropTable(
                name: "WeatherLocationSchedules");

            migrationBuilder.DropTable(
                name: "WeatherObservations");

            migrationBuilder.DropTable(
                name: "WeatherForecastRuns");

            migrationBuilder.DropTable(
                name: "WeatherLocations");

            migrationBuilder.DropTable(
                name: "WeatherLocationSites");

            migrationBuilder.DeleteData(
                table: "SystemSettings",
                keyColumn: "Key",
                keyValue: "WeatherDefaultLocationId");

            migrationBuilder.DeleteData(
                table: "SystemSettings",
                keyColumn: "Key",
                keyValue: "WeatherMaxTrackedLocations");

            migrationBuilder.DeleteData(
                table: "SystemSettings",
                keyColumn: "Key",
                keyValue: "WeatherRefreshIntervalMinutes");

            migrationBuilder.DeleteData(
                table: "SystemSettings",
                keyColumn: "Key",
                keyValue: "WeatherUserAgentContact");
        }
    }
}
