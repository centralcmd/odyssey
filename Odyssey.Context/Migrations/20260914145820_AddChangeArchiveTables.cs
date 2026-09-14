using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Odyssey.Context.Migrations
{
    /// <summary>
    /// The two archive ledgers for issue #75 — created on their own, ahead of the migrations that
    /// write to them.
    ///
    /// <para>
    /// <strong>Why this is a separate migration.</strong> MariaDB commits DDL implicitly, so a single
    /// migration that archived and then destroyed in one unit would have no atomicity to offer. This
    /// follows the issue #26 standard: create the ledger in its own migration, populate it in the next,
    /// destroy in the last.
    /// </para>
    ///
    /// <para>
    /// <strong>Why they carry the full source payload rather than ids.</strong> The rows and the labels
    /// they record are about to be deleted, so these tables are the only surviving record of them and
    /// the sole basis for <c>Down</c>. Ids alone would let <c>Down</c> find the rows but not
    /// reconstruct them.
    /// </para>
    ///
    /// <para>
    /// <strong>Retention.</strong> Both tables hold user-authored free text indefinitely, so they have a
    /// NAMED retention trigger (issue #75 §10.12): they are dropped by a migration in the release
    /// following the one that creates them, tracked as issue #78 — filed before #75 closed, not after.
    /// "A later migration will drop it" is precisely what did not happen to
    /// <c>_InsurancePolicyFileRelocation</c>, which is still live.
    /// GDPR Art. 5(1)(e), storage limitation. Note <c>BudgetItem</c> has no <c>CreatedByUserId</c>, so
    /// this is a storage-limitation question only — the archives are not reachable by an erasure request.
    /// </para>
    ///
    /// <para>
    /// Neither table is part of the EF model — they are operational artefacts, not domain data — so they
    /// are created here by hand and never appear in <c>OdysseyContextModelSnapshot</c>. They also carry
    /// no foreign keys: a ledger row is a record of what happened, and it has to survive the deletion of
    /// the budget or the tag it names, which is exactly the case <c>Down</c> has to report on.
    /// </para>
    /// </summary>
    public partial class AddChangeArchiveTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Disposition is part of the PRIMARY KEY, and that is load-bearing: without it a Down/Up
            // cycle appends a second generation of rows and the restore-by-join in
            // MakeBudgetItemTagRequiredAndDropItemNames' Down becomes non-deterministic. One row can be
            // both Deleted (it had no tag) and — in a later cycle — LabelsDropped, so the item id alone
            // is not unique either.
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

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "_BudgetItemLabelArchive");
            migrationBuilder.DropTable(name: "_TransactionTagNameDisambiguation");
        }
    }
}
