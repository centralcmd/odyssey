using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Odyssey.Context.Migrations
{
    /// <summary>
    /// Renames the <c>AccountTerms</c> table to <c>Terms</c> and reworks its cadence: the
    /// <c>BillingPeriod</c> column becomes <c>Interval</c>, and <c>IntervalCount</c> and
    /// <c>AnchorDate</c> join it (issue #120).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Hand-written, not as scaffolded.</b> EF emitted a <c>DropTable</c> + <c>CreateTable</c> pair
    /// for the rename, which destroys every stored row — the primary key and the very column the data
    /// remap below reads. The renames here are true renames for exactly that reason; verify this
    /// before regenerating.
    /// </para>
    /// <para>
    /// The foreign key is a drop/re-add because <c>MigrationBuilder</c> has no
    /// <c>RenameForeignKey</c>, and the primary key emits <b>no DDL at all</b>: MySQL and MariaDB
    /// assign no user-visible primary-key name (the constraint is always <c>PRIMARY</c>), so
    /// <c>PK_AccountTerms</c> → <c>PK_Terms</c> is a model-snapshot change whose only database
    /// counterpart would be a full table rebuild achieving nothing.
    /// </para>
    /// <para>
    /// The FK is dropped <em>before</em> the index is renamed and re-added after, so no statement
    /// operates on an index a foreign key currently depends on — and an <c>AccountId</c>-leading
    /// index is present throughout, since <c>RenameIndex</c> is in-place and InnoDB leaves a dropped
    /// FK's backing index behind. That is errno 1553, which
    /// <c>20260911101930_AddAccountTermLabel</c> already hit once.
    /// </para>
    /// </remarks>
    public partial class RenameAccountTermsToTermsAndReworkInterval : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameTable(
                name: "AccountTerms",
                newName: "Terms");

            migrationBuilder.RenameColumn(
                name: "AccountTermId",
                table: "Terms",
                newName: "TermId");

            migrationBuilder.RenameColumn(
                name: "BillingPeriod",
                table: "Terms",
                newName: "Interval");

            // Nullable with no default, so every existing row lands at NULL. That is the correct
            // post-state for all of them: no historical row has ever recorded an anchor, and copying
            // EffectiveFrom across would fabricate a distinction the user never made.
            migrationBuilder.AddColumn<int>(
                name: "IntervalCount",
                table: "Terms",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "AnchorDate",
                table: "Terms",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.DropForeignKey(
                name: "FK_AccountTerms_Accounts_AccountId",
                table: "Terms");

            migrationBuilder.RenameIndex(
                name: "IX_AccountTerms_AccountId_TermKind_LabelKey_EffectiveFrom",
                table: "Terms",
                newName: "IX_Terms_AccountId_TermKind_LabelKey_EffectiveFrom");

            migrationBuilder.AddForeignKey(
                name: "FK_Terms_Accounts_AccountId",
                table: "Terms",
                column: "AccountId",
                principalTable: "Accounts",
                principalColumn: "AccountId",
                onDelete: ReferentialAction.Cascade);

            // The retired ordinal 4 (Quarterly) becomes Monthly x 3, in ONE statement so the pair can
            // never be half-applied. Literal ordinals, never enum names — a later rename must not be
            // able to change what this migration did. The predicate is its own complement, so the
            // statement is provably idempotent: afterwards no row satisfies `Interval` = 4.
            // INTERVAL is a reserved word on MariaDB, hence the backticks.
            migrationBuilder.Sql("""
                UPDATE `Terms`
                   SET `Interval` = 3, `IntervalCount` = 3
                 WHERE `Interval` = 4;
                """);

            // Rows that were ALREADY periodic get the identity count, so the service's "non-null iff
            // periodic" invariant holds over pre-existing data and a read-modify-write of an
            // untouched old row is not refused. The IN list is the set that was periodic BEFORE this
            // migration — Weekly (7) is deliberately absent, since no pre-existing row can hold an
            // ordinal this migration introduces. It runs after the remap above, so the rewritten
            // quarterly rows already hold 3 and are excluded by IS NULL.
            migrationBuilder.Sql("""
                UPDATE `Terms`
                   SET `IntervalCount` = 1
                 WHERE `Interval` IN (2, 3, 5) AND `IntervalCount` IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The data remap is deliberately NOT reversed: inventing a Quarterly back-conversion
            // would fabricate data on rows a user may have authored as Monthly x 3 on purpose. Down
            // is a developer convenience here, and the one-way character belongs in the release notes.
            migrationBuilder.DropForeignKey(
                name: "FK_Terms_Accounts_AccountId",
                table: "Terms");

            migrationBuilder.RenameIndex(
                name: "IX_Terms_AccountId_TermKind_LabelKey_EffectiveFrom",
                table: "Terms",
                newName: "IX_AccountTerms_AccountId_TermKind_LabelKey_EffectiveFrom");

            migrationBuilder.AddForeignKey(
                name: "FK_AccountTerms_Accounts_AccountId",
                table: "Terms",
                column: "AccountId",
                principalTable: "Accounts",
                principalColumn: "AccountId",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.DropColumn(
                name: "AnchorDate",
                table: "Terms");

            migrationBuilder.DropColumn(
                name: "IntervalCount",
                table: "Terms");

            migrationBuilder.RenameColumn(
                name: "Interval",
                table: "Terms",
                newName: "BillingPeriod");

            migrationBuilder.RenameColumn(
                name: "TermId",
                table: "Terms",
                newName: "AccountTermId");

            migrationBuilder.RenameTable(
                name: "Terms",
                newName: "AccountTerms");
        }
    }
}
