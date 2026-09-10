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
            migrationBuilder.DropIndex(
                name: "IX_AccountTerms_AccountId_TermKind_EffectiveFrom",
                table: "AccountTerms");

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

            migrationBuilder.CreateIndex(
                name: "IX_AccountTerms_AccountId_TermKind_LabelKey_EffectiveFrom",
                table: "AccountTerms",
                columns: new[] { "AccountId", "TermKind", "LabelKey", "EffectiveFrom" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AccountTerms_AccountId_TermKind_LabelKey_EffectiveFrom",
                table: "AccountTerms");

            migrationBuilder.DropColumn(
                name: "Label",
                table: "AccountTerms");

            migrationBuilder.DropColumn(
                name: "LabelKey",
                table: "AccountTerms");

            migrationBuilder.CreateIndex(
                name: "IX_AccountTerms_AccountId_TermKind_EffectiveFrom",
                table: "AccountTerms",
                columns: new[] { "AccountId", "TermKind", "EffectiveFrom" });
        }
    }
}
