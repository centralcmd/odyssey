using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Odyssey.Context.ModelConfiguration;

internal sealed class ContractEventConfiguration : IEntityTypeConfiguration<ContractEvent>
{
    public void Configure(EntityTypeBuilder<ContractEvent> entity)
    {
        // Stored as the int ordinal issue #138 §4 pins. Other is both the catch-all member and the
        // entity default, so the HasDefaultValue / HasSentinel pair matches Contract.Type: an
        // omitted type binds to Other rather than failing, and EF leaves it out of the INSERT.
        entity.Property(e => e.Type)
            .HasColumnName("Type")
            .IsRequired()
            .HasDefaultValue(ContractEventType.Other)
            .HasSentinel(ContractEventType.Other)
            .HasConversion<int>();

        entity.HasIndex(e => new { e.ContractId, e.OccurredAt });
    }
}
