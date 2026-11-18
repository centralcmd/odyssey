using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Odyssey.Context.Migrations
{
    /// <summary>
    /// Issue #516. Adds <c>ImportSourceUid</c> and its <c>(CalendarId, ImportSourceUid)</c> index to
    /// <c>CalendarEvents</c> and <c>RecurrencePatterns</c> (both nullable, no backfill), and raises the calendar
    /// <c>.ics</c> defaults to match the task and journal surfaces: no count limit, and 10240 MB on size, because the
    /// calendar export and import now stream. Only a row no administrator has saved is moved (<c>UpdatedBy IS NULL</c>)
    /// and only while it still holds the old default, so a hand-edited row is left alone too. <c>Down()</c> restores
    /// the settings rows still at the new default and drops the columns: rows created from another calendar's export
    /// then lose the match key a re-import relies on.
    /// </summary>
    public partial class CalendarIcsStreamingAndLimits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ImportSourceUid",
                table: "RecurrencePatterns",
                type: "varchar(255)",
                maxLength: 255,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "ImportSourceUid",
                table: "CalendarEvents",
                type: "varchar(255)",
                maxLength: 255,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_RecurrencePatterns_CalendarId_ImportSourceUid",
                table: "RecurrencePatterns",
                columns: new[] { "CalendarId", "ImportSourceUid" });

            migrationBuilder.CreateIndex(
                name: "IX_CalendarEvents_CalendarId_ImportSourceUid",
                table: "CalendarEvents",
                columns: new[] { "CalendarId", "ImportSourceUid" });

            Move(migrationBuilder, "CalendarIcsMaxExportMegabytes", "64", "10240");
            Move(migrationBuilder, "CalendarIcsMaxImportMegabytes", "64", "10240");
            Move(migrationBuilder, "CalendarIcsMaxExportEvents", "2000", "unlimited");
            Move(migrationBuilder, "CalendarIcsMaxImportEvents", "2000", "unlimited");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_RecurrencePatterns_CalendarId_ImportSourceUid",
                table: "RecurrencePatterns");

            migrationBuilder.DropIndex(
                name: "IX_CalendarEvents_CalendarId_ImportSourceUid",
                table: "CalendarEvents");

            migrationBuilder.DropColumn(
                name: "ImportSourceUid",
                table: "RecurrencePatterns");

            migrationBuilder.DropColumn(
                name: "ImportSourceUid",
                table: "CalendarEvents");

            Move(migrationBuilder, "CalendarIcsMaxExportMegabytes", "10240", "64");
            Move(migrationBuilder, "CalendarIcsMaxImportMegabytes", "10240", "64");
            Move(migrationBuilder, "CalendarIcsMaxExportEvents", "unlimited", "2000");
            Move(migrationBuilder, "CalendarIcsMaxImportEvents", "unlimited", "2000");
        }

        private static void Move(MigrationBuilder migrationBuilder, string key, string from, string to) =>
            migrationBuilder.Sql(
                $"UPDATE `SystemSettings` SET `Value` = '{to}' " +
                $"WHERE `Key` = '{key}' AND `UpdatedBy` IS NULL AND `Value` = '{from}';");
    }
}
