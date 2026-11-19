using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Odyssey.Context.Migrations
{
    /// <summary>
    /// Release 2 of issue #472: fetched rates are stored in the published <c>X → NOK</c> direction only, and the
    /// readers (release 1) invert them. This deletes each fetched <c>NOK → X</c> row that has its fetched
    /// <c>X → NOK</c> partner at the same <c>AsOf</c>. A manual <c>NOK → X</c> row and a partnerless fetched one
    /// are kept: the first was entered by a person, and the second would otherwise leave its date with no rate.
    /// </summary>
    /// <remarks>
    /// <c>Down()</c> re-derives the deleted rows from their partners and is NOT an exact restore: the original
    /// reciprocal was rounded from the published value, this one from the stored <c>X → NOK</c> rate. The two are
    /// equal whenever that rate was stored exactly (every fixture series is, issue #472 §7.11) and can differ in
    /// the last decimal otherwise. <c>ExchangeRateId</c> is new; <c>CreatedAt</c> is copied from the partner, which
    /// is what the sync gave the original (both rows were inserted in one save), so a tie against a manual row on
    /// the same <c>AsOf</c> resolves as before.
    /// </remarks>
    public partial class StoreFetchedRatesInPublishedDirectionOnly : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DELETE r FROM ExchangeRates r
                JOIN ExchangeRates p
                  ON p.FromCurrencyCode = r.ToCurrencyCode
                 AND p.ToCurrencyCode   = 'NOK'
                 AND p.AsOf             = r.AsOf
                 AND p.Provider         = r.Provider
                WHERE r.Provider IS NOT NULL
                  AND r.FromCurrencyCode = 'NOK';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                INSERT INTO ExchangeRates (ExchangeRateId, FromCurrencyCode, ToCurrencyCode, Rate, AsOf, CreatedAt, Provider)
                SELECT UUID(), 'NOK', p.FromCurrencyCode, ROUND(1 / p.Rate, 8), p.AsOf, p.CreatedAt, p.Provider
                FROM ExchangeRates p
                WHERE p.Provider IS NOT NULL AND p.ToCurrencyCode = 'NOK' AND p.FromCurrencyCode <> 'NOK'
                  AND 1 / p.Rate BETWEEN 0.00000001 AND 9999999999.99999999
                  AND NOT EXISTS (SELECT 1 FROM ExchangeRates e
                                  WHERE e.FromCurrencyCode = 'NOK' AND e.ToCurrencyCode = p.FromCurrencyCode
                                    AND e.AsOf = p.AsOf AND e.Provider = p.Provider);
                """);
        }
    }
}
