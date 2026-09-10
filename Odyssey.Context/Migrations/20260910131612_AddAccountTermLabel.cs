using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Odyssey.Context.Migrations
{
    /// <inheritdoc />
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

            // InnoDB requires an index on a foreign-key column at all times, so this composite has to
            // exist BEFORE IX_AccountTerms_AccountId_TermKind_EffectiveFrom is dropped: that index is
            // the only one leading with AccountId, so FK_AccountTerms_Accounts_AccountId leans on it.
            // EF scaffolds every DropIndex ahead of every CreateIndex, which fails with "Cannot drop
            // index 'IX_AccountTerms_AccountId_TermKind_EffectiveFrom': needed in a foreign key
            // constraint" (errno 1553) — hence the hand-ordering here. Same defect as the
            // IX_Transactions_AccountId reorder in DropDeprecatedColumnsAndTuneIndexes.
            migrationBuilder.CreateIndex(
                name: "IX_AccountTerms_AccountId_TermKind_LabelKey_EffectiveFrom",
                table: "AccountTerms",
                columns: new[] { "AccountId", "TermKind", "LabelKey", "EffectiveFrom" });

            // Now redundant: the replacement leads with the same (AccountId, TermKind) prefix, so it
            // satisfies both the FK and the kind-filtered history query on its own.
            migrationBuilder.DropIndex(
                name: "IX_AccountTerms_AccountId_TermKind_EffectiveFrom",
                table: "AccountTerms");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Mirror image of the same constraint: recreate the narrower composite before dropping the
            // wider one, so the foreign key never loses its index.
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
