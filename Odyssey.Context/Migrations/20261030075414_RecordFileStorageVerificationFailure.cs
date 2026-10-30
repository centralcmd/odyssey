using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Odyssey.Context.Migrations
{
    /// <inheritdoc />
    public partial class RecordFileStorageVerificationFailure : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "VerificationFailedAtUtc",
                table: "FileStorageMaintenanceLeases",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VerificationFailedCheck",
                table: "FileStorageMaintenanceLeases",
                type: "varchar(64)",
                maxLength: 64,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.UpdateData(
                table: "FileStorageMaintenanceLeases",
                keyColumn: "Id",
                keyValue: (byte)1,
                columns: new[] { "VerificationFailedAtUtc", "VerificationFailedCheck" },
                values: new object[] { null, null });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "VerificationFailedAtUtc",
                table: "FileStorageMaintenanceLeases");

            migrationBuilder.DropColumn(
                name: "VerificationFailedCheck",
                table: "FileStorageMaintenanceLeases");
        }
    }
}
