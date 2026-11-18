using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Odyssey.Context.Migrations
{
    /// <summary>
    /// The exchange-rate sync's run log leaves (issue #409 §11, migration 2 of 2), once <c>AddJobRuns</c> has copied it
    /// onto <c>JobRuns</c>. A separate migration because MariaDB DDL is not atomic (issue #468): the drop happens only
    /// after the copy has committed and been verified.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The parity check (S-N4) runs before the drop</b> and aborts, dropping nothing, unless every legacy id is in
    /// <c>JobRuns</c> as an exchange-rate sync run with an equal status, trigger, timestamps and the four row counters.
    /// </para>
    /// <para>
    /// <b><c>Down()</c> restores the schema only — an empty table.</b> The rows live on in <c>JobRuns</c>; getting them
    /// back into the old table is a restore from backup, nothing else.
    /// </para>
    /// </remarks>
    public partial class DropExchangeRateSyncRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                BEGIN NOT ATOMIC
                    DECLARE bad CHAR(36) DEFAULT NULL;
                    DECLARE failure TEXT DEFAULT NULL;
                    SELECT s.`ExchangeRateSyncRunId` INTO bad
                    FROM `ExchangeRateSyncRuns` s
                    WHERE NOT EXISTS (
                        SELECT 1 FROM `JobRuns` j
                        WHERE j.`JobRunId` = s.`ExchangeRateSyncRunId`
                          AND j.`JobType` = 1
                          AND j.`Status` = s.`Status`
                          AND j.`Trigger` = s.`Trigger`
                          AND j.`StartedAtUtc` = s.`StartedAtUtc`
                          AND j.`FinishedAtUtc` <=> s.`FinishedAtUtc`
                          AND j.`ClaimedAtUtc` <=> s.`ClaimedAtUtc`
                          AND j.`ErrorKind` <=> s.`ErrorKind`
                          AND j.`RowsInserted` <=> s.`RowsInserted`
                          AND j.`RowsSkipped` <=> s.`RowsSkipped`
                          AND j.`RowsRejected` <=> s.`RowsRejected`
                          AND j.`RowsDeleted` <=> s.`RowsDeleted`)
                    ORDER BY s.`StartedAtUtc`, s.`ExchangeRateSyncRunId`
                    LIMIT 1;
                    IF bad IS NOT NULL THEN
                        SET failure = CONCAT('DropExchangeRateSyncRuns: exchange-rate sync run ', bad,
                            ' is missing from JobRuns or differs from its copy; ExchangeRateSyncRuns is kept. Re-run AddJobRuns''s copy or repair the row.');
                        SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = failure;
                    END IF;
                END
                """);

            migrationBuilder.DropTable(
                name: "ExchangeRateSyncRuns");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ExchangeRateSyncRuns",
                columns: table => new
                {
                    ExchangeRateSyncRunId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    ClaimedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    CurrencyCodes = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ErrorKind = table.Column<int>(type: "int", nullable: true),
                    FinishedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    Provider = table.Column<int>(type: "int", nullable: false),
                    RequestedByUserId = table.Column<string>(type: "varchar(255)", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    RowsDeleted = table.Column<int>(type: "int", nullable: false),
                    RowsInserted = table.Column<int>(type: "int", nullable: false),
                    RowsRejected = table.Column<int>(type: "int", nullable: false),
                    RowsSkipped = table.Column<int>(type: "int", nullable: false),
                    RunningGuard = table.Column<int>(type: "int", nullable: true, computedColumnSql: "IF(`Status` = 0, 1, NULL)", stored: false),
                    SpanFrom = table.Column<DateOnly>(type: "date", nullable: true),
                    SpanTo = table.Column<DateOnly>(type: "date", nullable: true),
                    StartedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    Trigger = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExchangeRateSyncRuns", x => x.ExchangeRateSyncRunId);
                    table.CheckConstraint("CK_ExchangeRateSyncRuns_ErrorKind", "`ErrorKind` IS NULL OR `ErrorKind` BETWEEN 1 AND 9");
                    table.CheckConstraint("CK_ExchangeRateSyncRuns_Provider", "`Provider` IN (1)");
                    table.CheckConstraint("CK_ExchangeRateSyncRuns_Status", "`Status` IN (0, 1, 2, 3, 4)");
                    table.CheckConstraint("CK_ExchangeRateSyncRuns_Trigger", "`Trigger` IN (0, 1, 2)");
                    table.ForeignKey(
                        name: "FK_ExchangeRateSyncRuns_AspNetUsers_RequestedByUserId",
                        column: x => x.RequestedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_ExchangeRateSyncRuns_RequestedByUserId",
                table: "ExchangeRateSyncRuns",
                column: "RequestedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ExchangeRateSyncRuns_RunningGuard",
                table: "ExchangeRateSyncRuns",
                column: "RunningGuard",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExchangeRateSyncRuns_StartedAtUtc",
                table: "ExchangeRateSyncRuns",
                column: "StartedAtUtc");
        }
    }
}
