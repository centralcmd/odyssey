using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Odyssey.Context.Migrations
{
    /// <summary>
    /// The two signature stamps (issue #145) plus a data backfill that keeps every existing contract
    /// in the status it already had.
    ///
    /// <para>
    /// <b>Leaving the columns null is NOT the safe alternative.</b> The derivation reads a null
    /// <c>Signed</c> as unsigned, so an un-backfilled upgrade would flip every contract in the
    /// database to <c>Draft</c> and empty the run rate and the upcoming charges overnight. Backfilling
    /// is what makes the change invisible until a user marks something as a draft.
    /// </para>
    ///
    /// <para>
    /// <b>The value is fabricated, and that is a stated, accepted cost.</b> No backfilled stamp is a
    /// statement of fact. It is accepted on the same precondition that licenses this repository's
    /// migration squashes and that CLAUDE.md records — every <c>odyssey</c> database in existence is a
    /// local dev or test database rebuilt from <c>DemoDataSeeder</c>, so there is no real signature
    /// history to falsify. Release notes must still say the columns were backfilled and are not
    /// evidence of a signing date. The risk is reduced rather than merely deferred: the value is
    /// derived from each row's OWN dates rather than one shared clock reading stamped across the
    /// table, so the residual is a per-row approximation instead of a table-wide assertion.
    /// </para>
    /// </summary>
    public partial class AddContractSignatureDates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "Ready",
                table: "Contracts",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "Signed",
                table: "Contracts",
                type: "datetime(6)",
                nullable: true);

            // Every existing row becomes SIGNED, so the signature layer short-circuits nothing and the
            // date chain runs exactly as it does today: the run rate, the upcoming charges, the
            // ending-soon slice and all five existing counts are identical across the upgrade.
            //
            // Four properties, each load-bearing:
            //
            //  • Both columns take the SAME expression, so every backfilled row satisfies G2
            //    (Signed >= Ready) by construction. A row that fails its own guards would be unwritable
            //    by any later PUT that did not first repair it.
            //
            //  • The LEAST(…, CreatedAtUtc) clamp is NOT redundant with the COALESCE. A bare
            //    COALESCE(StartDate, CompletionDate, CreatedAtUtc) stamps a FUTURE Signed onto every
            //    Upcoming contract, violating G3 ("neither stamp may be dated in the future") on the
            //    very next write. CreatedAtUtc is required and always in the past, so the clamp makes
            //    every value G3-clean by construction while a backdated contract keeps its earlier
            //    StartDate. Read the whole expression as: signed no later than the day its term began,
            //    and no later than the day its row was created.
            //
            //  • It is raw SQL, not HasData. HasData seeds ROWS, not column values on existing rows,
            //    and cannot express a per-row expression over three other columns at all.
            //
            //  • WHERE Signed IS NULL makes it RE-RUNNABLE, so a migration interrupted after the
            //    column adds and re-applied per docs/migration-history-drift.md does not re-stamp rows
            //    a second time.
            migrationBuilder.Sql(
                """
                UPDATE `Contracts`
                SET `Ready`  = LEAST(COALESCE(`StartDate`, `CompletionDate`, `CreatedAtUtc`), `CreatedAtUtc`),
                    `Signed` = LEAST(COALESCE(`StartDate`, `CompletionDate`, `CreatedAtUtc`), `CreatedAtUtc`)
                WHERE `Signed` IS NULL;
                """);
        }

        /// <summary>
        /// Drops both columns. The backfilled values are not recoverable, which is correct — they were
        /// fabricated on the way up, so there is nothing to restore.
        /// </summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Ready",
                table: "Contracts");

            migrationBuilder.DropColumn(
                name: "Signed",
                table: "Contracts");
        }
    }
}
