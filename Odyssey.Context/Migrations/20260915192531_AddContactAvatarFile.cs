using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Odyssey.Context.Migrations
{
    /// <inheritdoc />
    public partial class AddContactAvatarFile : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "AvatarFileId",
                table: "Contacts",
                type: "char(36)",
                nullable: true,
                collation: "ascii_general_ci");

            migrationBuilder.CreateIndex(
                name: "IX_Contacts_AvatarFileId",
                table: "Contacts",
                column: "AvatarFileId",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Contacts_FileMetadata_AvatarFileId",
                table: "Contacts",
                column: "AvatarFileId",
                principalTable: "FileMetadata",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Contacts_FileMetadata_AvatarFileId",
                table: "Contacts");

            migrationBuilder.DropIndex(
                name: "IX_Contacts_AvatarFileId",
                table: "Contacts");

            migrationBuilder.DropColumn(
                name: "AvatarFileId",
                table: "Contacts");
        }
    }
}
