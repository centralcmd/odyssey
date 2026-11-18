using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Odyssey.Context.Migrations
{
    /// <inheritdoc />
    public partial class AddExchangeRateFetchedLookupIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_ExchangeRates_FromCurrencyCode_ToCurrencyCode_Provider_AsOf",
                table: "ExchangeRates",
                columns: new[] { "FromCurrencyCode", "ToCurrencyCode", "Provider", "AsOf" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ExchangeRates_FromCurrencyCode_ToCurrencyCode_Provider_AsOf",
                table: "ExchangeRates");
        }
    }
}
