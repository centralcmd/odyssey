using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Odyssey.Context.Migrations
{
    /// <inheritdoc />
    public partial class AddContactAliasesAndLifecycleDates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "DateOfDeath",
                table: "PersonDetails",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MiddleName",
                table: "PersonDetails",
                type: "varchar(128)",
                maxLength: 128,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<DateOnly>(
                name: "DissolvedDate",
                table: "OrganizationDetails",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "EstablishedDate",
                table: "OrganizationDetails",
                type: "date",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ContactAliases",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    ContactId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    Value = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Label = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContactAliases", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ContactAliases_Contacts_ContactId",
                        column: x => x.ContactId,
                        principalTable: "Contacts",
                        principalColumn: "ContactId",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_ContactAliases_ContactId_Value",
                table: "ContactAliases",
                columns: new[] { "ContactId", "Value" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ContactAliases");

            migrationBuilder.DropColumn(
                name: "DateOfDeath",
                table: "PersonDetails");

            migrationBuilder.DropColumn(
                name: "MiddleName",
                table: "PersonDetails");

            migrationBuilder.DropColumn(
                name: "DissolvedDate",
                table: "OrganizationDetails");

            migrationBuilder.DropColumn(
                name: "EstablishedDate",
                table: "OrganizationDetails");
        }
    }
}
