using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Odyssey.Context.Migrations
{
    /// <summary>
    /// Raises the journal VJOURNAL import/export defaults to match the task surface: no count limit, and 10240 MB
    /// (the new ceiling) on size, because a journal file now carries its attached files and photos inline. Only a row
    /// no administrator has saved is moved (<c>UpdatedBy IS NULL</c>) — comparing values could not tell "never
    /// touched" from "deliberately set back to the default". The <c>HasData</c> seed carries the same values, which is
    /// what the scaffolded snapshot records.
    /// </summary>
    public partial class RaiseJournalIcsLimits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            Move(migrationBuilder, "JournalIcsMaxExportMegabytes", "64", "10240");
            Move(migrationBuilder, "JournalIcsMaxImportMegabytes", "64", "10240");
            Move(migrationBuilder, "JournalIcsMaxExportRows", "2000", "unlimited");
            Move(migrationBuilder, "JournalIcsMaxImportEntries", "2000", "unlimited");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            Move(migrationBuilder, "JournalIcsMaxExportMegabytes", "10240", "64");
            Move(migrationBuilder, "JournalIcsMaxImportMegabytes", "10240", "64");
            Move(migrationBuilder, "JournalIcsMaxExportRows", "unlimited", "2000");
            Move(migrationBuilder, "JournalIcsMaxImportEntries", "unlimited", "2000");
        }

        private static void Move(MigrationBuilder migrationBuilder, string key, string from, string to) =>
            migrationBuilder.Sql(
                $"UPDATE `SystemSettings` SET `Value` = '{to}' " +
                $"WHERE `Key` = '{key}' AND `UpdatedBy` IS NULL AND `Value` = '{from}';");
    }
}
