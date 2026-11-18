using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Odyssey.Context.Migrations
{
    /// <inheritdoc />
    public partial class AddExchangeRatePositiveCheck : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddCheckConstraint(
                name: "CK_ExchangeRates_RatePositive",
                table: "ExchangeRates",
                sql: "`Rate` > 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_ExchangeRates_RatePositive",
                table: "ExchangeRates");
        }
    }
}
