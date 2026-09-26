using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Odyssey.Context.Migrations
{
    /// <inheritdoc />
    public partial class AddPropertyContractParties : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // AddColumn goes FIRST, before the check is dropped (issue #208 §11). MariaDB commits each DDL
            // statement implicitly, so an interrupted run leaves a prefix applied; with the column first,
            // any prefix that got as far as dropping the check also created the column, and
            // MigrationRunner's drift guard reports it instead of the replay dying on a DROP of a
            // constraint that is already gone. The widened check is valid for every existing row
            // (PropertyId is NULL and exactly one of the other two is set), so there is no backfill.
            migrationBuilder.AddColumn<Guid>(
                name: "PropertyId",
                table: "ContractParties",
                type: "char(36)",
                nullable: true,
                collation: "ascii_general_ci");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ContractParties_ExactlyOneTarget",
                table: "ContractParties");

            migrationBuilder.CreateIndex(
                name: "IX_ContractParties_ContractId_PropertyId_Role",
                table: "ContractParties",
                columns: new[] { "ContractId", "PropertyId", "Role" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ContractParties_PropertyId",
                table: "ContractParties",
                column: "PropertyId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ContractParties_ExactlyOneTarget",
                table: "ContractParties",
                sql: "((`AccountId` IS NOT NULL) + (`ContactId` IS NOT NULL) + (`PropertyId` IS NOT NULL)) = 1");

            migrationBuilder.AddForeignKey(
                name: "FK_ContractParties_Properties_PropertyId",
                table: "ContractParties",
                column: "PropertyId",
                principalTable: "Properties",
                principalColumn: "PropertyId",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ContractParties_Properties_PropertyId",
                table: "ContractParties");

            migrationBuilder.DropIndex(
                name: "IX_ContractParties_ContractId_PropertyId_Role",
                table: "ContractParties");

            migrationBuilder.DropIndex(
                name: "IX_ContractParties_PropertyId",
                table: "ContractParties");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ContractParties_ExactlyOneTarget",
                table: "ContractParties");

            migrationBuilder.DropColumn(
                name: "PropertyId",
                table: "ContractParties");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ContractParties_ExactlyOneTarget",
                table: "ContractParties",
                sql: "((`AccountId` IS NOT NULL) + (`ContactId` IS NOT NULL)) = 1");
        }
    }
}
