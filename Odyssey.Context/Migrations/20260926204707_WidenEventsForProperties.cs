using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Odyssey.Context.Migrations
{
    /// <inheritdoc />
    public partial class WidenEventsForProperties : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ContractEvents_AspNetUsers_CreatedByUserId",
                table: "ContractEvents");

            migrationBuilder.DropForeignKey(
                name: "FK_ContractEvents_Contracts_ContractId",
                table: "ContractEvents");

            migrationBuilder.DropPrimaryKey(
                name: "PK_ContractEvents",
                table: "ContractEvents");

            migrationBuilder.RenameTable(
                name: "ContractEvents",
                newName: "Events");

            migrationBuilder.RenameColumn(
                name: "ContractEventId",
                table: "Events",
                newName: "EventId");

            migrationBuilder.RenameIndex(
                name: "IX_ContractEvents_CreatedByUserId",
                table: "Events",
                newName: "IX_Events_CreatedByUserId");

            migrationBuilder.RenameIndex(
                name: "IX_ContractEvents_ContractId_OccurredAt",
                table: "Events",
                newName: "IX_Events_ContractId_OccurredAt");

            migrationBuilder.AlterColumn<int>(
                name: "Type",
                table: "Events",
                type: "int",
                nullable: true,
                defaultValue: 8,
                oldClrType: typeof(int),
                oldType: "int",
                oldDefaultValue: 8);

            migrationBuilder.AlterColumn<Guid>(
                name: "ContractId",
                table: "Events",
                type: "char(36)",
                nullable: true,
                collation: "ascii_general_ci",
                oldClrType: typeof(Guid),
                oldType: "char(36)")
                .OldAnnotation("Relational:Collation", "ascii_general_ci");

            migrationBuilder.AddColumn<int>(
                name: "OwnerKind",
                table: "Events",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "PropertyId",
                table: "Events",
                type: "char(36)",
                nullable: true,
                collation: "ascii_general_ci");

            migrationBuilder.AddPrimaryKey(
                name: "PK_Events",
                table: "Events",
                column: "EventId");

            migrationBuilder.CreateIndex(
                name: "IX_Events_PropertyId_OccurredAt",
                table: "Events",
                columns: new[] { "PropertyId", "OccurredAt" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_Events_ExactlyOneOwner",
                table: "Events",
                sql: "(`OwnerKind` = 0 AND `ContractId` IS NOT NULL AND `PropertyId` IS NULL) OR (`OwnerKind` = 1 AND `PropertyId` IS NOT NULL AND `ContractId` IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Events_TypeMatchesOwner",
                table: "Events",
                sql: "`Type` IS NOT NULL AND ((`OwnerKind` = 0 AND `Type` BETWEEN 0 AND 99) OR (`OwnerKind` = 1 AND `Type` BETWEEN 100 AND 199))");

            migrationBuilder.AddForeignKey(
                name: "FK_Events_AspNetUsers_CreatedByUserId",
                table: "Events",
                column: "CreatedByUserId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_Events_Contracts_ContractId",
                table: "Events",
                column: "ContractId",
                principalTable: "Contracts",
                principalColumn: "ContractId",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Events_Properties_PropertyId",
                table: "Events",
                column: "PropertyId",
                principalTable: "Properties",
                principalColumn: "PropertyId",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Property events have no home in the pre-#209 ContractEvents table, whose ContractId is
            // NOT NULL; reverting drops them, as reverting a migration that added a table would.
            migrationBuilder.Sql("DELETE FROM `Events` WHERE `OwnerKind` = 1;");

            migrationBuilder.DropForeignKey(
                name: "FK_Events_AspNetUsers_CreatedByUserId",
                table: "Events");

            migrationBuilder.DropForeignKey(
                name: "FK_Events_Contracts_ContractId",
                table: "Events");

            migrationBuilder.DropForeignKey(
                name: "FK_Events_Properties_PropertyId",
                table: "Events");

            migrationBuilder.DropPrimaryKey(
                name: "PK_Events",
                table: "Events");

            migrationBuilder.DropIndex(
                name: "IX_Events_PropertyId_OccurredAt",
                table: "Events");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Events_ExactlyOneOwner",
                table: "Events");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Events_TypeMatchesOwner",
                table: "Events");

            migrationBuilder.DropColumn(
                name: "OwnerKind",
                table: "Events");

            migrationBuilder.DropColumn(
                name: "PropertyId",
                table: "Events");

            migrationBuilder.RenameTable(
                name: "Events",
                newName: "ContractEvents");

            migrationBuilder.RenameColumn(
                name: "EventId",
                table: "ContractEvents",
                newName: "ContractEventId");

            migrationBuilder.RenameIndex(
                name: "IX_Events_CreatedByUserId",
                table: "ContractEvents",
                newName: "IX_ContractEvents_CreatedByUserId");

            migrationBuilder.RenameIndex(
                name: "IX_Events_ContractId_OccurredAt",
                table: "ContractEvents",
                newName: "IX_ContractEvents_ContractId_OccurredAt");

            migrationBuilder.AlterColumn<int>(
                name: "Type",
                table: "ContractEvents",
                type: "int",
                nullable: false,
                defaultValue: 8,
                oldClrType: typeof(int),
                oldType: "int",
                oldNullable: true,
                oldDefaultValue: 8);

            migrationBuilder.AlterColumn<Guid>(
                name: "ContractId",
                table: "ContractEvents",
                type: "char(36)",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                collation: "ascii_general_ci",
                oldClrType: typeof(Guid),
                oldType: "char(36)",
                oldNullable: true)
                .OldAnnotation("Relational:Collation", "ascii_general_ci");

            migrationBuilder.AddPrimaryKey(
                name: "PK_ContractEvents",
                table: "ContractEvents",
                column: "ContractEventId");

            migrationBuilder.AddForeignKey(
                name: "FK_ContractEvents_AspNetUsers_CreatedByUserId",
                table: "ContractEvents",
                column: "CreatedByUserId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_ContractEvents_Contracts_ContractId",
                table: "ContractEvents",
                column: "ContractId",
                principalTable: "Contracts",
                principalColumn: "ContractId",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
