using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Odyssey.Context.Migrations
{
    /// <summary>
    /// Removes the budget item rows that cannot survive issue #75's constraints — untagged rows (the
    /// column becomes <c>NOT NULL</c>) and duplicate-tag rows (the pair becomes unique) — archiving each
    /// one in full first.
    ///
    /// <para>
    /// <strong>This is destructive, deliberately.</strong> Non-Goal 7 forecloses minting a tag per
    /// distinct legacy name: an automated conversion would have no one to judge whether two names meant
    /// one category. The archive is what makes the loss recoverable and what <c>Down</c> restores from.
    /// </para>
    ///
    /// <para>
    /// No DDL here, only DML — the tables it writes to were created by the previous migration precisely
    /// so that this one commits nothing implicitly ahead of its own work.
    /// </para>
    /// </summary>
    public partial class ArchiveAndRemoveUntaggableBudgetItems : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1. Every untagged row, in full.
            migrationBuilder.Sql(@"
                INSERT INTO `_BudgetItemLabelArchive`
                    (`BudgetItemId`, `Disposition`, `BudgetId`, `CategoryType`, `PlannedAmount`,
                     `TransactionTagId`, `Name`, `Description`, `ArchivedAt`)
                SELECT bi.`BudgetItemId`, 'Deleted', bi.`BudgetId`, bi.`CategoryType`, bi.`PlannedAmount`,
                       bi.`TransactionTagId`, bi.`Name`, bi.`Description`, UTC_TIMESTAMP(6)
                FROM `BudgetItems` bi
                WHERE bi.`TransactionTagId` IS NULL;");

            // 2. Every duplicate-tag row step 4 will remove, in full. 'Lowest' BudgetItemId is
            //    LEXICOGRAPHIC — the column is char(36) — which is deterministic but NOT chronological,
            //    so this keeps an arbitrary-but-stable member of each group, not the oldest.
            migrationBuilder.Sql(@"
                INSERT INTO `_BudgetItemLabelArchive`
                    (`BudgetItemId`, `Disposition`, `BudgetId`, `CategoryType`, `PlannedAmount`,
                     `TransactionTagId`, `Name`, `Description`, `ArchivedAt`)
                SELECT bi.`BudgetItemId`, 'Deleted', bi.`BudgetId`, bi.`CategoryType`, bi.`PlannedAmount`,
                       bi.`TransactionTagId`, bi.`Name`, bi.`Description`, UTC_TIMESTAMP(6)
                FROM `BudgetItems` bi
                JOIN `BudgetItems` keep
                  ON keep.`BudgetId` = bi.`BudgetId`
                 AND keep.`TransactionTagId` = bi.`TransactionTagId`
                 AND keep.`BudgetItemId` < bi.`BudgetItemId`
                WHERE bi.`TransactionTagId` IS NOT NULL;");

            // 3. Untagged rows.
            migrationBuilder.Sql("DELETE FROM `BudgetItems` WHERE `TransactionTagId` IS NULL;");

            // 4. Duplicate-tag rows, keeping the lowest BudgetItemId per (BudgetId, TransactionTagId).
            migrationBuilder.Sql(@"
                DELETE bi FROM `BudgetItems` bi
                JOIN `BudgetItems` keep
                  ON keep.`BudgetId` = bi.`BudgetId`
                 AND keep.`TransactionTagId` = bi.`TransactionTagId`
                 AND keep.`BudgetItemId` < bi.`BudgetItemId`;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Re-insert every row this migration deleted. By the time this runs,
            // MakeBudgetItemTagRequiredAndDropItemNames' Down has already restored the nullable
            // TransactionTagId, the Name/Description columns and the (BudgetId, Name) index — which is
            // why that Down has to reverse all five of its steps: these rows are BY CONSTRUCTION exactly
            // the ones a NOT NULL column and a unique (BudgetId, TransactionTagId) index reject.
            migrationBuilder.Sql(@"
                INSERT INTO `BudgetItems`
                    (`BudgetItemId`, `BudgetId`, `Name`, `Description`, `CategoryType`, `PlannedAmount`,
                     `TransactionTagId`)
                SELECT a.`BudgetItemId`, a.`BudgetId`, COALESCE(a.`Name`, ''), a.`Description`,
                       a.`CategoryType`, a.`PlannedAmount`, a.`TransactionTagId`
                FROM `_BudgetItemLabelArchive` a
                WHERE a.`Disposition` = 'Deleted'
                  AND NOT EXISTS (SELECT 1 FROM `BudgetItems` bi WHERE bi.`BudgetItemId` = a.`BudgetItemId`);");

            // The rows are back in BudgetItems, so the Deleted archive entries have served their purpose.
            // Clearing them is what keeps a Down/Up cycle at the same row count as a single Up (AC 24).
            migrationBuilder.Sql("DELETE FROM `_BudgetItemLabelArchive` WHERE `Disposition` = 'Deleted';");
        }
    }
}
