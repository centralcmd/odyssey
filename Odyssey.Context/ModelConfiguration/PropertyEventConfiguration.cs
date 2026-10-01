using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Odyssey.Context.ModelConfiguration;

internal sealed class PropertyEventConfiguration : IEntityTypeConfiguration<PropertyEvent>
{
    public void Configure(EntityTypeBuilder<PropertyEvent> entity)
    {
        // One physical column with ContractEvent.Type. EF requires every property sharing a column
        // to declare the same default, so this restates the contract branch's DEFAULT 8 — it is
        // NOT a property default: 8 is no PropertyEventType member. The sentinel is 0, which no
        // member equals either, so EF always sends a real property Type; only an unset (0) value
        // would fall back to the column's 8, and CK_Events_TypeMatchesOwner rejects that rather
        // than letting it pass as a contract type (issue #209 §3.2, AC 3a).
        entity.Property(e => e.Type)
            .HasColumnName("Type")
            .IsRequired()
            .HasDefaultValue((PropertyEventType)(int)ContractEventType.Other)
            .HasSentinel((PropertyEventType)0)
            .HasConversion<int>();

        entity.HasOne(e => e.Property)
            .WithMany(p => p.Events)
            .HasForeignKey(e => e.PropertyId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasIndex(e => new { e.PropertyId, e.OccurredAt });
    }
}
