using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Odyssey.Context.Migrations
{
    /// <inheritdoc />
    public partial class AddTransactionContactTimeStampIndex : Migration
    {
        /// <summary>
        /// Swaps IX_Transactions_ContactId for (ContactId, TimeStamp), the contingency index issue #226
        /// §10 names for the contract smart-tag match, which filters on the merchant and the contract's
        /// term together. Measured on MariaDB at 100 000 transactions it roughly halves the scoped count
        /// and the searched page. The old index is a strict PREFIX of the new one, so it is dropped
        /// rather than kept alongside.
        ///
        /// <para>
        /// <b>CREATE BEFORE DROP, and the order is load-bearing</b> — the same reason as
        /// AddTransactionAmountCoveringIndex: Transactions.ContactId's foreign key needs an index leading
        /// with ContactId for as long as the constraint exists, and scaffolding emits the drop first,
        /// which fails on MariaDB with "needed in a foreign key constraint".
        /// </para>
        /// </summary>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Transactions_ContactId_TimeStamp",
                table: "Transactions",
                columns: new[] { "ContactId", "TimeStamp" });

            migrationBuilder.DropIndex(
                name: "IX_Transactions_ContactId",
                table: "Transactions");
        }

        /// <summary>Reverses <see cref="Up"/>, in the same create-before-drop order and for the same reason.</summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Transactions_ContactId",
                table: "Transactions",
                column: "ContactId");

            migrationBuilder.DropIndex(
                name: "IX_Transactions_ContactId_TimeStamp",
                table: "Transactions");
        }
    }
}
