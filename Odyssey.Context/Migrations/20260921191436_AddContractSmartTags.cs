using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Odyssey.Context.Migrations
{
    /// <inheritdoc />
    public partial class AddContractSmartTags : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ContractSmartTags",
                columns: table => new
                {
                    ContractId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    TransactionTagId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    AddedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContractSmartTags", x => new { x.ContractId, x.TransactionTagId });
                    table.ForeignKey(
                        name: "FK_ContractSmartTags_Contracts_ContractId",
                        column: x => x.ContractId,
                        principalTable: "Contracts",
                        principalColumn: "ContractId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ContractSmartTags_TransactionTags_TransactionTagId",
                        column: x => x.TransactionTagId,
                        principalTable: "TransactionTags",
                        principalColumn: "TransactionTagId",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.InsertData(
                table: "SystemSettings",
                columns: new[] { "Key", "UpdatedAt", "UpdatedBy", "Value" },
                values: new object[] { "ContractMaxSmartTagsPerContract", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "20" });

            migrationBuilder.CreateIndex(
                name: "IX_ContractSmartTags_AddedAt",
                table: "ContractSmartTags",
                column: "AddedAt");

            migrationBuilder.CreateIndex(
                name: "IX_ContractSmartTags_TransactionTagId",
                table: "ContractSmartTags",
                column: "TransactionTagId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ContractSmartTags");

            migrationBuilder.DeleteData(
                table: "SystemSettings",
                keyColumn: "Key",
                keyValue: "ContractMaxSmartTagsPerContract");
        }
    }
}
