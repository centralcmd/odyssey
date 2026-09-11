using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Odyssey.Context.Migrations
{
    /// <summary>
    /// Gives an account term a user-authored <c>Label</c> and the folded <c>LabelKey</c> that carries
    /// the series key, and widens the term index to <c>(AccountId, TermKind, LabelKey, EffectiveFrom)</c>.
    ///
    /// <para>
    /// <b>No backfill.</b> Existing rows keep <c>NULL</c>, which is exactly the unnamed series they
    /// already were, so every pre-existing term stays semantically what it was.
    /// </para>
    ///
    /// <para>
    /// <b>The operation order is hand-corrected and must stay that way.</b> InnoDB requires an index on
    /// a foreign-key column at all times, and EF scaffolds every <c>DropIndex</c> ahead of every
    /// <c>CreateIndex</c>. <c>IX_AccountTerms_AccountId_TermKind_EffectiveFrom</c> is the only index
    /// leading with <c>AccountId</c> — EF's FK-index convention dropped the standalone one when that
    /// composite was created — so the scaffolded order leaves
    /// <c>FK_AccountTerms_Accounts_AccountId</c> with nothing to lean on and fails with errno 1553.
    /// The replacement is therefore created BEFORE the original is dropped, and <c>Down()</c> mirrors
    /// it. Same defect and fix as the <c>IX_Transactions_AccountId</c> reorder in #46. Verify with
    /// <c>dotnet ef migrations script</c>: the emitted DDL must show <c>CREATE INDEX</c> ahead of
    /// <c>DROP INDEX</c>.
    /// </para>
    /// </summary>
    public partial class AddAccountTermLabel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Label",
                table: "AccountTerms",
                type: "varchar(64)",
                maxLength: 64,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "LabelKey",
                table: "AccountTerms",
                type: "varchar(64)",
                maxLength: 64,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            // Created first: it carries the (AccountId, …) prefix the foreign key needs, so the key is
            // never left without a usable index.
            migrationBuilder.CreateIndex(
                name: "IX_AccountTerms_AccountId_TermKind_LabelKey_EffectiveFrom",
                table: "AccountTerms",
                columns: new[] { "AccountId", "TermKind", "LabelKey", "EffectiveFrom" });

            migrationBuilder.DropIndex(
                name: "IX_AccountTerms_AccountId_TermKind_EffectiveFrom",
                table: "AccountTerms");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Mirrored: the narrower index comes back before the wider one goes away.
            migrationBuilder.CreateIndex(
                name: "IX_AccountTerms_AccountId_TermKind_EffectiveFrom",
                table: "AccountTerms",
                columns: new[] { "AccountId", "TermKind", "EffectiveFrom" });

            migrationBuilder.DropIndex(
                name: "IX_AccountTerms_AccountId_TermKind_LabelKey_EffectiveFrom",
                table: "AccountTerms");

            migrationBuilder.DropColumn(
                name: "Label",
                table: "AccountTerms");

            migrationBuilder.DropColumn(
                name: "LabelKey",
                table: "AccountTerms");
        }
    }
}
