using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Odyssey.Context.Migrations
{
    /// <inheritdoc />
    public partial class AddContractFileValidity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "IssuedAt",
                table: "ContractFiles",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "IssuedBy",
                table: "ContractFiles",
                type: "char(36)",
                nullable: true,
                collation: "ascii_general_ci");

            migrationBuilder.AddColumn<DateTime>(
                name: "ValidFrom",
                table: "ContractFiles",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ValidTo",
                table: "ContractFiles",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ContractFiles_IssuedBy",
                table: "ContractFiles",
                column: "IssuedBy");

            migrationBuilder.AddForeignKey(
                name: "FK_ContractFiles_Contacts_IssuedBy",
                table: "ContractFiles",
                column: "IssuedBy",
                principalTable: "Contacts",
                principalColumn: "ContactId",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ContractFiles_Contacts_IssuedBy",
                table: "ContractFiles");

            migrationBuilder.DropIndex(
                name: "IX_ContractFiles_IssuedBy",
                table: "ContractFiles");

            migrationBuilder.DropColumn(
                name: "IssuedAt",
                table: "ContractFiles");

            migrationBuilder.DropColumn(
                name: "IssuedBy",
                table: "ContractFiles");

            migrationBuilder.DropColumn(
                name: "ValidFrom",
                table: "ContractFiles");

            migrationBuilder.DropColumn(
                name: "ValidTo",
                table: "ContractFiles");
        }
    }
}
