using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Odyssey.Context.Migrations
{
    /// <summary>
    /// Raises the task VTODO import/export defaults: no count limit, and 10240 MB (the new ceiling) on size, because
    /// a task file now carries its attached files inline. Only a row no administrator has saved is moved
    /// (<c>UpdatedBy IS NULL</c>) — comparing values could not tell "never touched" from "deliberately set back to the
    /// default". The <c>HasData</c> seed carries the same values, which is what the scaffolded snapshot records.
    /// </summary>
    public partial class RaiseTaskIcsLimits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            Move(migrationBuilder, "TaskIcsMaxExportMegabytes", "64", "10240");
            Move(migrationBuilder, "TaskIcsMaxImportMegabytes", "64", "10240");
            Move(migrationBuilder, "TaskIcsMaxExportTasks", "2000", "unlimited");
            Move(migrationBuilder, "TaskIcsMaxImportTasks", "2000", "unlimited");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            Move(migrationBuilder, "TaskIcsMaxExportMegabytes", "10240", "64");
            Move(migrationBuilder, "TaskIcsMaxImportMegabytes", "10240", "64");
            Move(migrationBuilder, "TaskIcsMaxExportTasks", "unlimited", "2000");
            Move(migrationBuilder, "TaskIcsMaxImportTasks", "unlimited", "2000");
        }

        private static void Move(MigrationBuilder migrationBuilder, string key, string from, string to) =>
            migrationBuilder.Sql(
                $"UPDATE `SystemSettings` SET `Value` = '{to}' " +
                $"WHERE `Key` = '{key}' AND `UpdatedBy` IS NULL AND `Value` = '{from}';");
    }
}
