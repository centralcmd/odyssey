using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Odyssey.Context.Migrations
{
    /// <summary>
    /// Folds the two rate kinds (<c>InterestRate = 1</c>, <c>ExpectedReturn = 2</c>) and any
    /// <c>Unknown = 0</c> or otherwise unrecognised row into <c>TermKind.Fee</c> (10), moving what the
    /// kind said into the term's own <c>Label</c> — the same shape <c>CollapseFeeTermKinds</c> used
    /// for the four fee kinds.
    ///
    /// <para>
    /// <b>A data migration with no schema change.</b> <c>TermKind</c> persists as an int, so EF
    /// scaffolds this migration empty and the SQL is written into that shell by hand.
    /// </para>
    ///
    /// <para>
    /// <b>Order matters.</b> The kind's name is backfilled into <c>Label</c>/<c>LabelKey</c> <em>where
    /// the row has none</em> first — a rate never carried a label, so in practice that is every rate
    /// row — and only then is the kind remapped. The value, unit and dates are untouched, so a rate
    /// keeps reading as the same percentage. If an owner already had a fee named "Interest rate" or
    /// "Expected return", the converted rate joins that series; the index is not unique and the
    /// resolver tie-breaks on <c>CreatedAtUtc</c>, so that is a merge, never a failure or data loss.
    /// </para>
    ///
    /// <para>
    /// <b><c>Down()</c> is lossy by construction.</b> The old kind survives only in the exact label
    /// <c>Up()</c> wrote, so a fee a user had named "Interest rate" themselves reverts to a rate.
    /// </para>
    /// </summary>
    public partial class FoldRateTermKindsIntoFee : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1 — Backfill the old kind's name as the label, only where the row is unnamed.
            migrationBuilder.Sql(
                "UPDATE `Terms` SET `Label` = 'Interest rate', `LabelKey` = 'interest rate' " +
                "WHERE `TermKind` = 1 AND `LabelKey` IS NULL;");
            migrationBuilder.Sql(
                "UPDATE `Terms` SET `Label` = 'Expected return', `LabelKey` = 'expected return' " +
                "WHERE `TermKind` = 2 AND `LabelKey` IS NULL;");
            migrationBuilder.Sql(
                "UPDATE `Terms` SET `Label` = 'Unspecified', `LabelKey` = 'unspecified' " +
                "WHERE `TermKind` NOT IN (1, 2, 10) AND `LabelKey` IS NULL;");

            // 2 — Every remaining non-fee row becomes a fee.
            migrationBuilder.Sql(
                "UPDATE `Terms` SET `TermKind` = 10 WHERE `TermKind` <> 10;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Best-effort, and only from the exact labels Up() wrote. The rate kinds refused a label,
            // so the label is cleared again on the rows restored to a rate.
            migrationBuilder.Sql(
                "UPDATE `Terms` SET `TermKind` = 1, `Label` = NULL, `LabelKey` = NULL " +
                "WHERE `TermKind` = 10 AND `LabelKey` = 'interest rate';");
            migrationBuilder.Sql(
                "UPDATE `Terms` SET `TermKind` = 2, `Label` = NULL, `LabelKey` = NULL " +
                "WHERE `TermKind` = 10 AND `LabelKey` = 'expected return';");
            migrationBuilder.Sql(
                "UPDATE `Terms` SET `TermKind` = 0, `Label` = NULL, `LabelKey` = NULL " +
                "WHERE `TermKind` = 10 AND `LabelKey` = 'unspecified';");
        }
    }
}
