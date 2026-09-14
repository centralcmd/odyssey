using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Odyssey.Context.Migrations
{
    /// <summary>
    /// Drops issue #75's two archive ledgers, <c>_BudgetItemLabelArchive</c> and
    /// <c>_TransactionTagNameDisambiguation</c> — the retention trigger #75 §10.12 named, tracked as
    /// issue #78.
    ///
    /// <para>
    /// <strong>Why.</strong> Both tables hold user-authored free text — budget item names and
    /// descriptions, original tag names — and nothing reads them except the <c>Down</c> of the migration
    /// set that wrote them. Keeping them indefinitely would be exactly the "a later migration will drop
    /// it" that never happened to <c>_InsurancePolicyFileRelocation</c>. GDPR Art. 5(1)(e), storage
    /// limitation.
    /// </para>
    ///
    /// <para>
    /// <strong>No export step first</strong> (#78 Non-Goal 1): what licensed #75's destructive migration
    /// is that no deployed <c>odyssey</c> database holds data anyone needs to keep. If a real deployment
    /// has retired that precondition before this ships, re-read #75 §15 first.
    /// </para>
    ///
    /// <para>
    /// <strong><c>Down</c> recreates both tables EMPTY</strong>, with their original columns and primary
    /// keys, so that the #75 migrations' own <c>Down</c>s still have tables to read. The data is gone by
    /// then, and a <c>Down</c> that pretended otherwise would be worse than one that does not. The
    /// consequence is deliberate: reverting past #75 after this migration restores every surviving budget
    /// item with its id as its name, re-inserts none of the deleted items and leaves disambiguated tag
    /// names suffixed — the reversibility the archives bought ends here.
    /// </para>
    ///
    /// <para>
    /// Neither table is an EF entity, so there is no model or snapshot change.
    /// </para>
    /// </summary>
    public partial class DropChangeArchiveTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "_BudgetItemLabelArchive");
            migrationBuilder.DropTable(name: "_TransactionTagNameDisambiguation");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Identical to AddChangeArchiveTables' Up — including Disposition in the archive's primary
            // key, which that migration's Down/Up cycle relies on.
            migrationBuilder.CreateTable(
                name: "_BudgetItemLabelArchive",
                columns: table => new
                {
                    BudgetItemId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    Disposition = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    BudgetId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    CategoryType = table.Column<int>(type: "int", nullable: false),
                    PlannedAmount = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: false),
                    TransactionTagId = table.Column<Guid>(type: "char(36)", nullable: true, collation: "ascii_general_ci"),
                    Name = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Description = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ArchivedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK__BudgetItemLabelArchive", x => new { x.BudgetItemId, x.Disposition });
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "_TransactionTagNameDisambiguation",
                columns: table => new
                {
                    TransactionTagId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    OriginalName = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    NewName = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ArchivedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK__TransactionTagNameDisambiguation", x => x.TransactionTagId);
                })
                .Annotation("MySql:CharSet", "utf8mb4");
        }
    }
}
