using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Odyssey.Context.Migrations
{
    /// <inheritdoc />
    public partial class AddTransactionAmountCoveringIndex : Migration
    {
        /// <summary>
        /// Swaps IX_Transactions_AccountId_TimeStamp for a covering index that also carries Amount, so
        /// the net-worth history's bucketed aggregate (issue #90 §5.3) is answered from the index
        /// rather than the heap. The old index is a strict PREFIX of the new one, so it is dropped
        /// rather than kept alongside — keeping both would cost an extra secondary-index write on
        /// every insert to the app's highest-volume table for no read benefit.
        ///
        /// <para>
        /// <b>CREATE BEFORE DROP, and the order is load-bearing.</b> MariaDB requires an index whose
        /// leading column is the foreign-key column for as long as the constraint exists, and
        /// Transactions.AccountId's FK is served by exactly this index. Scaffolding emits the drop
        /// first, which fails with "needed in a foreign key constraint" — and it fails on MariaDB
        /// only, so nothing on the EF InMemory tiers would have shown it. Creating the covering index
        /// first gives the constraint another index leading with AccountId, after which the old one
        /// can go.
        /// </para>
        /// </summary>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Transactions_AccountId_TimeStamp_Amount",
                table: "Transactions",
                columns: new[] { "AccountId", "TimeStamp", "Amount" });

            migrationBuilder.DropIndex(
                name: "IX_Transactions_AccountId_TimeStamp",
                table: "Transactions");
        }

        /// <summary>Reverses <see cref="Up"/>, in the same create-before-drop order and for the same reason.</summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Transactions_AccountId_TimeStamp",
                table: "Transactions",
                columns: new[] { "AccountId", "TimeStamp" });

            migrationBuilder.DropIndex(
                name: "IX_Transactions_AccountId_TimeStamp_Amount",
                table: "Transactions");
        }
    }
}
