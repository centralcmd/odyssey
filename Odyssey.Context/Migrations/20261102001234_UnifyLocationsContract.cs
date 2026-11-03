using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Odyssey.Context.Migrations
{
    /// <summary>
    /// Step 3 of 3 of issue #380 §11 (unified locations): de-duplicates contact address links, makes
    /// <c>Addresses.LocationId</c> required, adds the unique <c>(ContactId, LocationId)</c> index and drops
    /// the old address columns from <c>Addresses</c> and <c>RealEstateDetails</c>.
    /// </summary>
    /// <remarks>
    /// Runs only after the conversion (step 2) returns; <c>MigrationRunner</c> stages it. This is a
    /// <b>data</b> migration: it deletes duplicate link rows and drops columns, and <c>Down()</c> restores
    /// the schema only — rolling back means restoring a backup.
    /// </remarks>
    public partial class UnifyLocationsContract : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1. One row per (ContactId, LocationId). The conversion links two spellings of one address (case
            //    or whitespace variants) to the same location, so a contact that listed both now lists one
            //    place twice. Keep the primary row, else the oldest id; the other row's label goes with it.
            migrationBuilder.Sql("""
                DELETE duplicate FROM `Addresses` AS duplicate
                JOIN `Addresses` AS kept
                  ON kept.`ContactId` = duplicate.`ContactId`
                 AND kept.`LocationId` = duplicate.`LocationId`
                 AND kept.`Id` <> duplicate.`Id`
                 AND (kept.`IsPrimary` > duplicate.`IsPrimary`
                      OR (kept.`IsPrimary` = duplicate.`IsPrimary` AND kept.`Id` < duplicate.`Id`))
                WHERE duplicate.`LocationId` IS NOT NULL;
                """);

            // 2. Every address is a link now. Fails loudly on an unlinked row, so a skipped or failed
            //    conversion cannot pass silently — deliberately no default value.
            migrationBuilder.AlterColumn<Guid>(
                name: "LocationId",
                table: "Addresses",
                type: "char(36)",
                nullable: false,
                collation: "ascii_general_ci",
                oldClrType: typeof(Guid),
                oldType: "char(36)",
                oldNullable: true)
                .OldAnnotation("Relational:Collation", "ascii_general_ci");

            // 3. The first object this migration CREATES, ahead of every drop: an interruption anywhere after
            //    it leaves the index behind, which MigrationRunner's drift guard reports on the next start
            //    instead of replaying drops of columns that are already gone. It also takes over the
            //    ContactId key's index, so it must exist before IX_Addresses_ContactId is dropped (MariaDB
            //    refuses to drop the last index a foreign key uses).
            migrationBuilder.CreateIndex(
                name: "IX_Addresses_ContactId_LocationId",
                table: "Addresses",
                columns: new[] { "ContactId", "LocationId" },
                unique: true);

            migrationBuilder.DropIndex(
                name: "IX_Addresses_ContactId",
                table: "Addresses");

            // 4. The old copies. No value survives the release (product-owner decision 5): rollback is a
            //    backup restore.
            migrationBuilder.DropColumn(
                name: "City",
                table: "Addresses");

            migrationBuilder.DropColumn(
                name: "CountryCode",
                table: "Addresses");

            migrationBuilder.DropColumn(
                name: "Line1",
                table: "Addresses");

            migrationBuilder.DropColumn(
                name: "Line2",
                table: "Addresses");

            migrationBuilder.DropColumn(
                name: "PostalCode",
                table: "Addresses");

            migrationBuilder.DropColumn(
                name: "Region",
                table: "Addresses");

            migrationBuilder.DropColumn(
                name: "AddressLine",
                table: "RealEstateDetails");

            migrationBuilder.DropColumn(
                name: "City",
                table: "RealEstateDetails");

            migrationBuilder.DropColumn(
                name: "CountryCode",
                table: "RealEstateDetails");

            migrationBuilder.DropColumn(
                name: "PostalCode",
                table: "RealEstateDetails");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The ContactId key's own index first: MariaDB refuses to drop the composite while the foreign
            // key has no other index to use.
            migrationBuilder.CreateIndex(
                name: "IX_Addresses_ContactId",
                table: "Addresses",
                column: "ContactId");

            migrationBuilder.DropIndex(
                name: "IX_Addresses_ContactId_LocationId",
                table: "Addresses");

            migrationBuilder.AddColumn<string>(
                name: "AddressLine",
                table: "RealEstateDetails",
                type: "varchar(256)",
                maxLength: 256,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "City",
                table: "RealEstateDetails",
                type: "varchar(128)",
                maxLength: 128,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "CountryCode",
                table: "RealEstateDetails",
                type: "varchar(2)",
                maxLength: 2,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "PostalCode",
                table: "RealEstateDetails",
                type: "varchar(32)",
                maxLength: 32,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AlterColumn<Guid>(
                name: "LocationId",
                table: "Addresses",
                type: "char(36)",
                nullable: true,
                collation: "ascii_general_ci",
                oldClrType: typeof(Guid),
                oldType: "char(36)")
                .OldAnnotation("Relational:Collation", "ascii_general_ci");

            migrationBuilder.AddColumn<string>(
                name: "City",
                table: "Addresses",
                type: "varchar(128)",
                maxLength: 128,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "CountryCode",
                table: "Addresses",
                type: "varchar(2)",
                maxLength: 2,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "Line1",
                table: "Addresses",
                type: "varchar(256)",
                maxLength: 256,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "Line2",
                table: "Addresses",
                type: "varchar(256)",
                maxLength: 256,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "PostalCode",
                table: "Addresses",
                type: "varchar(32)",
                maxLength: 32,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "Region",
                table: "Addresses",
                type: "varchar(128)",
                maxLength: 128,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

        }
    }
}
