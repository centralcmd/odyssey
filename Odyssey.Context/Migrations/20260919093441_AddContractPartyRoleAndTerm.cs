using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Odyssey.Context.Migrations
{
    /// <summary>
    /// Gives every contract party a <c>Role</c> and an optional <c>FromDate</c>/<c>ToDate</c> term, and
    /// widens the party uniqueness rule from <i>(contract, target)</i> to <i>(contract, target, role)</i>
    /// with two real unique indexes (issue #121 §11).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Hand-edited: the pre-flight duplicate sweep is the literal first operation, before any DDL.</b>
    /// The <i>(contract, target)</i> uniqueness this migration builds on does not <em>hold</em> today —
    /// it is only <em>attempted</em>, by a check-then-act in <c>ContractService.EnsureNotDuplicateParty</c>
    /// with no transaction and no constraint behind it, so a race, a hand edit or a restore could have
    /// produced duplicate rows that nothing would ever have caught. The sweep names the offending pair
    /// rather than letting the migration die later on a bare <c>Duplicate entry</c>.
    /// </para>
    /// <para>
    /// Two things make the ordering load-bearing rather than stylistic. MariaDB commits DDL implicitly,
    /// so "nothing was applied" only holds if the sweep precedes the <c>AddColumn</c> calls too, not
    /// merely the <c>CreateIndex</c> ones. And the sweep runs against the <em>pre-migration</em> schema:
    /// it groups on <c>(ContractId, AccountId)</c> and <c>(ContractId, ContactId)</c> and never mentions
    /// <c>Role</c>, because the backfill sets <c>Role = 0</c> on every existing row uniformly, which
    /// makes a pre-migration <i>(contract, target)</i> duplicate exactly a post-migration
    /// <i>(contract, target, role)</i> duplicate. So the check needs nothing this migration adds, and
    /// "nothing applied on failure" is true by construction rather than by careful ordering of DDL that
    /// has already committed.
    /// </para>
    /// <para>
    /// It <b>reports, never repairs</b> — the rule <c>MigrationRunner</c> already follows for a
    /// half-applied migration, and for the same reason: which of two duplicate links to drop is the
    /// operator's call, not the migration's. Repair procedure:
    /// <c>docs/migration-history-drift.md</c>.
    /// </para>
    /// <para>
    /// <c>SIGNAL</c>'s <c>MESSAGE_TEXT</c> is capped at 128 characters and silently truncated beyond it,
    /// so the text is deliberately terse: it carries the two GUIDs, which are what an operator needs to
    /// find the rows, and nothing else.
    /// </para>
    /// </remarks>
    public partial class AddContractPartyRoleAndTerm : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                BEGIN NOT ATOMIC
                    DECLARE offending TEXT DEFAULT NULL;
                    DECLARE failure TEXT DEFAULT NULL;

                    SELECT CONCAT(`ContractId`, ' -> account ', `AccountId`)
                      INTO offending
                      FROM `ContractParties`
                     WHERE `AccountId` IS NOT NULL
                     GROUP BY `ContractId`, `AccountId`
                    HAVING COUNT(*) > 1
                     LIMIT 1;

                    IF offending IS NULL THEN
                        SELECT CONCAT(`ContractId`, ' -> contact ', `ContactId`)
                          INTO offending
                          FROM `ContractParties`
                         WHERE `ContactId` IS NOT NULL
                         GROUP BY `ContractId`, `ContactId`
                        HAVING COUNT(*) > 1
                         LIMIT 1;
                    END IF;

                    IF offending IS NOT NULL THEN
                        -- SIGNAL's MESSAGE_TEXT takes a SIMPLE VALUE, never an expression, so the
                        -- text is assembled into a local variable first. A CONCAT() written inline
                        -- there fails with "Undeclared variable: CONCAT" rather than raising the
                        -- intended error, which would turn a refusal into a confusing parse failure.
                        SET failure = CONCAT('Duplicate contract party link: ', offending);
                        SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = failure;
                    END IF;
                END
                """);

            // The default IS the backfill: every existing row becomes Unspecified in the same statement,
            // with no data-migration step and no second pass.
            migrationBuilder.AddColumn<int>(
                name: "Role",
                table: "ContractParties",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "FromDate",
                table: "ContractParties",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ToDate",
                table: "ContractParties",
                type: "datetime(6)",
                nullable: true);

            // From here on the database, not a racy service pre-check, is what makes the uniqueness rule
            // true. MariaDB treats NULL as distinct in a unique index, so the account-side index does not
            // constrain contact parties and vice versa — one table, two uniqueness rules, no discriminator.
            migrationBuilder.CreateIndex(
                name: "IX_ContractParties_ContractId_AccountId_Role",
                table: "ContractParties",
                columns: new[] { "ContractId", "AccountId", "Role" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ContractParties_ContractId_ContactId_Role",
                table: "ContractParties",
                columns: new[] { "ContractId", "ContactId", "Role" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ContractParties_ContractId_AccountId_Role",
                table: "ContractParties");

            migrationBuilder.DropIndex(
                name: "IX_ContractParties_ContractId_ContactId_Role",
                table: "ContractParties");

            migrationBuilder.DropColumn(
                name: "FromDate",
                table: "ContractParties");

            migrationBuilder.DropColumn(
                name: "Role",
                table: "ContractParties");

            migrationBuilder.DropColumn(
                name: "ToDate",
                table: "ContractParties");
        }
    }
}
