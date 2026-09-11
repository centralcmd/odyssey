using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Odyssey.Context.Migrations
{
    /// <summary>
    /// Collapses the four fee kinds into one and moves the taxonomy they carried into the term's
    /// <c>Label</c>, where it now belongs.
    /// </summary>
    /// <remarks>
    /// No schema change: <c>TermKind</c> persists as an int, so this is a pure data migration and EF
    /// scaffolds it empty. <c>Fee</c> keeps ordinal 10 (the old <c>ManagementFee</c>), so those rows
    /// need no remap — only a label. 11, 12 and 99 are remapped onto 10.
    ///
    /// The label backfill runs FIRST and only where the row has none, so a fee the user already named
    /// keeps that name. The four backfilled names are distinct, so no two remapped rows can collide
    /// into one series. A collision is possible in theory between two rows an operator had already
    /// labelled identically under different fee kinds on the same date; the index is not unique and
    /// the resolver tie-breaks on CreatedAtUtc, so that yields a duplicate entry in one series rather
    /// than a failure or data loss.
    ///
    /// Down() is best-effort by construction: the old kind is recoverable only from the exact name
    /// this migration wrote, so a fee the user named themselves reverts to ManagementFee rather than
    /// to whichever kind it began as. That information no longer exists to restore.
    /// </remarks>
    public partial class CollapseFeeTermKinds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1. Move the kind's meaning into the label, for rows that do not already have one.
            //    LabelKey is the case-folded form, matching what TermLabel.KeyOf would produce.
            migrationBuilder.Sql(
                "UPDATE `AccountTerms` SET `Label` = 'Management fee', `LabelKey` = 'management fee' " +
                "WHERE `TermKind` = 10 AND (`Label` IS NULL OR `Label` = '');");

            migrationBuilder.Sql(
                "UPDATE `AccountTerms` SET `Label` = 'Service fee', `LabelKey` = 'service fee' " +
                "WHERE `TermKind` = 11 AND (`Label` IS NULL OR `Label` = '');");

            migrationBuilder.Sql(
                "UPDATE `AccountTerms` SET `Label` = 'Transaction fee', `LabelKey` = 'transaction fee' " +
                "WHERE `TermKind` = 12 AND (`Label` IS NULL OR `Label` = '');");

            // OtherFee already required a label, so this is a no-op on data written through the app.
            // It is here for a row inserted by hand before that rule existed.
            migrationBuilder.Sql(
                "UPDATE `AccountTerms` SET `Label` = 'Other fee', `LabelKey` = 'other fee' " +
                "WHERE `TermKind` = 99 AND (`Label` IS NULL OR `Label` = '');");

            // 2. Every fee is now kind 10.
            migrationBuilder.Sql("UPDATE `AccountTerms` SET `TermKind` = 10 WHERE `TermKind` IN (11, 12, 99);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Restore the three remapped kinds from the exact names Up() wrote, clearing the label it
            // added. A fee labelled by a user stays kind 10 with its label intact — see the remarks.
            migrationBuilder.Sql(
                "UPDATE `AccountTerms` SET `TermKind` = 11, `Label` = NULL, `LabelKey` = NULL " +
                "WHERE `TermKind` = 10 AND `LabelKey` = 'service fee';");

            migrationBuilder.Sql(
                "UPDATE `AccountTerms` SET `TermKind` = 12, `Label` = NULL, `LabelKey` = NULL " +
                "WHERE `TermKind` = 10 AND `LabelKey` = 'transaction fee';");

            migrationBuilder.Sql(
                "UPDATE `AccountTerms` SET `TermKind` = 99, `Label` = NULL, `LabelKey` = NULL " +
                "WHERE `TermKind` = 10 AND `LabelKey` = 'other fee';");

            migrationBuilder.Sql(
                "UPDATE `AccountTerms` SET `Label` = NULL, `LabelKey` = NULL " +
                "WHERE `TermKind` = 10 AND `LabelKey` = 'management fee';");
        }
    }
}
