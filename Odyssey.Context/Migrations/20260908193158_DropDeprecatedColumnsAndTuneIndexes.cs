using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Odyssey.Context.Migrations
{
    /// <inheritdoc />
    public partial class DropDeprecatedColumnsAndTuneIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Transactions_AccountId",
                table: "Transactions");

            migrationBuilder.DropIndex(
                name: "IX_Photos_Archived",
                table: "Photos");

            migrationBuilder.DropColumn(
                name: "SourceLineNumber",
                table: "FileAnalysisCandidateTransactions");

            migrationBuilder.DropColumn(
                name: "SourcePageNumber",
                table: "FileAnalysisCandidateTransactions");

            migrationBuilder.DropColumn(
                name: "LegacyType",
                table: "Contacts");

            migrationBuilder.DropColumn(
                name: "OrganizationNumber",
                table: "Contacts");

            migrationBuilder.AlterColumn<string>(
                name: "ExternalUid",
                table: "Contacts",
                type: "varchar(255)",
                maxLength: 255,
                nullable: false,
                collation: "utf8mb4_bin",
                oldClrType: typeof(string),
                oldType: "varchar(255)",
                oldMaxLength: 255)
                .Annotation("MySql:CharSet", "utf8mb4")
                .OldAnnotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_Transactions_AccountId_TimeStamp",
                table: "Transactions",
                columns: new[] { "AccountId", "TimeStamp" });

            migrationBuilder.CreateIndex(
                name: "IX_Transactions_Status",
                table: "Transactions",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_Photos_Archived_TakenAt",
                table: "Photos",
                columns: new[] { "Archived", "TakenAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Transactions_AccountId_TimeStamp",
                table: "Transactions");

            migrationBuilder.DropIndex(
                name: "IX_Transactions_Status",
                table: "Transactions");

            migrationBuilder.DropIndex(
                name: "IX_Photos_Archived_TakenAt",
                table: "Photos");

            migrationBuilder.AddColumn<int>(
                name: "SourceLineNumber",
                table: "FileAnalysisCandidateTransactions",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SourcePageNumber",
                table: "FileAnalysisCandidateTransactions",
                type: "int",
                nullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "ExternalUid",
                table: "Contacts",
                type: "varchar(255)",
                maxLength: 255,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "varchar(255)",
                oldMaxLength: 255,
                oldCollation: "utf8mb4_bin")
                .Annotation("MySql:CharSet", "utf8mb4")
                .OldAnnotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<int>(
                name: "LegacyType",
                table: "Contacts",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OrganizationNumber",
                table: "Contacts",
                type: "varchar(64)",
                maxLength: 64,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_Transactions_AccountId",
                table: "Transactions",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_Photos_Archived",
                table: "Photos",
                column: "Archived");
        }
    }
}
