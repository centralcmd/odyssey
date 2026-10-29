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

internal sealed class ExchangeRateSyncRunConfiguration : IEntityTypeConfiguration<ExchangeRateSyncRun>
{
    public void Configure(EntityTypeBuilder<ExchangeRateSyncRun> entity)
    {
        entity.Property(run => run.RunningGuard)
            .HasComputedColumnSql("IF(`Status` = 0, 1, NULL)", stored: false);

        entity.ToTable(tb =>
        {
            tb.HasCheckConstraint("CK_ExchangeRateSyncRuns_Trigger", "`Trigger` IN (0, 1, 2)");
            tb.HasCheckConstraint("CK_ExchangeRateSyncRuns_Status", "`Status` IN (0, 1, 2, 3, 4)");
            tb.HasCheckConstraint(
                "CK_ExchangeRateSyncRuns_ErrorKind", "`ErrorKind` IS NULL OR `ErrorKind` BETWEEN 1 AND 9");
            tb.HasCheckConstraint("CK_ExchangeRateSyncRuns_Provider", "`Provider` IN (1)");
        });
    }
}
