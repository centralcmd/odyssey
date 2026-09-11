using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Odyssey.Context.Migrations
{
    /// <summary>
    /// Collapses the four fee kinds into the single <c>TermKind.Fee</c>, moving what each one said
    /// into the term's own <c>Label</c>.
    ///
    /// <para>
    /// <b>A data migration with no schema change.</b> <c>TermKind</c> persists as an int, so EF
    /// scaffolds this migration empty and the SQL is written into that shell by hand. That is
    /// compatible with the always-scaffold rule — the migration is still generated; a data migration
    /// simply has nothing for EF to infer.
    /// </para>
    ///
    /// <para>
    /// <b>Order matters.</b> Each old kind's name is backfilled into <c>Label</c>/<c>LabelKey</c>
    /// <em>where the row has none</em> first, and only then are kinds 11, 12 and 99 remapped onto 10.
    /// <c>Fee</c> keeps ordinal 10 (the former <c>ManagementFee</c>), so those rows need a label but
    /// no remap. The four backfilled names are distinct, so no two remapped rows collapse into one
    /// series; a collision is possible only between two rows an operator had already labelled
    /// identically under different fee kinds on the same date, and since the index is not unique and
    /// the resolver tie-breaks on <c>CreatedAtUtc</c>, that yields a duplicate entry in one series
    /// rather than a failure or data loss.
    /// </para>
    ///
    /// <para>
    /// <b><c>Down()</c> is lossy by construction.</b> The old kind survives only in the exact name
    /// <c>Up()</c> wrote, so a fee a user had named themselves reverts to <c>ManagementFee</c>. That
    /// information no longer exists to restore.
    /// </para>
    /// </summary>
    public partial class CollapseFeeTermKinds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1 — Backfill the old kind's name as the label, only where the row is unnamed. A fee an
            //     operator had already named keeps their wording.
            migrationBuilder.Sql(
                "UPDATE `AccountTerms` SET `Label` = 'Management fee', `LabelKey` = 'management fee' " +
                "WHERE `TermKind` = 10 AND `LabelKey` IS NULL;");
            migrationBuilder.Sql(
                "UPDATE `AccountTerms` SET `Label` = 'Service fee', `LabelKey` = 'service fee' " +
                "WHERE `TermKind` = 11 AND `LabelKey` IS NULL;");
            migrationBuilder.Sql(
                "UPDATE `AccountTerms` SET `Label` = 'Transaction fee', `LabelKey` = 'transaction fee' " +
                "WHERE `TermKind` = 12 AND `LabelKey` IS NULL;");
            migrationBuilder.Sql(
                "UPDATE `AccountTerms` SET `Label` = 'Other fee', `LabelKey` = 'other fee' " +
                "WHERE `TermKind` = 99 AND `LabelKey` IS NULL;");

            // 2 — Remap the three surviving fee ordinals onto Fee (10).
            migrationBuilder.Sql(
                "UPDATE `AccountTerms` SET `TermKind` = 10 WHERE `TermKind` IN (11, 12, 99);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Best-effort, and only from the exact names Up() wrote: a fee the user named themselves
            // has no recoverable kind and stays at 10 (ManagementFee).
            migrationBuilder.Sql(
                "UPDATE `AccountTerms` SET `TermKind` = 11 WHERE `TermKind` = 10 AND `LabelKey` = 'service fee';");
            migrationBuilder.Sql(
                "UPDATE `AccountTerms` SET `TermKind` = 12 WHERE `TermKind` = 10 AND `LabelKey` = 'transaction fee';");
            migrationBuilder.Sql(
                "UPDATE `AccountTerms` SET `TermKind` = 99 WHERE `TermKind` = 10 AND `LabelKey` = 'other fee';");

            // The labels this migration itself wrote are removed again; anything else is the user's
            // own wording and is left alone.
            migrationBuilder.Sql(
                "UPDATE `AccountTerms` SET `Label` = NULL, `LabelKey` = NULL " +
                "WHERE `TermKind` IN (10, 11, 12, 99) " +
                "AND `LabelKey` IN ('management fee', 'service fee', 'transaction fee', 'other fee');");
        }
    }
}
