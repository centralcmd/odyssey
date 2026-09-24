using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Odyssey.Context.Migrations
{
    /// <summary>
    /// Drops <c>Terms.TermKind</c>. <c>FoldRateTermKindsIntoFee</c> left every row at <c>Fee</c>, so the
    /// column no longer distinguishes anything and a term's series key is <c>(owner, LabelKey)</c>.
    ///
    /// <para>
    /// <b>Order matters.</b> Each new index is created BEFORE the old one it replaces is dropped, in
    /// both directions. InnoDB requires an index on a foreign-key column at all times, and the two
    /// indexes being swapped are the only ones leading with <c>AccountId</c> / <c>ContractId</c>; EF's
    /// scaffolded order (every <c>DropIndex</c> ahead of every <c>CreateIndex</c>) fails with errno
    /// 1553 — the defect <c>AccountTermLabelMigrationTests</c> pins for the earlier swap.
    /// </para>
    ///
    /// <para>
    /// <c>Down()</c> restores the column at <c>Fee</c> (10), which is what every row held before
    /// <c>Up()</c> ran.
    /// </para>
    /// </summary>
    public partial class RemoveTermKind : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Terms_AccountId_LabelKey_EffectiveFrom",
                table: "Terms",
                columns: new[] { "AccountId", "LabelKey", "EffectiveFrom" });

            migrationBuilder.CreateIndex(
                name: "IX_Terms_ContractId_LabelKey_EffectiveFrom",
                table: "Terms",
                columns: new[] { "ContractId", "LabelKey", "EffectiveFrom" });

            migrationBuilder.DropIndex(
                name: "IX_Terms_AccountId_TermKind_LabelKey_EffectiveFrom",
                table: "Terms");

            migrationBuilder.DropIndex(
                name: "IX_Terms_ContractId_TermKind_LabelKey_EffectiveFrom",
                table: "Terms");

            migrationBuilder.DropColumn(
                name: "TermKind",
                table: "Terms");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "TermKind",
                table: "Terms",
                type: "int",
                nullable: false,
                defaultValue: 10);

            migrationBuilder.CreateIndex(
                name: "IX_Terms_AccountId_TermKind_LabelKey_EffectiveFrom",
                table: "Terms",
                columns: new[] { "AccountId", "TermKind", "LabelKey", "EffectiveFrom" });

            migrationBuilder.CreateIndex(
                name: "IX_Terms_ContractId_TermKind_LabelKey_EffectiveFrom",
                table: "Terms",
                columns: new[] { "ContractId", "TermKind", "LabelKey", "EffectiveFrom" });

            migrationBuilder.DropIndex(
                name: "IX_Terms_AccountId_LabelKey_EffectiveFrom",
                table: "Terms");

            migrationBuilder.DropIndex(
                name: "IX_Terms_ContractId_LabelKey_EffectiveFrom",
                table: "Terms");
        }
    }
}
