using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Odyssey.Context.Migrations
{
    /// <summary>
    /// Adds a <c>RESTRICT</c> foreign key to <c>Currencies</c> from every currency-code column that
    /// lacked one (issue #241), so a currency still in use can no longer be deleted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This migration writes data.</b> Until now nothing stopped a currency being deleted while rows
    /// still named it, so a production database may hold codes with no <c>Currencies</c> row, and
    /// adding a key over them fails. Before any key is added, every such orphaned code is restored as
    /// an <em>active</em> currency named <c>Restored currency XYZ (review)</c> with two minor units — the
    /// non-destructive repair: no referencing row is changed or removed, and referential integrity is
    /// re-established by putting back the one row that went missing. Active rather than archived,
    /// because every write path validates its currency with <c>EnsureSupportedAndActive</c>, and an
    /// archived restoration would leave those rows exactly as un-editable as the bug left them. An
    /// administrator should review every <c>Restored currency%</c> row after upgrading: correct the
    /// name and minor units, or move the rows and delete it.
    /// </para>
    /// <para>
    /// Every orphaned code is restored, a blank or non-ISO-shaped one included. Skipping such a code
    /// would not remove it from the referencing row, so the key's own addition would fail on it and the
    /// upgrade would stop there; restoring it keeps the upgrade going and puts the oddity on the
    /// currencies page, flagged by its name, where an administrator can see and resolve it. No write
    /// path produces such a code — they all normalise and validate — so only a hand edit could.
    /// </para>
    /// <para>
    /// The repair is idempotent (<c>NOT EXISTS</c>), so a replay after an interrupted run is safe.
    /// <c>UNION</c> de-duplicates under the columns' own collation — the same comparison the keys use —
    /// so two spellings the key would treat as one code (<c>nok</c>/<c>NOK</c> under the default
    /// case-insensitive collation) yield one insert, not a duplicate-key failure.
    /// <c>Down()</c> drops the keys and indexes only; the restored currencies stay, as they are what
    /// the referencing rows needed all along.
    /// </para>
    /// <para>
    /// <b>Adding a foreign key rebuilds the table.</b> With <c>foreign_key_checks</c> on, MariaDB can only
    /// add one with <c>ALGORITHM=COPY</c>, which copies the table and blocks writes to it for the
    /// duration. All eight tables are rebuilt, <c>Transactions</c> — the largest — among them, so
    /// schedule the upgrade for a quiet window on a large database.
    /// </para>
    /// </remarks>
    public partial class AddCurrencyForeignKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                INSERT INTO `Currencies` (`CurrencyCode`, `Name`, `MinorUnits`, `Symbol`, `Archived`)
                SELECT orphan.`Code`, CONCAT('Restored currency ', orphan.`Code`, ' (review)'), 2, NULL, NULL
                FROM (
                    SELECT `CurrencyCode` AS `Code` FROM `Accounts`
                    UNION SELECT `CurrencyCode` FROM `Transactions`
                    UNION SELECT `BaseCurrencyCode` FROM `Budgets`
                    UNION SELECT `BaseCurrencyCode` FROM `TaxStatements`
                    UNION SELECT `CurrencyCode` FROM `Properties`
                    UNION SELECT `CurrencyCode` FROM `AccountEstimates`
                    UNION SELECT `CurrencyCode` FROM `PropertyEstimates`
                    UNION SELECT `CurrencyCode` FROM `Terms`
                ) AS orphan
                WHERE orphan.`Code` IS NOT NULL
                  AND NOT EXISTS (
                      SELECT 1 FROM `Currencies` AS c WHERE c.`CurrencyCode` = orphan.`Code`);
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Terms_CurrencyCode",
                table: "Terms",
                column: "CurrencyCode");

            migrationBuilder.CreateIndex(
                name: "IX_TaxStatements_BaseCurrencyCode",
                table: "TaxStatements",
                column: "BaseCurrencyCode");

            migrationBuilder.CreateIndex(
                name: "IX_PropertyEstimates_CurrencyCode",
                table: "PropertyEstimates",
                column: "CurrencyCode");

            migrationBuilder.CreateIndex(
                name: "IX_Properties_CurrencyCode",
                table: "Properties",
                column: "CurrencyCode");

            migrationBuilder.CreateIndex(
                name: "IX_Budgets_BaseCurrencyCode",
                table: "Budgets",
                column: "BaseCurrencyCode");

            migrationBuilder.CreateIndex(
                name: "IX_Accounts_CurrencyCode",
                table: "Accounts",
                column: "CurrencyCode");

            migrationBuilder.CreateIndex(
                name: "IX_AccountEstimates_CurrencyCode",
                table: "AccountEstimates",
                column: "CurrencyCode");

            migrationBuilder.AddForeignKey(
                name: "FK_AccountEstimates_Currencies_CurrencyCode",
                table: "AccountEstimates",
                column: "CurrencyCode",
                principalTable: "Currencies",
                principalColumn: "CurrencyCode",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Accounts_Currencies_CurrencyCode",
                table: "Accounts",
                column: "CurrencyCode",
                principalTable: "Currencies",
                principalColumn: "CurrencyCode",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Budgets_Currencies_BaseCurrencyCode",
                table: "Budgets",
                column: "BaseCurrencyCode",
                principalTable: "Currencies",
                principalColumn: "CurrencyCode",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Properties_Currencies_CurrencyCode",
                table: "Properties",
                column: "CurrencyCode",
                principalTable: "Currencies",
                principalColumn: "CurrencyCode",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_PropertyEstimates_Currencies_CurrencyCode",
                table: "PropertyEstimates",
                column: "CurrencyCode",
                principalTable: "Currencies",
                principalColumn: "CurrencyCode",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_TaxStatements_Currencies_BaseCurrencyCode",
                table: "TaxStatements",
                column: "BaseCurrencyCode",
                principalTable: "Currencies",
                principalColumn: "CurrencyCode",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Terms_Currencies_CurrencyCode",
                table: "Terms",
                column: "CurrencyCode",
                principalTable: "Currencies",
                principalColumn: "CurrencyCode",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Transactions_Currencies_CurrencyCode",
                table: "Transactions",
                column: "CurrencyCode",
                principalTable: "Currencies",
                principalColumn: "CurrencyCode",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AccountEstimates_Currencies_CurrencyCode",
                table: "AccountEstimates");

            migrationBuilder.DropForeignKey(
                name: "FK_Accounts_Currencies_CurrencyCode",
                table: "Accounts");

            migrationBuilder.DropForeignKey(
                name: "FK_Budgets_Currencies_BaseCurrencyCode",
                table: "Budgets");

            migrationBuilder.DropForeignKey(
                name: "FK_Properties_Currencies_CurrencyCode",
                table: "Properties");

            migrationBuilder.DropForeignKey(
                name: "FK_PropertyEstimates_Currencies_CurrencyCode",
                table: "PropertyEstimates");

            migrationBuilder.DropForeignKey(
                name: "FK_TaxStatements_Currencies_BaseCurrencyCode",
                table: "TaxStatements");

            migrationBuilder.DropForeignKey(
                name: "FK_Terms_Currencies_CurrencyCode",
                table: "Terms");

            migrationBuilder.DropForeignKey(
                name: "FK_Transactions_Currencies_CurrencyCode",
                table: "Transactions");

            migrationBuilder.DropIndex(
                name: "IX_Terms_CurrencyCode",
                table: "Terms");

            migrationBuilder.DropIndex(
                name: "IX_TaxStatements_BaseCurrencyCode",
                table: "TaxStatements");

            migrationBuilder.DropIndex(
                name: "IX_PropertyEstimates_CurrencyCode",
                table: "PropertyEstimates");

            migrationBuilder.DropIndex(
                name: "IX_Properties_CurrencyCode",
                table: "Properties");

            migrationBuilder.DropIndex(
                name: "IX_Budgets_BaseCurrencyCode",
                table: "Budgets");

            migrationBuilder.DropIndex(
                name: "IX_Accounts_CurrencyCode",
                table: "Accounts");

            migrationBuilder.DropIndex(
                name: "IX_AccountEstimates_CurrencyCode",
                table: "AccountEstimates");
        }
    }
}
