using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Odyssey.Context.Migrations
{
    /// <inheritdoc />
    public partial class AddPropertyFiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PropertyFiles",
                columns: table => new
                {
                    PropertyFileId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    PropertyId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    FileMetadataId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    FileType = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    AttachedByUserId = table.Column<string>(type: "varchar(255)", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    AttachedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    ValidFrom = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    ValidTo = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    IssuedAt = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    IssuedBy = table.Column<Guid>(type: "char(36)", nullable: true, collation: "ascii_general_ci")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PropertyFiles", x => x.PropertyFileId);
                    table.ForeignKey(
                        name: "FK_PropertyFiles_AspNetUsers_AttachedByUserId",
                        column: x => x.AttachedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_PropertyFiles_Contacts_IssuedBy",
                        column: x => x.IssuedBy,
                        principalTable: "Contacts",
                        principalColumn: "ContactId",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_PropertyFiles_FileMetadata_FileMetadataId",
                        column: x => x.FileMetadataId,
                        principalTable: "FileMetadata",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PropertyFiles_Properties_PropertyId",
                        column: x => x.PropertyId,
                        principalTable: "Properties",
                        principalColumn: "PropertyId",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_PropertyFiles_AttachedAtUtc",
                table: "PropertyFiles",
                column: "AttachedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_PropertyFiles_AttachedByUserId",
                table: "PropertyFiles",
                column: "AttachedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_PropertyFiles_FileMetadataId",
                table: "PropertyFiles",
                column: "FileMetadataId");

            migrationBuilder.CreateIndex(
                name: "IX_PropertyFiles_IssuedBy",
                table: "PropertyFiles",
                column: "IssuedBy");

            migrationBuilder.CreateIndex(
                name: "IX_PropertyFiles_PropertyId_FileMetadataId",
                table: "PropertyFiles",
                columns: new[] { "PropertyId", "FileMetadataId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PropertyFiles");
        }
    }
}
