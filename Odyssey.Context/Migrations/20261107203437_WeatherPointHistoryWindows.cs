using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Odyssey.Context.Migrations
{
    /// <inheritdoc />
    public partial class WeatherPointHistoryWindows : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WeatherObservationCoverages",
                columns: table => new
                {
                    WeatherPointId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    SiteVersion = table.Column<int>(type: "int", nullable: false),
                    FromDate = table.Column<DateOnly>(type: "date", nullable: false),
                    ToDate = table.Column<DateOnly>(type: "date", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WeatherObservationCoverages", x => new { x.WeatherPointId, x.SiteVersion, x.FromDate });
                    table.CheckConstraint("CK_WeatherObservationCoverages_Order", "`ToDate` >= `FromDate`");
                    table.ForeignKey(
                        name: "FK_WeatherObservationCoverages_WeatherPoints_WeatherPointId",
                        column: x => x.WeatherPointId,
                        principalTable: "WeatherPoints",
                        principalColumn: "WeatherPointId",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "WeatherPointHistoryWindows",
                columns: table => new
                {
                    WeatherPointHistoryWindowId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    WeatherPointId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    FromDate = table.Column<DateOnly>(type: "date", nullable: false),
                    ToDate = table.Column<DateOnly>(type: "date", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WeatherPointHistoryWindows", x => x.WeatherPointHistoryWindowId);
                    table.CheckConstraint("CK_WeatherPointHistoryWindows_Order", "`ToDate` IS NULL OR `ToDate` >= `FromDate`");
                    table.ForeignKey(
                        name: "FK_WeatherPointHistoryWindows_WeatherPoints_WeatherPointId",
                        column: x => x.WeatherPointId,
                        principalTable: "WeatherPoints",
                        principalColumn: "WeatherPointId",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_WeatherPointHistoryWindows_WeatherPointId_FromDate",
                table: "WeatherPointHistoryWindows",
                columns: new[] { "WeatherPointId", "FromDate" },
                unique: true);

            // Data (issue #436 §11), in this order — create, insert, drop — so an interruption between insert and drop is
            // caught by MigrationRunner's replay guard. Every point, history on or off, gets one ongoing window at its old
            // HistoryFrom, so turning history on later behaves as it did. UUID() matches Pomelo's char(36) Guid.
            migrationBuilder.Sql("""
                INSERT INTO `WeatherPointHistoryWindows` (`WeatherPointHistoryWindowId`, `WeatherPointId`, `FromDate`, `ToDate`)
                SELECT UUID(), `WeatherPointId`, `HistoryFrom`, NULL FROM `WeatherPoints`;
                """);

            // What the two old watermarks said was checked, for the current site, so no point re-downloads its history.
            // Assumes the old range was contiguous; a hole the old daily path never revisited is claimed too (accepted
            // residual, release notes).
            migrationBuilder.Sql("""
                INSERT INTO `WeatherObservationCoverages` (`WeatherPointId`, `SiteVersion`, `FromDate`, `ToDate`)
                SELECT s.`WeatherPointId`, p.`CurrentSiteVersion`, s.`ObservationsBackfilledFromDate`, s.`ObservationsCheckedThroughDate`
                FROM `WeatherPointSchedules` s
                JOIN `WeatherPoints` p ON p.`WeatherPointId` = s.`WeatherPointId`
                WHERE s.`ObservationsBackfilledFromDate` IS NOT NULL
                  AND s.`ObservationsCheckedThroughDate` IS NOT NULL
                  AND s.`ObservationsBackfilledFromDate` <= s.`ObservationsCheckedThroughDate`;
                """);

            migrationBuilder.DropColumn(
                name: "ObservationsBackfilledFromDate",
                table: "WeatherPointSchedules");

            migrationBuilder.DropColumn(
                name: "HistoryFrom",
                table: "WeatherPoints");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "ObservationsBackfilledFromDate",
                table: "WeatherPointSchedules",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "HistoryFrom",
                table: "WeatherPoints",
                type: "date",
                nullable: false,
                defaultValue: new DateOnly(1, 1, 1));

            // Down() restores schema, and only one window per point: the earliest start becomes HistoryFrom. Every other
            // window is lost — undo this migration by restoring a backup (release notes).
            migrationBuilder.Sql("""
                UPDATE `WeatherPoints` p
                SET p.`HistoryFrom` = COALESCE(
                    (SELECT MIN(w.`FromDate`) FROM `WeatherPointHistoryWindows` w WHERE w.`WeatherPointId` = p.`WeatherPointId`),
                    DATE_SUB(DATE(p.`CreatedAt`), INTERVAL 1 YEAR));
                """);

            migrationBuilder.Sql("""
                UPDATE `WeatherPointSchedules` s
                JOIN `WeatherPoints` p ON p.`WeatherPointId` = s.`WeatherPointId`
                SET s.`ObservationsBackfilledFromDate` = (
                    SELECT MIN(c.`FromDate`) FROM `WeatherObservationCoverages` c
                    WHERE c.`WeatherPointId` = s.`WeatherPointId` AND c.`SiteVersion` = p.`CurrentSiteVersion`);
                """);

            migrationBuilder.DropTable(
                name: "WeatherObservationCoverages");

            migrationBuilder.DropTable(
                name: "WeatherPointHistoryWindows");
        }
    }
}
