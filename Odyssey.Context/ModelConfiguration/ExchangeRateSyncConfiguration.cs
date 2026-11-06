using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Odyssey.Context.ModelConfiguration;

internal sealed class ExchangeRateSyncCurrencyConfiguration : IEntityTypeConfiguration<ExchangeRateSyncCurrency>
{
    public void Configure(EntityTypeBuilder<ExchangeRateSyncCurrency> entity)
    {
        // CASCADE: configuration for a removed currency means nothing. Deleting a currency is already
        // blocked while any exchange rate names it, so this never removes a currency with fetched rows.
        entity.HasOne<Currency>()
            .WithMany()
            .HasForeignKey(sync => sync.CurrencyCode)
            .HasPrincipalKey(currency => currency.CurrencyCode)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
