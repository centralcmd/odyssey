using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Odyssey.Context.Migrations
{
    /// <inheritdoc />
    public partial class AddTaxStatementSettlementRange : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "SettlementEndDate",
                table: "TaxStatements",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SettlementStartDate",
                table: "TaxStatements",
                type: "datetime(6)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SettlementEndDate",
                table: "TaxStatements");

            migrationBuilder.DropColumn(
                name: "SettlementStartDate",
                table: "TaxStatements");
        }
    }
}
