using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Odyssey.Context.ModelConfiguration;

internal sealed class ExchangeRateConfiguration : IEntityTypeConfiguration<ExchangeRate>
{
    public void Configure(EntityTypeBuilder<ExchangeRate> entity)
    {
        // 1 unit of From = Rate units of To; decimal(18,8) keeps the precision the
        // ISO minor-unit range never needs to round prematurely during conversion.
        entity.Property(rate => rate.Rate)
            .HasPrecision(18, 8);

        // FK to the currency table for both ends of the pair. No cascade delete: rate rows are
        // never removed as a side effect of another change (only in-place Rate/AsOf corrections
        // or an explicit delete), so a currency should not be removable out from under them by
        // accident.
        entity.HasOne<Currency>()
            .WithMany()
            .HasForeignKey(rate => rate.FromCurrencyCode)
            .HasPrincipalKey(currency => currency.CurrencyCode)
            .OnDelete(DeleteBehavior.Restrict);

        entity.HasOne<Currency>()
            .WithMany()
            .HasForeignKey(rate => rate.ToCurrencyCode)
            .HasPrincipalKey(currency => currency.CurrencyCode)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
