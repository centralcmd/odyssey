using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Odyssey.Context.ModelConfiguration;

internal sealed class TermConfiguration : IEntityTypeConfiguration<Term>
{
    public void Configure(EntityTypeBuilder<Term> entity)
    {
        // A contract's price history is meaningless once the contract is gone and is only
        // reachable through it. The contract is the only owner since issue #190.
        entity.HasOne(term => term.Contract)
            .WithMany(contract => contract.Terms)
            .HasForeignKey(term => term.ContractId)
            .OnDelete(DeleteBehavior.Cascade);

        // Stored as UTC and read back as UTC: the provider materialises a datetime(6) as
        // Unspecified, which would serialise without its `Z` and read as the viewer's local time.
        entity.Property(term => term.DateTimeValue)
            .HasConversion(
                value => value,
                value => value.HasValue ? DateTime.SpecifyKind(value.Value, DateTimeKind.Utc) : value);

        // Exactly one value column is set, and it is the one the unit names (issue #192). The domain
        // service is the real guard; this backs it up against a hand edit or a restore. An unknown
        // ordinal matches no arm, so it also backs up the enum range.
        entity.ToTable(tb => tb.HasCheckConstraint(
            "CK_Terms_ValueMatchesUnit",
            "(`ValueUnit` IN (0, 1) AND `Value` IS NOT NULL AND `TextValue` IS NULL AND `DateTimeValue` IS NULL) "
            + "OR (`ValueUnit` = 2 AND `Value` IS NULL AND `TextValue` IS NOT NULL AND `DateTimeValue` IS NULL) "
            + "OR (`ValueUnit` = 3 AND `Value` IS NULL AND `TextValue` IS NULL AND `DateTimeValue` IS NOT NULL)"));
    }
}
