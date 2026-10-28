using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Odyssey.Context.Migrations
{
    /// <summary>
    /// Splits a location's single weather switch into a forecast half and a historical-data half (issue #348).
    /// Every existing row keeps what it had: both halves take the old <c>Enabled</c>. Written as add, copy, drop
    /// rather than the rename EF scaffolds, so neither half silently inherits the column on its own.
    /// </summary>
    public partial class SplitWeatherForecastAndHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ForecastEnabled",
                table: "WeatherLocations",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "HistoryEnabled",
                table: "WeatherLocations",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.Sql("UPDATE `WeatherLocations` SET `ForecastEnabled` = `Enabled`, `HistoryEnabled` = `Enabled`;");

            migrationBuilder.DropColumn(
                name: "Enabled",
                table: "WeatherLocations");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Enabled",
                table: "WeatherLocations",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            // Weather was "on" when either half was.
            migrationBuilder.Sql("UPDATE `WeatherLocations` SET `Enabled` = (`ForecastEnabled` OR `HistoryEnabled`);");

            migrationBuilder.DropColumn(
                name: "ForecastEnabled",
                table: "WeatherLocations");

            migrationBuilder.DropColumn(
                name: "HistoryEnabled",
                table: "WeatherLocations");
        }
    }
}
