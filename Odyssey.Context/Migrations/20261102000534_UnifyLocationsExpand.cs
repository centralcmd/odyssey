using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Odyssey.Context.Migrations
{
    /// <summary>
    /// Step 1 of 3 of issue #380 §11 (unified locations). Adds the structured address fields, the address
    /// key, the merge forwarder and the coordinate check to <c>Locations</c>; adds the five nullable
    /// <c>LocationId</c> references; and relaxes the old <c>Addresses</c> columns to <c>NULL</c> so the shape
    /// between the steps is consistent.
    /// </summary>
    /// <remarks>
    /// Scaffolded from an intermediate model that still carried the old address columns. Step 2 is the data
    /// conversion (<c>ILocationConversion</c>), which <c>MigrationRunner</c> calls after this migration and
    /// only while <c>UnifyLocationsContract</c> (step 3) is pending. <c>Down()</c> restores the schema only:
    /// rolling back this release means restoring a backup.
    /// </remarks>
    public partial class UnifyLocationsExpand : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Locations_SourceKnown",
                table: "Locations");

            migrationBuilder.AddColumn<Guid>(
                name: "LocationId",
                table: "RecurrencePatterns",
                type: "char(36)",
                nullable: true,
                collation: "ascii_general_ci");

            migrationBuilder.AddColumn<Guid>(
                name: "LocationId",
                table: "RealEstateDetails",
                type: "char(36)",
                nullable: true,
                collation: "ascii_general_ci");

            migrationBuilder.AlterColumn<string>(
                name: "TimeZoneId",
                table: "Locations",
                type: "varchar(64)",
                maxLength: 64,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "varchar(64)",
                oldMaxLength: 64)
                .Annotation("MySql:CharSet", "utf8mb4")
                .OldAnnotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AlterColumn<decimal>(
                name: "Longitude",
                table: "Locations",
                type: "decimal(8,5)",
                precision: 8,
                scale: 5,
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "decimal(8,5)",
                oldPrecision: 8,
                oldScale: 5);

            migrationBuilder.AlterColumn<decimal>(
                name: "Latitude",
                table: "Locations",
                type: "decimal(7,5)",
                precision: 7,
                scale: 5,
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "decimal(7,5)",
                oldPrecision: 7,
                oldScale: 5);

            migrationBuilder.AddColumn<string>(
                name: "AddressKey",
                table: "Locations",
                type: "char(64)",
                maxLength: 64,
                nullable: true,
                collation: "ascii_bin")
                .Annotation("MySql:CharSet", "ascii");

            migrationBuilder.AddColumn<string>(
                name: "City",
                table: "Locations",
                type: "varchar(128)",
                maxLength: 128,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "CountryCode",
                table: "Locations",
                type: "char(2)",
                maxLength: 2,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "Line1",
                table: "Locations",
                type: "varchar(256)",
                maxLength: 256,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "Line2",
                table: "Locations",
                type: "varchar(256)",
                maxLength: 256,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<Guid>(
                name: "MergedIntoLocationId",
                table: "Locations",
                type: "char(36)",
                nullable: true,
                collation: "ascii_general_ci");

            migrationBuilder.AddColumn<string>(
                name: "PostalCode",
                table: "Locations",
                type: "varchar(32)",
                maxLength: 32,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "Region",
                table: "Locations",
                type: "varchar(128)",
                maxLength: 128,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<Guid>(
                name: "LocationId",
                table: "JournalEntries",
                type: "char(36)",
                nullable: true,
                collation: "ascii_general_ci");

            migrationBuilder.AddColumn<Guid>(
                name: "LocationId",
                table: "CalendarEvents",
                type: "char(36)",
                nullable: true,
                collation: "ascii_general_ci");

            migrationBuilder.AlterColumn<string>(
                name: "Line1",
                table: "Addresses",
                type: "varchar(256)",
                maxLength: 256,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "varchar(256)",
                oldMaxLength: 256)
                .Annotation("MySql:CharSet", "utf8mb4")
                .OldAnnotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AlterColumn<string>(
                name: "CountryCode",
                table: "Addresses",
                type: "varchar(2)",
                maxLength: 2,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "varchar(2)",
                oldMaxLength: 2)
                .Annotation("MySql:CharSet", "utf8mb4")
                .OldAnnotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AlterColumn<string>(
                name: "City",
                table: "Addresses",
                type: "varchar(128)",
                maxLength: 128,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "varchar(128)",
                oldMaxLength: 128)
                .Annotation("MySql:CharSet", "utf8mb4")
                .OldAnnotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<Guid>(
                name: "LocationId",
                table: "Addresses",
                type: "char(36)",
                nullable: true,
                collation: "ascii_general_ci");

            migrationBuilder.CreateIndex(
                name: "IX_RecurrencePatterns_LocationId",
                table: "RecurrencePatterns",
                column: "LocationId");

            migrationBuilder.CreateIndex(
                name: "IX_RealEstateDetails_LocationId",
                table: "RealEstateDetails",
                column: "LocationId");

            migrationBuilder.CreateIndex(
                name: "IX_Locations_AddressKey",
                table: "Locations",
                column: "AddressKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Locations_MergedIntoLocationId",
                table: "Locations",
                column: "MergedIntoLocationId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Locations_CoordinatesAllOrNone",
                table: "Locations",
                sql: "(`Latitude` IS NULL AND `Longitude` IS NULL AND `TimeZoneId` IS NULL) OR (`Latitude` IS NOT NULL AND `Longitude` IS NOT NULL AND `TimeZoneId` IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Locations_SourceKnown",
                table: "Locations",
                sql: "`Source` IN (1, 2, 3, 4, 5, 6, 7)");

            migrationBuilder.CreateIndex(
                name: "IX_JournalEntries_LocationId",
                table: "JournalEntries",
                column: "LocationId");

            migrationBuilder.CreateIndex(
                name: "IX_CalendarEvents_LocationId",
                table: "CalendarEvents",
                column: "LocationId");

            migrationBuilder.CreateIndex(
                name: "IX_Addresses_LocationId",
                table: "Addresses",
                column: "LocationId");

            migrationBuilder.AddForeignKey(
                name: "FK_Addresses_Locations_LocationId",
                table: "Addresses",
                column: "LocationId",
                principalTable: "Locations",
                principalColumn: "LocationId",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_CalendarEvents_Locations_LocationId",
                table: "CalendarEvents",
                column: "LocationId",
                principalTable: "Locations",
                principalColumn: "LocationId",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_JournalEntries_Locations_LocationId",
                table: "JournalEntries",
                column: "LocationId",
                principalTable: "Locations",
                principalColumn: "LocationId",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Locations_Locations_MergedIntoLocationId",
                table: "Locations",
                column: "MergedIntoLocationId",
                principalTable: "Locations",
                principalColumn: "LocationId",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_RealEstateDetails_Locations_LocationId",
                table: "RealEstateDetails",
                column: "LocationId",
                principalTable: "Locations",
                principalColumn: "LocationId",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_RecurrencePatterns_Locations_LocationId",
                table: "RecurrencePatterns",
                column: "LocationId",
                principalTable: "Locations",
                principalColumn: "LocationId",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Addresses_Locations_LocationId",
                table: "Addresses");

            migrationBuilder.DropForeignKey(
                name: "FK_CalendarEvents_Locations_LocationId",
                table: "CalendarEvents");

            migrationBuilder.DropForeignKey(
                name: "FK_JournalEntries_Locations_LocationId",
                table: "JournalEntries");

            migrationBuilder.DropForeignKey(
                name: "FK_Locations_Locations_MergedIntoLocationId",
                table: "Locations");

            migrationBuilder.DropForeignKey(
                name: "FK_RealEstateDetails_Locations_LocationId",
                table: "RealEstateDetails");

            migrationBuilder.DropForeignKey(
                name: "FK_RecurrencePatterns_Locations_LocationId",
                table: "RecurrencePatterns");

            migrationBuilder.DropIndex(
                name: "IX_RecurrencePatterns_LocationId",
                table: "RecurrencePatterns");

            migrationBuilder.DropIndex(
                name: "IX_RealEstateDetails_LocationId",
                table: "RealEstateDetails");

            migrationBuilder.DropIndex(
                name: "IX_Locations_AddressKey",
                table: "Locations");

            migrationBuilder.DropIndex(
                name: "IX_Locations_MergedIntoLocationId",
                table: "Locations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Locations_CoordinatesAllOrNone",
                table: "Locations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Locations_SourceKnown",
                table: "Locations");

            migrationBuilder.DropIndex(
                name: "IX_JournalEntries_LocationId",
                table: "JournalEntries");

            migrationBuilder.DropIndex(
                name: "IX_CalendarEvents_LocationId",
                table: "CalendarEvents");

            migrationBuilder.DropIndex(
                name: "IX_Addresses_LocationId",
                table: "Addresses");

            migrationBuilder.DropColumn(
                name: "LocationId",
                table: "RecurrencePatterns");

            migrationBuilder.DropColumn(
                name: "LocationId",
                table: "RealEstateDetails");

            migrationBuilder.DropColumn(
                name: "AddressKey",
                table: "Locations");

            migrationBuilder.DropColumn(
                name: "City",
                table: "Locations");

            migrationBuilder.DropColumn(
                name: "CountryCode",
                table: "Locations");

            migrationBuilder.DropColumn(
                name: "Line1",
                table: "Locations");

            migrationBuilder.DropColumn(
                name: "Line2",
                table: "Locations");

            migrationBuilder.DropColumn(
                name: "MergedIntoLocationId",
                table: "Locations");

            migrationBuilder.DropColumn(
                name: "PostalCode",
                table: "Locations");

            migrationBuilder.DropColumn(
                name: "Region",
                table: "Locations");

            migrationBuilder.DropColumn(
                name: "LocationId",
                table: "JournalEntries");

            migrationBuilder.DropColumn(
                name: "LocationId",
                table: "CalendarEvents");

            migrationBuilder.DropColumn(
                name: "LocationId",
                table: "Addresses");

            migrationBuilder.UpdateData(
                table: "Locations",
                keyColumn: "TimeZoneId",
                keyValue: null,
                column: "TimeZoneId",
                value: "");

            migrationBuilder.AlterColumn<string>(
                name: "TimeZoneId",
                table: "Locations",
                type: "varchar(64)",
                maxLength: 64,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "varchar(64)",
                oldMaxLength: 64,
                oldNullable: true)
                .Annotation("MySql:CharSet", "utf8mb4")
                .OldAnnotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AlterColumn<decimal>(
                name: "Longitude",
                table: "Locations",
                type: "decimal(8,5)",
                precision: 8,
                scale: 5,
                nullable: false,
                defaultValue: 0m,
                oldClrType: typeof(decimal),
                oldType: "decimal(8,5)",
                oldPrecision: 8,
                oldScale: 5,
                oldNullable: true);

            migrationBuilder.AlterColumn<decimal>(
                name: "Latitude",
                table: "Locations",
                type: "decimal(7,5)",
                precision: 7,
                scale: 5,
                nullable: false,
                defaultValue: 0m,
                oldClrType: typeof(decimal),
                oldType: "decimal(7,5)",
                oldPrecision: 7,
                oldScale: 5,
                oldNullable: true);

            migrationBuilder.UpdateData(
                table: "Addresses",
                keyColumn: "Line1",
                keyValue: null,
                column: "Line1",
                value: "");

            migrationBuilder.AlterColumn<string>(
                name: "Line1",
                table: "Addresses",
                type: "varchar(256)",
                maxLength: 256,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "varchar(256)",
                oldMaxLength: 256,
                oldNullable: true)
                .Annotation("MySql:CharSet", "utf8mb4")
                .OldAnnotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.UpdateData(
                table: "Addresses",
                keyColumn: "CountryCode",
                keyValue: null,
                column: "CountryCode",
                value: "");

            migrationBuilder.AlterColumn<string>(
                name: "CountryCode",
                table: "Addresses",
                type: "varchar(2)",
                maxLength: 2,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "varchar(2)",
                oldMaxLength: 2,
                oldNullable: true)
                .Annotation("MySql:CharSet", "utf8mb4")
                .OldAnnotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.UpdateData(
                table: "Addresses",
                keyColumn: "City",
                keyValue: null,
                column: "City",
                value: "");

            migrationBuilder.AlterColumn<string>(
                name: "City",
                table: "Addresses",
                type: "varchar(128)",
                maxLength: 128,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "varchar(128)",
                oldMaxLength: 128,
                oldNullable: true)
                .Annotation("MySql:CharSet", "utf8mb4")
                .OldAnnotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Locations_SourceKnown",
                table: "Locations",
                sql: "`Source` IN (1, 2, 3)");
        }
    }
}
