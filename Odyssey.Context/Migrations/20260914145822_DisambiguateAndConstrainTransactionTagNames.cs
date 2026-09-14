using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Odyssey.Context.Migrations
{
    /// <summary>
    /// Makes transaction tag names unique, case-insensitively, archived rows included (issue #75 §5.11),
    /// renaming existing duplicates out of the way first.
    ///
    /// <para>
    /// <strong>Why the constraint exists.</strong> A tag name is an identity now, not a label: it names
    /// every budget item that plans for the tag. Two same-named tags would render as two budget rows
    /// indistinguishable in name, description and every visible field — and inline tag creation makes
    /// minting the second one a two-keystroke accident.
    /// </para>
    ///
    /// <para>
    /// <strong>Renamed, not merged</strong> (Non-Goal 8). Merging would have to repoint
    /// <c>Transaction</c>, <c>TransactionTagLink</c>, <c>BudgetItem</c> and <c>AccountSmartTag</c> rows
    /// and then decide which description survives — a data migration with its own failure modes, on a
    /// table this change otherwise only constrains. A numeric suffix preserves every reference and
    /// leaves the merge as a deliberate human act.
    /// </para>
    ///
    /// <para>
    /// <strong>The index covers archived rows</strong> because MariaDB has no filtered indexes. Reusing a
    /// retired name therefore means unarchiving or renaming the archived tag, and every message
    /// reporting the conflict says which case applies.
    /// </para>
    /// </summary>
    public partial class DisambiguateAndConstrainTransactionTagNames : Migration
    {
        /// <summary>
        /// How many disambiguation passes to emit. A rename can itself collide — appending " (2)" to the
        /// second <c>Groceries</c> clashes with a tag literally called <c>Groceries (2)</c> — so the pass
        /// is repeated. Each repetition strictly lengthens the names it touches over a finite set, so it
        /// converges; once no duplicate remains, every statement in a pass matches zero rows. Six is far
        /// beyond anything a real tag table can need.
        /// </summary>
        private const int DisambiguationPasses = 6;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            for (var pass = 0; pass < DisambiguationPasses; pass++)
            {
                EmitDisambiguationPass(migrationBuilder);
            }

            migrationBuilder.Sql("DROP TEMPORARY TABLE IF EXISTS `_tt_disambiguation_pass`;");

            migrationBuilder.CreateIndex(
                name: "IX_TransactionTags_Name",
                table: "TransactionTags",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TransactionTags_Name",
                table: "TransactionTags");

            // Restore every original name in one statement. Order is deliberately unspecified and safe
            // to leave so: the unique index is dropped above, and every row lands on the name it started
            // with, so no intermediate state can collide in a way that survives the statement. (A
            // multi-table UPDATE takes no ORDER BY on MariaDB in any case.)
            migrationBuilder.Sql(@"
                UPDATE `TransactionTags` t
                JOIN `_TransactionTagNameDisambiguation` d ON d.`TransactionTagId` = t.`TransactionTagId`
                SET t.`Name` = d.`OriginalName`;");

            // The names are back on the tags, so the ledger rows have served their purpose. Clearing them
            // is what keeps a Down/Up cycle at the same row count as a single Up.
            migrationBuilder.Sql("DELETE FROM `_TransactionTagNameDisambiguation`;");
        }

        /// <summary>
        /// One pass: materialise the rename each duplicate needs, record it, then apply it.
        ///
        /// <para>
        /// The candidates go through a temporary table rather than a derived table joined straight onto
        /// <c>TransactionTags</c>: MySQL/MariaDB refuse an <c>UPDATE</c> whose own target is re-read in
        /// the same statement, and materialising is the portable way around it. The temporary table is
        /// session-scoped and a migration runs on one connection, so it survives between statements.
        /// </para>
        /// </summary>
        private static void EmitDisambiguationPass(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TEMPORARY TABLE IF EXISTS `_tt_disambiguation_pass`;");

            // Rank is by TransactionTagId, which is char(36): "lowest" is LEXICOGRAPHIC, not
            // chronological. That is fine — the requirement is determinism, not recency — but it should
            // not be described as "the oldest". The rank-1 row keeps its name; every other row in the
            // group takes " (rank)". LEFT(...) keeps the result inside Name's 64-character column, and
            // GREATEST(1, ...) keeps it legal even for a pathologically long suffix.
            migrationBuilder.Sql(@"
                CREATE TEMPORARY TABLE `_tt_disambiguation_pass` AS
                SELECT d.`TransactionTagId`,
                       d.`Name` AS `CurrentName`,
                       CONCAT(
                           LEFT(d.`Name`, GREATEST(1, 64 - CHAR_LENGTH(CONCAT(' (', d.`rn`, ')')))),
                           ' (', d.`rn`, ')') AS `NewName`
                FROM (
                    SELECT t.`TransactionTagId`,
                           t.`Name`,
                           ROW_NUMBER() OVER (PARTITION BY LOWER(t.`Name`) ORDER BY t.`TransactionTagId`) AS `rn`
                    FROM `TransactionTags` t
                ) d
                WHERE d.`rn` > 1;");

            // OriginalName is written once and never rewritten — a second pass renaming the same tag
            // again must not lose the name it started with, which is the only thing Down can restore.
            migrationBuilder.Sql(@"
                INSERT INTO `_TransactionTagNameDisambiguation`
                    (`TransactionTagId`, `OriginalName`, `NewName`, `ArchivedAt`)
                SELECT p.`TransactionTagId`, p.`CurrentName`, p.`NewName`, UTC_TIMESTAMP(6)
                FROM `_tt_disambiguation_pass` p
                ON DUPLICATE KEY UPDATE
                    `NewName` = VALUES(`NewName`),
                    `ArchivedAt` = VALUES(`ArchivedAt`);");

            migrationBuilder.Sql(@"
                UPDATE `TransactionTags` t
                JOIN `_tt_disambiguation_pass` p ON p.`TransactionTagId` = t.`TransactionTagId`
                SET t.`Name` = p.`NewName`;");
        }
    }
}
