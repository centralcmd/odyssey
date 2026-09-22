using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Odyssey.Context.Migrations
{
    /// <summary>
    /// Drops the standalone insurance-policy feature: the <c>InsurancePolicies</c> header table, its
    /// four party link tables, the <c>PolicyRenewals</c>/<c>PolicyRenewalFiles</c> pair, the issue-#26
    /// relocation ledger, and the five <c>SystemSettings</c> rows that bounded the page
    /// (<c>InsuranceExpiringSoonWindowDays</c>, <c>InsuranceMaxSummaryPolicies</c>,
    /// <c>InsuranceMaxRenewalsPerPolicy</c>, <c>InsuranceMaxFilesPerParent</c>,
    /// <c>InsuranceMaxLinksPerPolicy</c>). Contracts cover the same ground through
    /// <c>ContractType.Insurance</c> with party roles, terms and <c>ContractEventType.Renewed</c>.
    ///
    /// <para>
    /// <strong>No rows are migrated into Contracts, deliberately.</strong> Every <c>odyssey</c>
    /// database in existence is a local dev or test database rebuilt from <c>DemoDataSeeder</c> — the
    /// same precondition that licenses a migration squash — so there is no policy anyone needs to
    /// keep. Nothing here reads a table before dropping it.
    /// </para>
    ///
    /// <para>
    /// <strong>Attached documents SURVIVE.</strong> <c>PolicyRenewalFile</c> was a join row pointing
    /// <em>at</em> <c>FileMetadata</c>, so dropping it leaves the underlying
    /// <c>FileMetadata</c>/<c>FileBlob</c> rows intact and the documents reachable at <c>/files</c>.
    /// That is the standing "detach, never delete" posture for the file store, not an oversight.
    /// </para>
    ///
    /// <para>
    /// <strong><c>_InsurancePolicyFileRelocation</c> is dropped FIRST, by hand.</strong> It is not an
    /// EF entity — it is the issue-#26 operational ledger, created by
    /// <c>AddInsurancePolicyFileRelocationLedger</c> and absent from every model snapshot — so the
    /// scaffolder emits no drop for it. It holds a <c>CASCADE</c> foreign key to
    /// <c>InsurancePolicies</c>, and InnoDB refuses <c>DROP TABLE</c> on a table another table's key
    /// still references, so omitting it fails this migration at the <c>InsurancePolicies</c> drop.
    /// </para>
    ///
    /// <para>
    /// <strong>What <c>Down</c> restores.</strong> The schema and the shipped settings defaults, not
    /// the data — the same bargain <c>DropSubscriptionsAndSummarySettings</c> struck. It buys the
    /// ability to run the previous binary against empty tables and nothing more.
    /// </para>
    ///
    /// <para>
    /// <strong>The four <c>insurance.*</c> permission claims have no migration half.</strong>
    /// <c>RoleClaimSeeder</c> reconciles <c>AspNetRoleClaims</c> from <c>RolePermissions</c> on every
    /// startup and applies removals as well as additions, so dropping the constants revokes the rows
    /// on the next boot. Do not add a <c>HasData</c> claim seed to compensate.
    /// </para>
    /// </summary>
    public partial class DropInsurancePoliciesAndSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The row deletes run BEFORE the drops: DROP TABLE commits implicitly on MariaDB, so
            // anything ordered after it is stranded on the far side of a commit boundary this
            // migration did not ask for.
            migrationBuilder.DeleteData(
                table: "SystemSettings",
                keyColumn: "Key",
                keyValue: "InsuranceExpiringSoonWindowDays");

            migrationBuilder.DeleteData(
                table: "SystemSettings",
                keyColumn: "Key",
                keyValue: "InsuranceMaxFilesPerParent");

            migrationBuilder.DeleteData(
                table: "SystemSettings",
                keyColumn: "Key",
                keyValue: "InsuranceMaxLinksPerPolicy");

            migrationBuilder.DeleteData(
                table: "SystemSettings",
                keyColumn: "Key",
                keyValue: "InsuranceMaxRenewalsPerPolicy");

            migrationBuilder.DeleteData(
                table: "SystemSettings",
                keyColumn: "Key",
                keyValue: "InsuranceMaxSummaryPolicies");

            // IF EXISTS throughout, not migrationBuilder.DropTable: an in-process replay of this
            // migration (the execution strategy retries a transient connection failure) must be a
            // no-op rather than ERROR 1051 on a table the first attempt already removed. Same
            // reasoning as DropInsurancePolicyFiles and DropSubscriptionsAndSummarySettings.
            //
            // The ledger goes first — it holds a CASCADE key to InsurancePolicies (see the class
            // summary), and the rest are ordered child-before-parent for the same reason.
            migrationBuilder.Sql("DROP TABLE IF EXISTS `_InsurancePolicyFileRelocation`;");
            migrationBuilder.Sql("DROP TABLE IF EXISTS `PolicyRenewalFiles`;");
            migrationBuilder.Sql("DROP TABLE IF EXISTS `PolicyRenewals`;");
            migrationBuilder.Sql("DROP TABLE IF EXISTS `InsurancePolicyBeneficiaries`;");
            migrationBuilder.Sql("DROP TABLE IF EXISTS `InsurancePolicyInsuredAccounts`;");
            migrationBuilder.Sql("DROP TABLE IF EXISTS `InsurancePolicyInsuredContacts`;");
            migrationBuilder.Sql("DROP TABLE IF EXISTS `InsurancePolicyInsurers`;");
            migrationBuilder.Sql("DROP TABLE IF EXISTS `InsurancePolicies`;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "InsurancePolicies",
                columns: table => new
                {
                    InsurancePolicyId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    Archived = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    Name = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Notes = table.Column<string>(type: "varchar(1024)", maxLength: 1024, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    PolicyNumber = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Type = table.Column<int>(type: "int", nullable: false, defaultValue: 11)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InsurancePolicies", x => x.InsurancePolicyId);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "InsurancePolicyBeneficiaries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    InsurancePolicyId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    ContactId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    CreatedByUserId = table.Column<string>(type: "varchar(255)", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    FromDate = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    ToDate = table.Column<DateTime>(type: "datetime(6)", nullable: true)
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
                    AccountId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    FromDate = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    ToDate = table.Column<DateTime>(type: "datetime(6)", nullable: true)
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
                    ContactId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    FromDate = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    ToDate = table.Column<DateTime>(type: "datetime(6)", nullable: true)
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
                    ContactId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    FromDate = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    ToDate = table.Column<DateTime>(type: "datetime(6)", nullable: true)
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

            migrationBuilder.CreateTable(
                name: "PolicyRenewals",
                columns: table => new
                {
                    PolicyRenewalId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    InsurancePolicyId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    CoverageAmount = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: false),
                    CoverageCurrencyCode = table.Column<string>(type: "varchar(3)", maxLength: 3, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    FromDate = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    Notes = table.Column<string>(type: "varchar(512)", maxLength: 512, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Premium = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: false),
                    PremiumCurrencyCode = table.Column<string>(type: "varchar(3)", maxLength: 3, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ToDate = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PolicyRenewals", x => x.PolicyRenewalId);
                    table.ForeignKey(
                        name: "FK_PolicyRenewals_InsurancePolicies_InsurancePolicyId",
                        column: x => x.InsurancePolicyId,
                        principalTable: "InsurancePolicies",
                        principalColumn: "InsurancePolicyId",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "PolicyRenewalFiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    FileMetadataId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    PolicyRenewalId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    AttachedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    AttachedByUserId = table.Column<string>(type: "varchar(255)", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    EffectiveDate = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    FileType = table.Column<int>(type: "int", nullable: false, defaultValue: 5)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PolicyRenewalFiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PolicyRenewalFiles_AspNetUsers_AttachedByUserId",
                        column: x => x.AttachedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_PolicyRenewalFiles_FileMetadata_FileMetadataId",
                        column: x => x.FileMetadataId,
                        principalTable: "FileMetadata",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PolicyRenewalFiles_PolicyRenewals_PolicyRenewalId",
                        column: x => x.PolicyRenewalId,
                        principalTable: "PolicyRenewals",
                        principalColumn: "PolicyRenewalId",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.InsertData(
                table: "SystemSettings",
                columns: new[] { "Key", "UpdatedAt", "UpdatedBy", "Value" },
                values: new object[,]
                {
                    { "InsuranceExpiringSoonWindowDays", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "30" },
                    { "InsuranceMaxFilesPerParent", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "50" },
                    { "InsuranceMaxLinksPerPolicy", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "50" },
                    { "InsuranceMaxRenewalsPerPolicy", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "100" },
                    { "InsuranceMaxSummaryPolicies", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "1000" }
                });

            migrationBuilder.CreateIndex(
                name: "IX_InsurancePolicies_Archived",
                table: "InsurancePolicies",
                column: "Archived");

            migrationBuilder.CreateIndex(
                name: "IX_InsurancePolicies_Type_Archived",
                table: "InsurancePolicies",
                columns: new[] { "Type", "Archived" });

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

            migrationBuilder.CreateIndex(
                name: "IX_PolicyRenewalFiles_AttachedAtUtc",
                table: "PolicyRenewalFiles",
                column: "AttachedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_PolicyRenewalFiles_AttachedByUserId",
                table: "PolicyRenewalFiles",
                column: "AttachedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_PolicyRenewalFiles_FileMetadataId",
                table: "PolicyRenewalFiles",
                column: "FileMetadataId");

            migrationBuilder.CreateIndex(
                name: "IX_PolicyRenewalFiles_PolicyRenewalId_FileMetadataId",
                table: "PolicyRenewalFiles",
                columns: new[] { "PolicyRenewalId", "FileMetadataId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PolicyRenewals_InsurancePolicyId_ToDate",
                table: "PolicyRenewals",
                columns: new[] { "InsurancePolicyId", "ToDate" });

            // The issue-#26 relocation ledger, recreated by hand because it is not an EF entity and
            // the scaffolder therefore knows nothing about it. Shape copied verbatim from
            // AddInsurancePolicyFileRelocationLedger, including its three keys: without them
            // AttachedByUserId would be the one attribution column in the schema that is neither a
            // SET NULL foreign key nor pseudonymized.
            migrationBuilder.CreateTable(
                name: "_InsurancePolicyFileRelocation",
                columns: table => new
                {
                    SourceId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    InsurancePolicyId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    FileMetadataId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    FileType = table.Column<int>(type: "int", nullable: false),
                    EffectiveDate = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    AttachedByUserId = table.Column<string>(type: "varchar(255)", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    AttachedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    DestinationPolicyRenewalId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    DestinationPolicyRenewalFileId = table.Column<Guid>(type: "char(36)", nullable: true, collation: "ascii_general_ci"),
                    Outcome = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    PlaceholderPeriodCreated = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    MigratedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK__InsurancePolicyFileRelocation", x => x.SourceId);
                    table.ForeignKey(
                        name: "FK__InsurancePolicyFileRelocation_AspNetUsers_AttachedByUserId",
                        column: x => x.AttachedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK__InsurancePolicyFileRelocation_FileMetadata_FileMetadataId",
                        column: x => x.FileMetadataId,
                        principalTable: "FileMetadata",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK__InsurancePolicyFileRelocation_InsurancePolicies_PolicyId",
                        column: x => x.InsurancePolicyId,
                        principalTable: "InsurancePolicies",
                        principalColumn: "InsurancePolicyId",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX__InsurancePolicyFileRelocation_AttachedByUserId",
                table: "_InsurancePolicyFileRelocation",
                column: "AttachedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX__InsurancePolicyFileRelocation_FileMetadataId",
                table: "_InsurancePolicyFileRelocation",
                column: "FileMetadataId");

            migrationBuilder.CreateIndex(
                name: "IX__InsurancePolicyFileRelocation_InsurancePolicyId",
                table: "_InsurancePolicyFileRelocation",
                column: "InsurancePolicyId");
        }
    }
}
