using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Odyssey.Context.Migrations
{
    /// <summary>
    /// The relocation ledger for issue #26 — created on its own, ahead of the relocation that writes
    /// to it.
    ///
    /// <para>
    /// <strong>Why this is a separate migration.</strong> A permanent <c>CREATE TABLE</c> implicitly
    /// commits on MariaDB. Had it sat inside <c>MoveInsurancePolicyFilesToRenewals</c>, the placeholder
    /// periods and relocated rows written after it would have been committed before that migration's
    /// assertion could evaluate — which is precisely the rollback the assertion exists to trigger. The
    /// relocation migration therefore contains no DDL at all, and this one contains nothing else.
    /// </para>
    ///
    /// <para>
    /// <strong>Why the ledger carries the full source payload.</strong> The source table is dropped in
    /// <c>DropInsurancePolicyFiles</c>, so this is the only surviving record of those rows and the sole
    /// basis for <c>Down</c>. Ids alone would let <c>Down</c> find the rows but not reconstruct them.
    /// </para>
    ///
    /// <para>
    /// <strong>Why it carries three foreign keys.</strong> They reproduce exactly what the source row's
    /// own keys did — <c>Cascade</c> to <c>InsurancePolicies</c> and <c>FileMetadata</c>,
    /// <c>SET NULL</c> to <c>AspNetUsers</c>. Without them <c>Down</c> would throw on a parent deleted
    /// between <c>Up</c> and <c>Down</c> (it recreates <c>InsurancePolicyFiles</c> <em>with</em> its
    /// keys and then inserts), and <c>AttachedByUserId</c> would be the first attribution column in the
    /// codebase that is neither a <c>SET NULL</c> foreign key nor pseudonymized. With them, a
    /// cascaded-away ledger row correctly restores nothing and a nulled one correctly restores
    /// <c>NULL</c>.
    /// </para>
    ///
    /// <para>
    /// <c>DestinationPolicyRenewalFileId</c> and <c>DestinationPolicyRenewalId</c> deliberately carry
    /// <em>no</em> key: they must survive a detach or a period deletion, since a ledger row is a record
    /// of what happened rather than a live reference.
    /// </para>
    ///
    /// <para>
    /// The table is not part of the EF model — it is an operational artefact, not domain data — so it
    /// is created here by hand and never appears in <c>OdysseyContextModelSnapshot</c>.
    /// </para>
    /// </summary>
    public partial class AddInsurancePolicyFileRelocationLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
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

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "_InsurancePolicyFileRelocation");
        }
    }
}
