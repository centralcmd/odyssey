using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Odyssey.Context.Migrations
{
    /// <summary>
    /// Books (issue #527): the six book tables, and the Library file area's <c>FileMetadata.LibraryStoredObjectId</c>
    /// (RESTRICT, unique) with <c>CK_FileMetadata_ExactlyOneSource</c> re-created to count it.
    /// </summary>
    /// <remarks>
    /// Schema-only. Re-adding the check re-validates every <c>FileMetadata</c> row, which rebuilds the table under a
    /// lock on MariaDB — time it on a production-sized copy. <c>Down()</c> restores schema, not rows: once any file has
    /// been uploaded with <c>area: Library</c>, dropping the column would orphan it, so roll back by restoring a backup.
    /// Ordered after <c>AddPodcastFeeds</c>: the two touch disjoint objects.
    /// </remarks>
    public partial class AddBooks : Migration
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
                name: "Books",
                columns: table => new
                {
                    BookId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    Title = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Publisher = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Year = table.Column<int>(type: "int", nullable: true),
                    Isbn = table.Column<string>(type: "varchar(13)", maxLength: 13, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Format = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    Description = table.Column<string>(type: "varchar(4000)", maxLength: 4000, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Notes = table.Column<string>(type: "varchar(8000)", maxLength: 8000, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    MainAuthorContactId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    Version = table.Column<int>(type: "int", nullable: false),
                    CreatedByUserId = table.Column<string>(type: "varchar(255)", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    UpdatedByUserId = table.Column<string>(type: "varchar(255)", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Books", x => x.BookId);
                    table.ForeignKey(
                        name: "FK_Books_AspNetUsers_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Books_AspNetUsers_UpdatedByUserId",
                        column: x => x.UpdatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Books_Contacts_MainAuthorContactId",
                        column: x => x.MainAuthorContactId,
                        principalTable: "Contacts",
                        principalColumn: "ContactId",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "BookTags",
                columns: table => new
                {
                    BookTagId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    Name = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Description = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Archived = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    Icon = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Color = table.Column<string>(type: "varchar(7)", maxLength: 7, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BookTags", x => x.BookTagId);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "BookAttachments",
                columns: table => new
                {
                    BookAttachmentId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    BookId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    FileId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BookAttachments", x => x.BookAttachmentId);
                    table.ForeignKey(
                        name: "FK_BookAttachments_Books_BookId",
                        column: x => x.BookId,
                        principalTable: "Books",
                        principalColumn: "BookId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_BookAttachments_FileMetadata_FileId",
                        column: x => x.FileId,
                        principalTable: "FileMetadata",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "BookContributors",
                columns: table => new
                {
                    BookContributorId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    BookId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    ContactId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    Role = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Position = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BookContributors", x => x.BookContributorId);
                    table.ForeignKey(
                        name: "FK_BookContributors_Books_BookId",
                        column: x => x.BookId,
                        principalTable: "Books",
                        principalColumn: "BookId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_BookContributors_Contacts_ContactId",
                        column: x => x.ContactId,
                        principalTable: "Contacts",
                        principalColumn: "ContactId",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "BookImages",
                columns: table => new
                {
                    BookImageId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    BookId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    PhotoId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    IsCover = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BookImages", x => x.BookImageId);
                    table.ForeignKey(
                        name: "FK_BookImages_Books_BookId",
                        column: x => x.BookId,
                        principalTable: "Books",
                        principalColumn: "BookId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_BookImages_Photos_PhotoId",
                        column: x => x.PhotoId,
                        principalTable: "Photos",
                        principalColumn: "PhotoId",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "BookTagLinks",
                columns: table => new
                {
                    BookTagLinkId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    BookId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    BookTagId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BookTagLinks", x => x.BookTagLinkId);
                    table.ForeignKey(
                        name: "FK_BookTagLinks_BookTags_BookTagId",
                        column: x => x.BookTagId,
                        principalTable: "BookTags",
                        principalColumn: "BookTagId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BookTagLinks_Books_BookId",
                        column: x => x.BookId,
                        principalTable: "Books",
                        principalColumn: "BookId",
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
                name: "IX_BookAttachments_BookId_FileId",
                table: "BookAttachments",
                columns: new[] { "BookId", "FileId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BookAttachments_FileId",
                table: "BookAttachments",
                column: "FileId");

            migrationBuilder.CreateIndex(
                name: "IX_BookContributors_BookId_ContactId",
                table: "BookContributors",
                columns: new[] { "BookId", "ContactId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BookContributors_ContactId",
                table: "BookContributors",
                column: "ContactId");

            migrationBuilder.CreateIndex(
                name: "IX_BookImages_BookId_PhotoId",
                table: "BookImages",
                columns: new[] { "BookId", "PhotoId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BookImages_PhotoId",
                table: "BookImages",
                column: "PhotoId");

            migrationBuilder.CreateIndex(
                name: "IX_Books_CreatedByUserId",
                table: "Books",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Books_Isbn",
                table: "Books",
                column: "Isbn");

            migrationBuilder.CreateIndex(
                name: "IX_Books_MainAuthorContactId",
                table: "Books",
                column: "MainAuthorContactId");

            migrationBuilder.CreateIndex(
                name: "IX_Books_Status",
                table: "Books",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_Books_Title",
                table: "Books",
                column: "Title");

            migrationBuilder.CreateIndex(
                name: "IX_Books_UpdatedByUserId",
                table: "Books",
                column: "UpdatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_BookTagLinks_BookId_BookTagId",
                table: "BookTagLinks",
                columns: new[] { "BookId", "BookTagId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BookTagLinks_BookTagId",
                table: "BookTagLinks",
                column: "BookTagId");

            migrationBuilder.CreateIndex(
                name: "IX_BookTags_Archived",
                table: "BookTags",
                column: "Archived");

            migrationBuilder.CreateIndex(
                name: "IX_BookTags_Name",
                table: "BookTags",
                column: "Name",
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
                name: "BookAttachments");

            migrationBuilder.DropTable(
                name: "BookContributors");

            migrationBuilder.DropTable(
                name: "BookImages");

            migrationBuilder.DropTable(
                name: "BookTagLinks");

            migrationBuilder.DropTable(
                name: "BookTags");

            migrationBuilder.DropTable(
                name: "Books");

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
