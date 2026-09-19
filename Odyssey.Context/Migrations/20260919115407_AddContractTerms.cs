using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Odyssey.Context.Migrations
{
    /// <summary>
    /// Widens <c>Terms</c> from one owner to two (issue #135): <c>AccountId</c> becomes nullable,
    /// <c>ContractId</c> arrives with a cascading FK to <c>Contracts</c>, the account index gains its
    /// contract mirror, and <c>CK_Terms_ExactlyOneOwner</c> backstops the invariant the domain service
    /// already guarantees.
    ///
    /// <para>
    /// <b>No backfill is needed.</b> Every existing row has a non-null <c>AccountId</c> and a null
    /// <c>ContractId</c>, so the CHECK constraint is satisfied the moment it is added — which is why
    /// step 4 may follow steps 1-2 in the same migration, the order EF generates. MariaDB commits DDL
    /// implicitly, so an interruption leaves an arbitrary prefix applied with no history row; that is
    /// the condition <c>MigrationRunner</c>'s pre-flight check reports, and this ordering is what makes
    /// a partial application recoverable by re-running (see docs/migration-history-drift.md).
    /// </para>
    /// </summary>
    public partial class AddContractTerms : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "AccountId",
                table: "Terms",
                type: "char(36)",
                nullable: true,
                collation: "ascii_general_ci",
                oldClrType: typeof(Guid),
                oldType: "char(36)")
                .OldAnnotation("Relational:Collation", "ascii_general_ci");

            migrationBuilder.AddColumn<Guid>(
                name: "ContractId",
                table: "Terms",
                type: "char(36)",
                nullable: true,
                collation: "ascii_general_ci");

            migrationBuilder.InsertData(
                table: "SystemSettings",
                columns: new[] { "Key", "UpdatedAt", "UpdatedBy", "Value" },
                values: new object[] { "ContractMaxTermsPerContract", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "500" });

            migrationBuilder.CreateIndex(
                name: "IX_Terms_ContractId_TermKind_LabelKey_EffectiveFrom",
                table: "Terms",
                columns: new[] { "ContractId", "TermKind", "LabelKey", "EffectiveFrom" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_Terms_ExactlyOneOwner",
                table: "Terms",
                sql: "((`AccountId` IS NOT NULL) + (`ContractId` IS NOT NULL)) = 1");

            migrationBuilder.AddForeignKey(
                name: "FK_Terms_Contracts_ContractId",
                table: "Terms",
                column: "ContractId",
                principalTable: "Contracts",
                principalColumn: "ContractId",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Terms_Contracts_ContractId",
                table: "Terms");

            migrationBuilder.DropIndex(
                name: "IX_Terms_ContractId_TermKind_LabelKey_EffectiveFrom",
                table: "Terms");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Terms_ExactlyOneOwner",
                table: "Terms");

            migrationBuilder.DeleteData(
                table: "SystemSettings",
                keyColumn: "Key",
                keyValue: "ContractMaxTermsPerContract");

            migrationBuilder.DropColumn(
                name: "ContractId",
                table: "Terms");

            migrationBuilder.AlterColumn<Guid>(
                name: "AccountId",
                table: "Terms",
                type: "char(36)",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                collation: "ascii_general_ci",
                oldClrType: typeof(Guid),
                oldType: "char(36)",
                oldNullable: true)
                .OldAnnotation("Relational:Collation", "ascii_general_ci");
        }
    }
}
