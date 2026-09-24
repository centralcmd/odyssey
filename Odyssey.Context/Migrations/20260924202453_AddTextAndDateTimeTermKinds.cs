using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Odyssey.Context.Migrations
{
    /// <inheritdoc />
    public partial class AddTextAndDateTimeTermKinds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<decimal>(
                name: "Value",
                table: "Terms",
                type: "decimal(18,6)",
                precision: 18,
                scale: 6,
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "decimal(18,6)",
                oldPrecision: 18,
                oldScale: 6);

            migrationBuilder.AddColumn<DateTime>(
                name: "DateTimeValue",
                table: "Terms",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TextValue",
                table: "Terms",
                type: "varchar(256)",
                maxLength: 256,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Terms_ValueMatchesUnit",
                table: "Terms",
                sql: "(`ValueUnit` IN (0, 1) AND `Value` IS NOT NULL AND `TextValue` IS NULL AND `DateTimeValue` IS NULL) OR (`ValueUnit` = 2 AND `Value` IS NULL AND `TextValue` IS NOT NULL AND `DateTimeValue` IS NULL) OR (`ValueUnit` = 3 AND `Value` IS NULL AND `TextValue` IS NULL AND `DateTimeValue` IS NOT NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Refuse rather than lose data: a Text or DateTime row has no numeric value to restore, and
            // dropping its columns would silently discard the text or instant (issue #192 §11).
            migrationBuilder.Sql(
                "BEGIN NOT ATOMIC "
                + "IF EXISTS (SELECT 1 FROM `Terms` WHERE `ValueUnit` NOT IN (0, 1)) THEN "
                + "SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Terms holds Text or DateTime rows; remove them before reverting AddTextAndDateTimeTermKinds.'; "
                + "END IF; "
                + "END");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Terms_ValueMatchesUnit",
                table: "Terms");

            migrationBuilder.DropColumn(
                name: "DateTimeValue",
                table: "Terms");

            migrationBuilder.DropColumn(
                name: "TextValue",
                table: "Terms");

            migrationBuilder.AlterColumn<decimal>(
                name: "Value",
                table: "Terms",
                type: "decimal(18,6)",
                precision: 18,
                scale: 6,
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "decimal(18,6)",
                oldPrecision: 18,
                oldScale: 6,
                oldNullable: true);
        }
    }
}
