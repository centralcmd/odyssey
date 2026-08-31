using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Odyssey.Context.Migrations
{
    /// <summary>
    /// Drops <c>InsurancePolicyFiles</c>, after <c>MoveInsurancePolicyFilesToRenewals</c> has asserted
    /// the relocation complete (issue #26).
    ///
    /// <para>
    /// <strong>Why the drop is its own migration.</strong> A <c>DROP TABLE</c> implicitly commits on
    /// MariaDB, so keeping it inside the relocation would have committed that migration's writes before
    /// its assertion could reject them. It would also have made the relocation unreplayable: a re-entry
    /// after the drop dies at <c>ERROR 1146</c> on the first statement naming the source table. An
    /// earlier draft guarded every such statement with the <c>PREPARE</c>/<c>EXECUTE</c> conditional
    /// idiom, which cannot work here — that idiom is built on user variables, and MySqlConnector parses
    /// <c>@name</c> as a parameter placeholder unless <c>AllowUserVariables=True</c>, which none of this
    /// repository's connection strings set. Splitting the drop out deletes the mechanism instead of
    /// hardening it: the relocation's history row commits before this migration runs, so it can never
    /// replay against a dropped table, and <c>DROP TABLE IF EXISTS</c> is idempotent unguarded.
    /// </para>
    ///
    /// <para>
    /// Reversibility does not depend on this table surviving: <c>Down</c> recreates it and the ledger
    /// repopulates it from the full source payload it recorded.
    /// </para>
    /// </summary>
    public partial class DropInsurancePolicyFiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // IF EXISTS, not migrationBuilder.DropTable: an in-process replay of this migration (the
            // execution strategy retries a transient connection failure) must be a no-op rather than
            // ERROR 1051 on a table the first attempt already removed.
            migrationBuilder.Sql("DROP TABLE IF EXISTS `InsurancePolicyFiles`;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "InsurancePolicyFiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    FileMetadataId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    InsurancePolicyId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    AttachedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    AttachedByUserId = table.Column<string>(type: "varchar(255)", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    EffectiveDate = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    FileType = table.Column<int>(type: "int", nullable: false, defaultValue: 5)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InsurancePolicyFiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InsurancePolicyFiles_AspNetUsers_AttachedByUserId",
                        column: x => x.AttachedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_InsurancePolicyFiles_FileMetadata_FileMetadataId",
                        column: x => x.FileMetadataId,
                        principalTable: "FileMetadata",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_InsurancePolicyFiles_InsurancePolicies_InsurancePolicyId",
                        column: x => x.InsurancePolicyId,
                        principalTable: "InsurancePolicies",
                        principalColumn: "InsurancePolicyId",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_InsurancePolicyFiles_AttachedAtUtc",
                table: "InsurancePolicyFiles",
                column: "AttachedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_InsurancePolicyFiles_AttachedByUserId",
                table: "InsurancePolicyFiles",
                column: "AttachedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_InsurancePolicyFiles_FileMetadataId",
                table: "InsurancePolicyFiles",
                column: "FileMetadataId");

            migrationBuilder.CreateIndex(
                name: "IX_InsurancePolicyFiles_InsurancePolicyId_FileMetadataId",
                table: "InsurancePolicyFiles",
                columns: new[] { "InsurancePolicyId", "FileMetadataId" },
                unique: true);
        }
    }
}
