using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Odyssey.Context.Migrations
{
    /// <inheritdoc />
    public partial class WeatherStationPerMeasure : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Measured history starts over. The old rows took every measure from one station chosen for
            // temperature, which is exactly what this change replaces, and every place must look its stations up
            // again per measure. Weather has not shipped, so no deployed database holds rows this discards; the
            // forecast half is untouched. Down() restores the columns, not these rows.
            migrationBuilder.Sql("DELETE FROM `WeatherObservations`;");
            migrationBuilder.Sql("DELETE FROM `WeatherDays` WHERE `ForecastCoverageHours` IS NULL;");
            migrationBuilder.Sql(
                "UPDATE `WeatherDays` SET `ObservedMinTempC` = NULL, `ObservedMaxTempC` = NULL, `ObservedMeanTempC` = NULL, " +
                "`ObservedMeanWindSpeedMps` = NULL, `ObservedMaxWindGustMps` = NULL, `ObservedPrecipitationMm` = NULL, " +
                "`ObservedFetchedAtUtc` = NULL, `ObservedSiteVersion` = NULL;");
            migrationBuilder.Sql("UPDATE `WeatherLocations` SET `StationResolvedAtUtc` = NULL;");
            migrationBuilder.Sql(
                "UPDATE `WeatherLocationSchedules` SET `ObservationsCheckedThroughDate` = NULL, `ObservationsBackfilledFromDate` = NULL, " +
                "`LastObservationSuccessUtc` = NULL, `ObservationFailureKind` = NULL, `ObservationConsecutiveFailures` = 0, " +
                "`NextObservationFetchAfterUtc` = NULL;");

            migrationBuilder.DropColumn(
                name: "StationId",
                table: "WeatherObservations");

            migrationBuilder.DropColumn(
                name: "FrostStationId",
                table: "WeatherLocations");

            migrationBuilder.AddColumn<string>(
                name: "PrecipitationStationId",
                table: "WeatherObservations",
                type: "varchar(32)",
                maxLength: 32,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "TemperatureStationId",
                table: "WeatherObservations",
                type: "varchar(32)",
                maxLength: 32,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "WindStationId",
                table: "WeatherObservations",
                type: "varchar(32)",
                maxLength: 32,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "WeatherStations",
                columns: table => new
                {
                    LocationId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    Group = table.Column<int>(type: "int", nullable: false),
                    StationId = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Name = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    DistanceKm = table.Column<decimal>(type: "decimal(5,1)", precision: 5, scale: 1, nullable: true),
                    AltitudeMeters = table.Column<int>(type: "int", nullable: true),
                    ResolvedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WeatherStations", x => new { x.LocationId, x.Group });
                    table.CheckConstraint("CK_WeatherStations_GroupKnown", "`Group` IN (0, 1, 2)");
                    table.ForeignKey(
                        name: "FK_WeatherStations_WeatherLocations_LocationId",
                        column: x => x.LocationId,
                        principalTable: "WeatherLocations",
                        principalColumn: "LocationId",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WeatherStations");

            migrationBuilder.DropColumn(
                name: "PrecipitationStationId",
                table: "WeatherObservations");

            migrationBuilder.DropColumn(
                name: "TemperatureStationId",
                table: "WeatherObservations");

            migrationBuilder.DropColumn(
                name: "WindStationId",
                table: "WeatherObservations");

            migrationBuilder.AddColumn<string>(
                name: "StationId",
                table: "WeatherObservations",
                type: "varchar(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "FrostStationId",
                table: "WeatherLocations",
                type: "varchar(32)",
                maxLength: 32,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");
        }
    }
}
