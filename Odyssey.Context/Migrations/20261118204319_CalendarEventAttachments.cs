using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Odyssey.Context.Migrations
{
    /// <inheritdoc />
    public partial class CalendarEventAttachments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CalendarEventAttachments",
                columns: table => new
                {
                    CalendarEventAttachmentId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    CalendarEventId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    FileId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CalendarEventAttachments", x => x.CalendarEventAttachmentId);
                    table.ForeignKey(
                        name: "FK_CalendarEventAttachments_CalendarEvents_CalendarEventId",
                        column: x => x.CalendarEventId,
                        principalTable: "CalendarEvents",
                        principalColumn: "CalendarEventId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CalendarEventAttachments_FileMetadata_FileId",
                        column: x => x.FileId,
                        principalTable: "FileMetadata",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "CalendarEventPhotos",
                columns: table => new
                {
                    CalendarEventPhotoId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    CalendarEventId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    PhotoId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    Position = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CalendarEventPhotos", x => x.CalendarEventPhotoId);
                    table.ForeignKey(
                        name: "FK_CalendarEventPhotos_CalendarEvents_CalendarEventId",
                        column: x => x.CalendarEventId,
                        principalTable: "CalendarEvents",
                        principalColumn: "CalendarEventId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CalendarEventPhotos_Photos_PhotoId",
                        column: x => x.PhotoId,
                        principalTable: "Photos",
                        principalColumn: "PhotoId",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "RecurrencePatternAttachments",
                columns: table => new
                {
                    RecurrencePatternAttachmentId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    RecurrencePatternId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    FileId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecurrencePatternAttachments", x => x.RecurrencePatternAttachmentId);
                    table.ForeignKey(
                        name: "FK_RecurrencePatternAttachments_FileMetadata_FileId",
                        column: x => x.FileId,
                        principalTable: "FileMetadata",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_RecurrencePatternAttachments_RecurrencePatterns_RecurrencePa~",
                        column: x => x.RecurrencePatternId,
                        principalTable: "RecurrencePatterns",
                        principalColumn: "RecurrencePatternId",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "RecurrencePatternPhotos",
                columns: table => new
                {
                    RecurrencePatternPhotoId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    RecurrencePatternId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    PhotoId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    Position = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecurrencePatternPhotos", x => x.RecurrencePatternPhotoId);
                    table.ForeignKey(
                        name: "FK_RecurrencePatternPhotos_Photos_PhotoId",
                        column: x => x.PhotoId,
                        principalTable: "Photos",
                        principalColumn: "PhotoId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_RecurrencePatternPhotos_RecurrencePatterns_RecurrencePattern~",
                        column: x => x.RecurrencePatternId,
                        principalTable: "RecurrencePatterns",
                        principalColumn: "RecurrencePatternId",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.InsertData(
                table: "SystemSettings",
                columns: new[] { "Key", "UpdatedAt", "UpdatedBy", "Value" },
                values: new object[] { "CalendarEventMaxLinksPerKind", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "50" });

            migrationBuilder.CreateIndex(
                name: "IX_CalendarEventAttachments_CalendarEventId_FileId",
                table: "CalendarEventAttachments",
                columns: new[] { "CalendarEventId", "FileId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CalendarEventAttachments_FileId",
                table: "CalendarEventAttachments",
                column: "FileId");

            migrationBuilder.CreateIndex(
                name: "IX_CalendarEventPhotos_CalendarEventId_PhotoId",
                table: "CalendarEventPhotos",
                columns: new[] { "CalendarEventId", "PhotoId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CalendarEventPhotos_PhotoId",
                table: "CalendarEventPhotos",
                column: "PhotoId");

            migrationBuilder.CreateIndex(
                name: "IX_RecurrencePatternAttachments_FileId",
                table: "RecurrencePatternAttachments",
                column: "FileId");

            migrationBuilder.CreateIndex(
                name: "IX_RecurrencePatternAttachments_RecurrencePatternId_FileId",
                table: "RecurrencePatternAttachments",
                columns: new[] { "RecurrencePatternId", "FileId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RecurrencePatternPhotos_PhotoId",
                table: "RecurrencePatternPhotos",
                column: "PhotoId");

            migrationBuilder.CreateIndex(
                name: "IX_RecurrencePatternPhotos_RecurrencePatternId_PhotoId",
                table: "RecurrencePatternPhotos",
                columns: new[] { "RecurrencePatternId", "PhotoId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CalendarEventAttachments");

            migrationBuilder.DropTable(
                name: "CalendarEventPhotos");

            migrationBuilder.DropTable(
                name: "RecurrencePatternAttachments");

            migrationBuilder.DropTable(
                name: "RecurrencePatternPhotos");

            migrationBuilder.DeleteData(
                table: "SystemSettings",
                keyColumn: "Key",
                keyValue: "CalendarEventMaxLinksPerKind");
        }
    }
}
