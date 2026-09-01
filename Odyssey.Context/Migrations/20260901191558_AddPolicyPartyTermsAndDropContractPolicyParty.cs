using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Odyssey.Context.Migrations
{
    /// <inheritdoc />
    public partial class AddPolicyPartyTermsAndDropContractPolicyParty : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // A party whose ONLY target was a policy has no target left once the column goes, and the
            // re-added CHECK (which MariaDB validates against existing rows) would then refuse to
            // create. The rows are deleted while the column still exists, so the constraint is added
            // against a table that already satisfies it. Deleting is the only option that keeps the
            // XOR invariant: there is no other target to fall back to.
            migrationBuilder.Sql(
                "DELETE FROM `ContractParties` WHERE `InsurancePolicyId` IS NOT NULL;");

            migrationBuilder.DropForeignKey(
                name: "FK_ContractParties_InsurancePolicies_InsurancePolicyId",
                table: "ContractParties");

            migrationBuilder.DropIndex(
                name: "IX_ContractParties_InsurancePolicyId",
                table: "ContractParties");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ContractParties_ExactlyOneTarget",
                table: "ContractParties");

            migrationBuilder.DropColumn(
                name: "InsurancePolicyId",
                table: "ContractParties");

            migrationBuilder.AddColumn<DateTime>(
                name: "FromDate",
                table: "InsurancePolicyInsurers",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ToDate",
                table: "InsurancePolicyInsurers",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "FromDate",
                table: "InsurancePolicyInsuredContacts",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ToDate",
                table: "InsurancePolicyInsuredContacts",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "FromDate",
                table: "InsurancePolicyInsuredAccounts",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ToDate",
                table: "InsurancePolicyInsuredAccounts",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "FromDate",
                table: "InsurancePolicyBeneficiaries",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ToDate",
                table: "InsurancePolicyBeneficiaries",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_ContractParties_ExactlyOneTarget",
                table: "ContractParties",
                sql: "((`AccountId` IS NOT NULL) + (`ContactId` IS NOT NULL)) = 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_ContractParties_ExactlyOneTarget",
                table: "ContractParties");

            migrationBuilder.DropColumn(
                name: "FromDate",
                table: "InsurancePolicyInsurers");

            migrationBuilder.DropColumn(
                name: "ToDate",
                table: "InsurancePolicyInsurers");

            migrationBuilder.DropColumn(
                name: "FromDate",
                table: "InsurancePolicyInsuredContacts");

            migrationBuilder.DropColumn(
                name: "ToDate",
                table: "InsurancePolicyInsuredContacts");

            migrationBuilder.DropColumn(
                name: "FromDate",
                table: "InsurancePolicyInsuredAccounts");

            migrationBuilder.DropColumn(
                name: "ToDate",
                table: "InsurancePolicyInsuredAccounts");

            migrationBuilder.DropColumn(
                name: "FromDate",
                table: "InsurancePolicyBeneficiaries");

            migrationBuilder.DropColumn(
                name: "ToDate",
                table: "InsurancePolicyBeneficiaries");

            migrationBuilder.AddColumn<Guid>(
                name: "InsurancePolicyId",
                table: "ContractParties",
                type: "char(36)",
                nullable: true,
                collation: "ascii_general_ci");

            migrationBuilder.CreateIndex(
                name: "IX_ContractParties_InsurancePolicyId",
                table: "ContractParties",
                column: "InsurancePolicyId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ContractParties_ExactlyOneTarget",
                table: "ContractParties",
                sql: "((`AccountId` IS NOT NULL) + (`ContactId` IS NOT NULL) + (`InsurancePolicyId` IS NOT NULL)) = 1");

            migrationBuilder.AddForeignKey(
                name: "FK_ContractParties_InsurancePolicies_InsurancePolicyId",
                table: "ContractParties",
                column: "InsurancePolicyId",
                principalTable: "InsurancePolicies",
                principalColumn: "InsurancePolicyId",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
