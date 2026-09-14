using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Odyssey.Context.Migrations
{
    /// <summary>
    /// The schema half of issue #75: the budget item's tag becomes required and unique per budget, and
    /// the item's own name and description are dropped.
    ///
    /// <para>
    /// <strong>Step 1 archives the SURVIVORS' labels</strong>, and it is the larger share of what this
    /// change destroys — <c>ArchiveAndRemoveUntaggableBudgetItems</c> only recorded the rows it deleted.
    /// Ordering the column drop last does not preserve any evidence by itself; the archive does.
    /// </para>
    ///
    /// <para>
    /// <strong><c>Down</c> is hand-written and reverses all five steps.</strong> The scaffolded version
    /// re-added <c>Name</c> as <c>defaultValue: ""</c> and recreated the unique <c>(BudgetId, Name)</c>
    /// index, which collides on the second item of every budget. It also has to revert steps 3 and 4 —
    /// otherwise <c>ArchiveAndRemoveUntaggableBudgetItems</c>' <c>Down</c>, which runs next, re-inserts
    /// the archived rows against a still-<c>NOT NULL</c> column and a still-present unique index, and
    /// those rows are by construction exactly the untagged and duplicate-tag items those two constraints
    /// reject.
    /// </para>
    /// </summary>
    public partial class MakeBudgetItemTagRequiredAndDropItemNames : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1. Archive the labels of every row that SURVIVES, before they are dropped.
            migrationBuilder.Sql(@"
                INSERT INTO `_BudgetItemLabelArchive`
                    (`BudgetItemId`, `Disposition`, `BudgetId`, `CategoryType`, `PlannedAmount`,
                     `TransactionTagId`, `Name`, `Description`, `ArchivedAt`)
                SELECT bi.`BudgetItemId`, 'LabelsDropped', bi.`BudgetId`, bi.`CategoryType`,
                       bi.`PlannedAmount`, bi.`TransactionTagId`, bi.`Name`, bi.`Description`,
                       UTC_TIMESTAMP(6)
                FROM `BudgetItems` bi
                ON DUPLICATE KEY UPDATE
                    `Name` = VALUES(`Name`),
                    `Description` = VALUES(`Description`),
                    `ArchivedAt` = VALUES(`ArchivedAt`);");

            // 2. The old identity index goes before the column it is built on.
            migrationBuilder.DropIndex(
                name: "IX_BudgetItems_BudgetId_Name",
                table: "BudgetItems");

            // 3. The tag becomes required. DeleteBehavior.Restrict is explicit on both sides, and the
            //    scaffold emitted no foreign-key drop/re-add around this alter — MariaDB narrows a
            //    nullable column to NOT NULL in place without touching the key.
            //
            //    The scaffold's `defaultValue: Guid.Empty` is deliberately NOT kept. It exists to fill
            //    rows that are still NULL, and ArchiveAndRemoveUntaggableBudgetItems has already
            //    deleted every one of them — so it would fill nothing, while leaving the column a
            //    permanent DEFAULT '00000000-…' that a raw insert omitting the tag could land on.
            //    Dropping it makes the previous migration load-bearing rather than belt-and-braces.
            migrationBuilder.AlterColumn<Guid>(
                name: "TransactionTagId",
                table: "BudgetItems",
                type: "char(36)",
                nullable: false,
                collation: "ascii_general_ci",
                oldClrType: typeof(Guid),
                oldType: "char(36)",
                oldNullable: true)
                .OldAnnotation("Relational:Collation", "ascii_general_ci");

            // 4. The new identity index. The plain IX_BudgetItems_TransactionTagId the foreign key needs
            //    is deliberately KEPT: this composite index leads with BudgetId and cannot serve a lookup
            //    by tag alone — which is exactly what the RESTRICT key does on every tag delete. Do not
            //    let a later migration drop it as redundant.
            migrationBuilder.CreateIndex(
                name: "IX_BudgetItems_BudgetId_TransactionTagId",
                table: "BudgetItems",
                columns: new[] { "BudgetId", "TransactionTagId" },
                unique: true);

            // 5. The labels themselves.
            migrationBuilder.DropColumn(
                name: "Description",
                table: "BudgetItems");

            migrationBuilder.DropColumn(
                name: "Name",
                table: "BudgetItems");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // 1. Reverses Up 4.
            migrationBuilder.DropIndex(
                name: "IX_BudgetItems_BudgetId_TransactionTagId",
                table: "BudgetItems");

            // 2. Reverses Up 3.
            migrationBuilder.AlterColumn<Guid>(
                name: "TransactionTagId",
                table: "BudgetItems",
                type: "char(36)",
                nullable: true,
                collation: "ascii_general_ci",
                oldClrType: typeof(Guid),
                oldType: "char(36)")
                .OldAnnotation("Relational:Collation", "ascii_general_ci");

            // 3. Reverses Up 5 — and Name comes back NULLABLE, not `NOT NULL DEFAULT ''`. Adding it as
            //    NOT NULL would be legal, but recreating the unique (BudgetId, Name) index before step 4
            //    had restored the values would then collide on the second item of every budget. Step 5
            //    tightens it once the values are back.
            migrationBuilder.AddColumn<string>(
                name: "Description",
                table: "BudgetItems",
                type: "varchar(256)",
                maxLength: 256,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "Name",
                table: "BudgetItems",
                type: "varchar(64)",
                maxLength: 64,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            // 4. Reverses Up 1. A row with no archive entry (created after the Up) has no name to
            //    restore; it takes the item id so the NOT NULL in step 5 has something unique to hold.
            migrationBuilder.Sql(@"
                UPDATE `BudgetItems` bi
                JOIN `_BudgetItemLabelArchive` a
                  ON a.`BudgetItemId` = bi.`BudgetItemId` AND a.`Disposition` = 'LabelsDropped'
                SET bi.`Name` = a.`Name`, bi.`Description` = a.`Description`;");

            migrationBuilder.Sql(@"
                UPDATE `BudgetItems`
                SET `Name` = `BudgetItemId`
                WHERE `Name` IS NULL OR `Name` = '';");

            // The labels are back on the rows, so the LabelsDropped entries have served their purpose.
            // Clearing them is what keeps a Down/Up cycle at the same row count as a single Up (AC 24).
            migrationBuilder.Sql("DELETE FROM `_BudgetItemLabelArchive` WHERE `Disposition` = 'LabelsDropped';");

            // 5. Name returns to NOT NULL, now that every row has one.
            migrationBuilder.AlterColumn<string>(
                name: "Name",
                table: "BudgetItems",
                type: "varchar(64)",
                maxLength: 64,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "varchar(64)",
                oldMaxLength: 64,
                oldNullable: true)
                .Annotation("MySql:CharSet", "utf8mb4")
                .OldAnnotation("MySql:CharSet", "utf8mb4");

            // 6. Reverses Up 2.
            migrationBuilder.CreateIndex(
                name: "IX_BudgetItems_BudgetId_Name",
                table: "BudgetItems",
                columns: new[] { "BudgetId", "Name" },
                unique: true);
        }
    }
}
