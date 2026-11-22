using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Odyssey.Context.Migrations
{
    /// <inheritdoc />
    public partial class AddRecipeAttachmentsAndLibraryFiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_FileMetadata_ExactlyOneSource",
                table: "FileMetadata");

            migrationBuilder.AddColumn<Guid>(
                name: "LibraryStoredObjectId",
                table: "FileMetadata",
                type: "char(36)",
                nullable: true,
                collation: "ascii_general_ci");

            migrationBuilder.CreateTable(
                name: "RecipeAttachments",
                columns: table => new
                {
                    RecipeAttachmentId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    RecipeId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    FileId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecipeAttachments", x => x.RecipeAttachmentId);
                    table.ForeignKey(
                        name: "FK_RecipeAttachments_FileMetadata_FileId",
                        column: x => x.FileId,
                        principalTable: "FileMetadata",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_RecipeAttachments_Recipes_RecipeId",
                        column: x => x.RecipeId,
                        principalTable: "Recipes",
                        principalColumn: "RecipeId",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_FileMetadata_LibraryStoredObjectId",
                table: "FileMetadata",
                column: "LibraryStoredObjectId",
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_FileMetadata_ExactlyOneSource",
                table: "FileMetadata",
                sql: "(`FinanceStoredObjectId` IS NOT NULL) + (`JournalStoredObjectId` IS NOT NULL) + (`ContactAvatarStoredObjectId` IS NOT NULL) + (`LibraryStoredObjectId` IS NOT NULL) = 1");

            migrationBuilder.CreateIndex(
                name: "IX_RecipeAttachments_FileId",
                table: "RecipeAttachments",
                column: "FileId");

            migrationBuilder.CreateIndex(
                name: "IX_RecipeAttachments_RecipeId_FileId",
                table: "RecipeAttachments",
                columns: new[] { "RecipeId", "FileId" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_FileMetadata_StoredObjects_LibraryStoredObjectId",
                table: "FileMetadata",
                column: "LibraryStoredObjectId",
                principalTable: "StoredObjects",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_FileMetadata_StoredObjects_LibraryStoredObjectId",
                table: "FileMetadata");

            migrationBuilder.DropTable(
                name: "RecipeAttachments");

            migrationBuilder.DropIndex(
                name: "IX_FileMetadata_LibraryStoredObjectId",
                table: "FileMetadata");

            migrationBuilder.DropCheckConstraint(
                name: "CK_FileMetadata_ExactlyOneSource",
                table: "FileMetadata");

            migrationBuilder.DropColumn(
                name: "LibraryStoredObjectId",
                table: "FileMetadata");

            migrationBuilder.AddCheckConstraint(
                name: "CK_FileMetadata_ExactlyOneSource",
                table: "FileMetadata",
                sql: "(`FinanceStoredObjectId` IS NOT NULL) + (`JournalStoredObjectId` IS NOT NULL) + (`ContactAvatarStoredObjectId` IS NOT NULL) = 1");
        }
    }
}
