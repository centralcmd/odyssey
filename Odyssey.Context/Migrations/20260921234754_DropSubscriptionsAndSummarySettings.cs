using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Odyssey.Context.Migrations
{
    /// <summary>
    /// Drops the standalone subscriptions feature: the <c>Subscriptions</c> table and the three
    /// <c>SystemSettings</c> rows that bounded its page-header roll-up
    /// (<c>SubscriptionRenewalWindowDays</c>, <c>SubscriptionMaxSummaryRenewals</c>,
    /// <c>SubscriptionMaxSummarySubscriptions</c>). Contracts cover the same ground through
    /// <c>ContractType.Subscription</c>.
    ///
    /// <para>
    /// <strong>No rows are migrated into Contracts, deliberately.</strong> Every <c>odyssey</c>
    /// database in existence is a local dev or test database rebuilt from <c>DemoDataSeeder</c> —
    /// the same precondition that licenses a migration squash — so there is no subscription anyone
    /// needs to keep. Nothing here reads the table before dropping it.
    /// </para>
    ///
    /// <para>
    /// <strong>What <c>Down</c> restores.</strong> The schema and the shipped settings defaults, not
    /// the data. <c>DropInsurancePolicyFiles</c> could honestly claim reversibility because its
    /// relocation ledger repopulated the table it dropped; there is no ledger here and none is
    /// wanted, so <c>Down</c> buys the ability to run the previous binary against an empty table.
    /// </para>
    ///
    /// <para>
    /// <strong>The four <c>subscriptions.*</c> permission claims have no migration half.</strong>
    /// <c>RoleClaimSeeder</c> reconciles <c>AspNetRoleClaims</c> from <c>RolePermissions</c> on every
    /// startup and applies removals as well as additions, so dropping the constants revokes the rows
    /// on the next boot. Do not add a <c>HasData</c> claim seed to compensate.
    /// </para>
    /// </summary>
    public partial class DropSubscriptionsAndSummarySettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The row deletes run BEFORE the drop: DROP TABLE commits implicitly on MariaDB, so
            // anything ordered after it is stranded on the far side of a commit boundary this
            // migration did not ask for.
            migrationBuilder.DeleteData(
                table: "SystemSettings",
                keyColumn: "Key",
                keyValue: "SubscriptionMaxSummaryRenewals");

            migrationBuilder.DeleteData(
                table: "SystemSettings",
                keyColumn: "Key",
                keyValue: "SubscriptionMaxSummarySubscriptions");

            migrationBuilder.DeleteData(
                table: "SystemSettings",
                keyColumn: "Key",
                keyValue: "SubscriptionRenewalWindowDays");

            // IF EXISTS, not migrationBuilder.DropTable: an in-process replay of this migration (the
            // execution strategy retries a transient connection failure) must be a no-op rather than
            // ERROR 1051 on a table the first attempt already removed. Same reasoning as
            // DropInsurancePolicyFiles.
            migrationBuilder.Sql("DROP TABLE IF EXISTS `Subscriptions`;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Subscriptions",
                columns: table => new
                {
                    SubscriptionId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    Amount = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: false),
                    Archived = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    ContactId = table.Column<Guid>(type: "char(36)", nullable: true, collation: "ascii_general_ci"),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    CurrencyCode = table.Column<string>(type: "varchar(3)", maxLength: 3, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    EndDate = table.Column<DateOnly>(type: "date", nullable: true),
                    ExternalId = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    FirstBillingDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Interval = table.Column<int>(type: "int", nullable: false, defaultValue: 2),
                    IntervalCount = table.Column<int>(type: "int", nullable: false, defaultValue: 1),
                    Name = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Notes = table.Column<string>(type: "varchar(1024)", maxLength: 1024, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Paused = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    StartDate = table.Column<DateOnly>(type: "date", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Subscriptions", x => x.SubscriptionId);
                    table.ForeignKey(
                        name: "FK_Subscriptions_Contacts_ContactId",
                        column: x => x.ContactId,
                        principalTable: "Contacts",
                        principalColumn: "ContactId",
                        onDelete: ReferentialAction.SetNull);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.InsertData(
                table: "SystemSettings",
                columns: new[] { "Key", "UpdatedAt", "UpdatedBy", "Value" },
                values: new object[,]
                {
                    { "SubscriptionMaxSummaryRenewals", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "6" },
                    { "SubscriptionMaxSummarySubscriptions", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "1000" },
                    { "SubscriptionRenewalWindowDays", new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "45" }
                });

            migrationBuilder.CreateIndex(
                name: "IX_Subscriptions_Archived",
                table: "Subscriptions",
                column: "Archived");

            migrationBuilder.CreateIndex(
                name: "IX_Subscriptions_ContactId",
                table: "Subscriptions",
                column: "ContactId");

            migrationBuilder.CreateIndex(
                name: "IX_Subscriptions_Interval_Archived",
                table: "Subscriptions",
                columns: new[] { "Interval", "Archived" });

            migrationBuilder.CreateIndex(
                name: "IX_Subscriptions_Paused",
                table: "Subscriptions",
                column: "Paused");
        }
    }
}
