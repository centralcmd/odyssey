using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Odyssey.Context.Migrations
{
    /// <inheritdoc />
    public partial class AddNewsOpmlImportLimits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                table: "SystemSettings",
                columns: new[] { "Key", "UpdatedAt", "UpdatedBy", "Value" },
                values: new object[,]
                {
                    { "NewsOpmlMaxImportEntries", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "100000" },
                    { "NewsOpmlMaxImportMegabytes", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "64" }
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "SystemSettings",
                keyColumn: "Key",
                keyValue: "NewsOpmlMaxImportEntries");

            migrationBuilder.DeleteData(
                table: "SystemSettings",
                keyColumn: "Key",
                keyValue: "NewsOpmlMaxImportMegabytes");
        }
    }
}
