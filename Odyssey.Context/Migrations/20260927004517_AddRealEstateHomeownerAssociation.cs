using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Odyssey.Context.Migrations
{
    /// <inheritdoc />
    public partial class AddRealEstateHomeownerAssociation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "HomeownerAssociationId",
                table: "RealEstateDetails",
                type: "char(36)",
                nullable: true,
                collation: "ascii_general_ci");

            migrationBuilder.CreateIndex(
                name: "IX_RealEstateDetails_HomeownerAssociationId",
                table: "RealEstateDetails",
                column: "HomeownerAssociationId");

            migrationBuilder.AddForeignKey(
                name: "FK_RealEstateDetails_Contacts_HomeownerAssociationId",
                table: "RealEstateDetails",
                column: "HomeownerAssociationId",
                principalTable: "Contacts",
                principalColumn: "ContactId",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_RealEstateDetails_Contacts_HomeownerAssociationId",
                table: "RealEstateDetails");

            migrationBuilder.DropIndex(
                name: "IX_RealEstateDetails_HomeownerAssociationId",
                table: "RealEstateDetails");

            migrationBuilder.DropColumn(
                name: "HomeownerAssociationId",
                table: "RealEstateDetails");
        }
    }
}
