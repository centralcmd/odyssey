using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Odyssey.Context.Migrations
{
    /// <summary>
    /// Issue #308: the contract's smart-tag scope — two nullable calendar days the smart-tag match
    /// reads instead of the term. A DATA migration on a production database: the backfill copies every
    /// contract's current term window into the scope, so matching is unchanged on upgrade. <c>Down()</c>
    /// drops the columns and with them any scope a user widened after upgrade; nothing that existed
    /// before is lost.
    /// </summary>
    public partial class AddContractSmartTagScope : Migration
    {
        /// <summary>
        /// The backfill — the SQL form of <c>SmartTagScopeRules.FromTerm</c> (§8 rule 4). A one-off
        /// (<c>CompletionDate</c> set) backfills open/open even when it still holds stale term dates,
        /// which is what the old window computation did. <c>DATE()</c> truncates explicitly, matching
        /// the calendar day the old window read. An inverted legacy term keeps its start and opens its
        /// end, since an inverted scope would fail the CHECK added next. Every value derives from the
        /// term columns alone, so re-running it is idempotent; public so the integration test runs the
        /// same text.
        /// </summary>
        public const string BackfillSql = """
            UPDATE `Contracts` SET
              `SmartTagScopeStart` = CASE WHEN `CompletionDate` IS NULL THEN DATE(`StartDate`) END,
              `SmartTagScopeEnd`   = CASE WHEN `CompletionDate` IS NULL
                                           AND (`StartDate` IS NULL OR `EndDate` IS NULL
                                                OR DATE(`StartDate`) <= DATE(`EndDate`))
                                          THEN DATE(`EndDate`) END;
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "SmartTagScopeEnd",
                table: "Contracts",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SmartTagScopeStart",
                table: "Contracts",
                type: "datetime(6)",
                nullable: true);

            // Hand-inserted between the columns and the CHECK (issue #308 §11).
            migrationBuilder.Sql(BackfillSql);

            migrationBuilder.AddCheckConstraint(
                name: "CK_Contracts_SmartTagScopeOrder",
                table: "Contracts",
                sql: "`SmartTagScopeStart` IS NULL OR `SmartTagScopeEnd` IS NULL OR `SmartTagScopeStart` <= `SmartTagScopeEnd`");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Contracts_SmartTagScopeOrder",
                table: "Contracts");

            migrationBuilder.DropColumn(
                name: "SmartTagScopeEnd",
                table: "Contracts");

            migrationBuilder.DropColumn(
                name: "SmartTagScopeStart",
                table: "Contracts");
        }
    }
}
