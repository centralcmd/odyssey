using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Odyssey.Context.Migrations
{
    /// <summary>
    /// Replaces <c>InsurancePolicy.InsurerId</c> / <c>.InsuredAccountId</c> with four link collections
    /// (issue #27): insurers, insured accounts, insured contacts and beneficiaries.
    ///
    /// <para>
    /// The order is <b>create → backfill → verify → drop</b>, and it is load-bearing: MariaDB commits
    /// DDL implicitly, so an interruption after the create or the backfill leaves the new tables
    /// populated and the old columns still present — a state <c>MigrationRunner</c>'s pre-check reports
    /// rather than replaying blindly (this migration contains <c>CreateTableOperation</c>s, so
    /// <c>MigrationRunner.CreatedBy</c> actually populates). Repair procedure:
    /// <c>docs/migration-history-drift.md</c>.
    /// </para>
    /// </summary>
    public partial class AddInsurancePolicyLinkCollections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ── 1. Create ────────────────────────────────────────────────────────────────────────
            migrationBuilder.CreateTable(
                name: "InsurancePolicyBeneficiaries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    InsurancePolicyId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    ContactId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    CreatedByUserId = table.Column<string>(type: "varchar(255)", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InsurancePolicyBeneficiaries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InsurancePolicyBeneficiaries_AspNetUsers_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_InsurancePolicyBeneficiaries_Contacts_ContactId",
                        column: x => x.ContactId,
                        principalTable: "Contacts",
                        principalColumn: "ContactId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InsurancePolicyBeneficiaries_InsurancePolicies_InsurancePoli~",
                        column: x => x.InsurancePolicyId,
                        principalTable: "InsurancePolicies",
                        principalColumn: "InsurancePolicyId",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "InsurancePolicyInsuredAccounts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    InsurancePolicyId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    AccountId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InsurancePolicyInsuredAccounts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InsurancePolicyInsuredAccounts_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "AccountId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_InsurancePolicyInsuredAccounts_InsurancePolicies_InsurancePo~",
                        column: x => x.InsurancePolicyId,
                        principalTable: "InsurancePolicies",
                        principalColumn: "InsurancePolicyId",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "InsurancePolicyInsuredContacts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    InsurancePolicyId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    ContactId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InsurancePolicyInsuredContacts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InsurancePolicyInsuredContacts_Contacts_ContactId",
                        column: x => x.ContactId,
                        principalTable: "Contacts",
                        principalColumn: "ContactId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InsurancePolicyInsuredContacts_InsurancePolicies_InsurancePo~",
                        column: x => x.InsurancePolicyId,
                        principalTable: "InsurancePolicies",
                        principalColumn: "InsurancePolicyId",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "InsurancePolicyInsurers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    InsurancePolicyId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    ContactId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InsurancePolicyInsurers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InsurancePolicyInsurers_Contacts_ContactId",
                        column: x => x.ContactId,
                        principalTable: "Contacts",
                        principalColumn: "ContactId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InsurancePolicyInsurers_InsurancePolicies_InsurancePolicyId",
                        column: x => x.InsurancePolicyId,
                        principalTable: "InsurancePolicies",
                        principalColumn: "InsurancePolicyId",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_InsurancePolicyBeneficiaries_ContactId",
                table: "InsurancePolicyBeneficiaries",
                column: "ContactId");

            migrationBuilder.CreateIndex(
                name: "IX_InsurancePolicyBeneficiaries_CreatedByUserId",
                table: "InsurancePolicyBeneficiaries",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_InsurancePolicyBeneficiaries_InsurancePolicyId_ContactId",
                table: "InsurancePolicyBeneficiaries",
                columns: new[] { "InsurancePolicyId", "ContactId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InsurancePolicyInsuredAccounts_AccountId",
                table: "InsurancePolicyInsuredAccounts",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_InsurancePolicyInsuredAccounts_InsurancePolicyId_AccountId",
                table: "InsurancePolicyInsuredAccounts",
                columns: new[] { "InsurancePolicyId", "AccountId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InsurancePolicyInsuredContacts_ContactId",
                table: "InsurancePolicyInsuredContacts",
                column: "ContactId");

            migrationBuilder.CreateIndex(
                name: "IX_InsurancePolicyInsuredContacts_InsurancePolicyId_ContactId",
                table: "InsurancePolicyInsuredContacts",
                columns: new[] { "InsurancePolicyId", "ContactId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InsurancePolicyInsurers_ContactId",
                table: "InsurancePolicyInsurers",
                column: "ContactId");

            migrationBuilder.CreateIndex(
                name: "IX_InsurancePolicyInsurers_InsurancePolicyId_ContactId",
                table: "InsurancePolicyInsurers",
                columns: new[] { "InsurancePolicyId", "ContactId" },
                unique: true);

            // ── 2. Backfill ──────────────────────────────────────────────────────────────────────
            // The PK is a Guid with DatabaseGeneratedOption.Identity, which for these entities means EF
            // supplies the value on insert — a raw INSERT … SELECT bypasses EF entirely and must supply
            // its own, so the backfill uses MariaDB's UUID() per row. That makes it non-deterministic
            // across databases, which is fine here because NOTHING references a link row's Id: no
            // foreign key points at one, and the real invariant lives on the (PolicyId, TargetId)
            // unique index.
            //
            // Written idempotently (INSERT … SELECT … WHERE NOT EXISTS) so re-running inserts nothing
            // rather than dying on that unique index. Note this is NOT for transient-fault replay:
            // MigrationRunner wraps its drift guard AND MigrateAsync in one execution strategy, so a
            // replay re-runs the guard first, sees the tables this migration already created, and
            // throws MigrationDriftException before ever reaching here. The path that genuinely
            // exercises the idempotency is MANUAL REPAIR, where an operator following
            // docs/migration-history-drift.md re-applies a partially-applied migration by hand.
            migrationBuilder.Sql(@"
                INSERT INTO `InsurancePolicyInsurers` (`Id`, `InsurancePolicyId`, `ContactId`)
                SELECT UUID(), p.`InsurancePolicyId`, p.`InsurerId`
                FROM `InsurancePolicies` p
                WHERE p.`InsurerId` IS NOT NULL
                  AND NOT EXISTS (
                    SELECT 1 FROM `InsurancePolicyInsurers` l
                    WHERE l.`InsurancePolicyId` = p.`InsurancePolicyId` AND l.`ContactId` = p.`InsurerId`);");

            migrationBuilder.Sql(@"
                INSERT INTO `InsurancePolicyInsuredAccounts` (`Id`, `InsurancePolicyId`, `AccountId`)
                SELECT UUID(), p.`InsurancePolicyId`, p.`InsuredAccountId`
                FROM `InsurancePolicies` p
                WHERE p.`InsuredAccountId` IS NOT NULL
                  AND NOT EXISTS (
                    SELECT 1 FROM `InsurancePolicyInsuredAccounts` l
                    WHERE l.`InsurancePolicyId` = p.`InsurancePolicyId` AND l.`AccountId` = p.`InsuredAccountId`);");

            // ── 3. Verify, with a statement that can actually fail ────────────────────────────────
            // On MariaDB a SELECT that returns rows fails NOTHING — the migration would sail straight
            // past a failed backfill and then drop the source columns. SIGNAL SQLSTATE '45000' aborts;
            // and because SIGNAL is not valid inside a bare IF outside a stored program, it is wrapped
            // in BEGIN NOT ATOMIC … END (the compound statement MariaDB 10.1+ supports outside a
            // procedure). A verify that cannot fail is worse than no verify, because it reads as
            // protection.
            migrationBuilder.Sql(@"
                BEGIN NOT ATOMIC
                    DECLARE orphans INT;
                    SELECT COUNT(*) INTO orphans
                    FROM `InsurancePolicies` p
                    WHERE (p.`InsurerId` IS NOT NULL AND NOT EXISTS (
                            SELECT 1 FROM `InsurancePolicyInsurers` l
                            WHERE l.`InsurancePolicyId` = p.`InsurancePolicyId` AND l.`ContactId` = p.`InsurerId`))
                       OR (p.`InsuredAccountId` IS NOT NULL AND NOT EXISTS (
                            SELECT 1 FROM `InsurancePolicyInsuredAccounts` l
                            WHERE l.`InsurancePolicyId` = p.`InsurancePolicyId` AND l.`AccountId` = p.`InsuredAccountId`));
                    IF orphans > 0 THEN
                        SIGNAL SQLSTATE '45000'
                            SET MESSAGE_TEXT = 'AddInsurancePolicyLinkCollections: insurer/insured-account backfill is incomplete; refusing to drop InsurerId and InsuredAccountId.';
                    END IF;
                END;");

            // ── 4. Drop the two scalar columns, their indexes and their FKs ──────────────────────
            migrationBuilder.DropForeignKey(
                name: "FK_InsurancePolicies_Accounts_InsuredAccountId",
                table: "InsurancePolicies");

            migrationBuilder.DropForeignKey(
                name: "FK_InsurancePolicies_Contacts_InsurerId",
                table: "InsurancePolicies");

            migrationBuilder.DropIndex(
                name: "IX_InsurancePolicies_InsuredAccountId",
                table: "InsurancePolicies");

            migrationBuilder.DropIndex(
                name: "IX_InsurancePolicies_InsurerId",
                table: "InsurancePolicies");

            migrationBuilder.DropColumn(
                name: "InsuredAccountId",
                table: "InsurancePolicies");

            migrationBuilder.DropColumn(
                name: "InsurerId",
                table: "InsurancePolicies");

            // ── 5. Seed the new admin-editable cap ───────────────────────────────────────────────
            migrationBuilder.InsertData(
                table: "SystemSettings",
                columns: new[] { "Key", "UpdatedAt", "UpdatedBy", "Value" },
                values: new object[] { "InsuranceMaxLinksPerPolicy", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "50" });
        }

        /// <summary>
        /// <b>Not supported, and it fails loudly rather than pretending.</b> This is not merely lossy:
        /// zero insurers is a valid state after this migration while <c>InsurerId</c> was
        /// <c>NOT NULL</c>, so a zero-insurer policy has no row to backfill from and no legal value to
        /// invent — and dropping the four tables destroys every multi-link policy outright. Restore
        /// from backup instead.
        /// </summary>
        protected override void Down(MigrationBuilder migrationBuilder) =>
            throw new NotSupportedException(
                "AddInsurancePolicyLinkCollections cannot be reverted. A policy may now legitimately "
                + "have zero insurers, which the NOT NULL InsurerId column cannot represent, and the "
                + "link tables hold members no scalar column can hold. Restore from backup.");
    }
}
