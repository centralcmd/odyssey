using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Odyssey.Context.Migrations
{
    /// <inheritdoc />
    public partial class AddFileAnalysisMatchContextAndPromptAdditions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "SystemSettings",
                keyColumn: "Key",
                keyValue: "FileAnalysisMatchMaxVocabulary");

            migrationBuilder.AddColumn<int>(
                name: "AliasCount",
                table: "FileAnalysisJobs",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ContactCount",
                table: "FileAnalysisJobs",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DisclosureVersionInForce",
                table: "FileAnalysisJobs",
                type: "varchar(16)",
                maxLength: 16,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "ExtractionPromptAdditionInForce",
                table: "FileAnalysisJobs",
                type: "text",
                maxLength: 4000,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "MatchDisclosureVersionInForce",
                table: "FileAnalysisJobs",
                type: "varchar(16)",
                maxLength: 16,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<int>(
                name: "MatchGeneration",
                table: "FileAnalysisJobs",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "MatchPromptAdditionInForce",
                table: "FileAnalysisJobs",
                type: "text",
                maxLength: 4000,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "MatchPromptVersion",
                table: "FileAnalysisJobs",
                type: "varchar(32)",
                maxLength: 32,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<int>(
                name: "MatchVocabularyScope",
                table: "FileAnalysisJobs",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TagCount",
                table: "FileAnalysisJobs",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TagDescriptionCount",
                table: "FileAnalysisJobs",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "VocabularyReduction",
                table: "FileAnalysisJobs",
                type: "int",
                nullable: true);

            migrationBuilder.InsertData(
                table: "SystemSettings",
                columns: new[] { "Key", "UpdatedAt", "UpdatedBy", "Value" },
                values: new object[,]
                {
                    { "FileAnalysisExtractionPromptAddition", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "" },
                    { "FileAnalysisMatchPromptAddition", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "" }
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "SystemSettings",
                keyColumn: "Key",
                keyValue: "FileAnalysisExtractionPromptAddition");

            migrationBuilder.DeleteData(
                table: "SystemSettings",
                keyColumn: "Key",
                keyValue: "FileAnalysisMatchPromptAddition");

            migrationBuilder.DropColumn(
                name: "AliasCount",
                table: "FileAnalysisJobs");

            migrationBuilder.DropColumn(
                name: "ContactCount",
                table: "FileAnalysisJobs");

            migrationBuilder.DropColumn(
                name: "DisclosureVersionInForce",
                table: "FileAnalysisJobs");

            migrationBuilder.DropColumn(
                name: "ExtractionPromptAdditionInForce",
                table: "FileAnalysisJobs");

            migrationBuilder.DropColumn(
                name: "MatchDisclosureVersionInForce",
                table: "FileAnalysisJobs");

            migrationBuilder.DropColumn(
                name: "MatchGeneration",
                table: "FileAnalysisJobs");

            migrationBuilder.DropColumn(
                name: "MatchPromptAdditionInForce",
                table: "FileAnalysisJobs");

            migrationBuilder.DropColumn(
                name: "MatchPromptVersion",
                table: "FileAnalysisJobs");

            migrationBuilder.DropColumn(
                name: "MatchVocabularyScope",
                table: "FileAnalysisJobs");

            migrationBuilder.DropColumn(
                name: "TagCount",
                table: "FileAnalysisJobs");

            migrationBuilder.DropColumn(
                name: "TagDescriptionCount",
                table: "FileAnalysisJobs");

            migrationBuilder.DropColumn(
                name: "VocabularyReduction",
                table: "FileAnalysisJobs");

            migrationBuilder.InsertData(
                table: "SystemSettings",
                columns: new[] { "Key", "UpdatedAt", "UpdatedBy", "Value" },
                values: new object[] { "FileAnalysisMatchMaxVocabulary", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "500" });
        }
    }
}
