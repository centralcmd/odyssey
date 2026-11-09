using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Odyssey.Dtos.Finance;

namespace Odyssey.Context.ModelConfiguration;

internal sealed class InstrumentConfiguration : IEntityTypeConfiguration<Instrument>
{
    public void Configure(EntityTypeBuilder<Instrument> entity)
    {
        // RESTRICT: a currency an instrument is quoted in cannot be removed from under it. CurrencyService counts
        // instruments as a delete blocker, so this key is the backstop, not the guard.
        entity.HasOne<Currency>()
            .WithMany()
            .HasForeignKey(instrument => instrument.CurrencyCode)
            .HasPrincipalKey(currency => currency.CurrencyCode)
            .OnDelete(DeleteBehavior.Restrict);

        entity.ToTable(tb => tb.HasCheckConstraint("CK_Instruments_Type", "`Type` IN (1, 2, 3, 9)"));
    }
}

internal sealed class InstrumentPriceConfiguration : IEntityTypeConfiguration<InstrumentPrice>
{
    public void Configure(EntityTypeBuilder<InstrumentPrice> entity)
    {
        entity.Property(price => price.Close)
            .HasPrecision(MoneyBoundsPrecision, InstrumentPriceRules.MaxDecimals);

        entity.Property(price => price.SourceKeyGuard)
            .HasComputedColumnSql("COALESCE(`SourceKey`, '')", stored: true);

        // RESTRICT: an instrument with prices cannot be deleted; prices leave through the purge first, so a
        // misclick cannot destroy fetched history.
        entity.HasOne<Instrument>()
            .WithMany()
            .HasForeignKey(price => price.InstrumentId)
            .OnDelete(DeleteBehavior.Restrict);

        entity.ToTable(tb =>
        {
            tb.HasCheckConstraint("CK_InstrumentPrices_ClosePositive", "`Close` > 0");
            tb.HasCheckConstraint(
                "CK_InstrumentPrices_SourceKeyFormat",
                $"`SourceKey` IS NULL OR `SourceKey` REGEXP '{InstrumentIdentifierRules.SourceKeyPattern}'");
        });
    }

    private const int MoneyBoundsPrecision = Odyssey.Dtos.MoneyBounds.InstrumentPricePrecision;
}

internal sealed class InstrumentPriceSourceBindingConfiguration : IEntityTypeConfiguration<InstrumentPriceSourceBinding>
{
    public void Configure(EntityTypeBuilder<InstrumentPriceSourceBinding> entity)
    {
        // CASCADE: a binding means nothing without its instrument. The instrument cannot be deleted while it has
        // prices, so this never orphans fetched rows.
        entity.HasOne<Instrument>()
            .WithMany()
            .HasForeignKey(binding => binding.InstrumentId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.ToTable(tb =>
        {
            tb.HasCheckConstraint(
                "CK_InstrumentPriceSourceBindings_Priority",
                $"`Priority` BETWEEN 1 AND {InstrumentPriceBindingLimits.MaxBindingsPerInstrument}");
            tb.HasCheckConstraint(
                "CK_InstrumentPriceSourceBindings_SourceKeyFormat", $"`SourceKey` REGEXP '{InstrumentIdentifierRules.SourceKeyPattern}'");
            tb.HasCheckConstraint(
                "CK_InstrumentPriceSourceBindings_ConsecutiveFailures", "`ConsecutiveFailures` >= 0");
        });
    }
}

internal sealed class InstrumentPriceSourceStateConfiguration : IEntityTypeConfiguration<InstrumentPriceSourceState>
{
    public void Configure(EntityTypeBuilder<InstrumentPriceSourceState> entity)
    {
        entity.ToTable(tb => tb.HasCheckConstraint(
            "CK_InstrumentPriceSourceStates_RequestCount", "`RequestCount` >= 0"));
    }
}
