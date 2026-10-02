using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Odyssey.Context.ModelConfiguration;

internal sealed class OwnedEventConfiguration : IEntityTypeConfiguration<OwnedEvent>
{
    public void Configure(EntityTypeBuilder<OwnedEvent> entity)
    {
        // ContractEvent and PropertyEvent share one table through TPH — a recorded, deliberate
        // exception to the sibling-table convention. The four controls that contain it are the
        // discriminator, the two CHECKs here, and owner-scoped service queries (issue #209 §3.2).
        entity.ToTable("Events", tb =>
        {
            // A row names exactly one owner, and the one the discriminator says.
            tb.HasCheckConstraint(
                "CK_Events_ExactlyOneOwner",
                "(`OwnerKind` = 0 AND `ContractId` IS NOT NULL AND `PropertyId` IS NULL) OR " +
                "(`OwnerKind` = 1 AND `PropertyId` IS NOT NULL AND `ContractId` IS NULL)");

            // Each owner's Type stays inside its own disjoint ordinal range, so a stored value always
            // identifies its enum. The build-time twin is the ordinal-range guard test. The explicit
            // IS NOT NULL is load-bearing: TPH maps a column declared on derived types as NULLable,
            // and a CHECK whose predicate evaluates to UNKNOWN passes — BETWEEN alone would admit a
            // NULL Type.
            tb.HasCheckConstraint(
                "CK_Events_TypeMatchesOwner",
                "`Type` IS NOT NULL AND (" +
                "(`OwnerKind` = 0 AND `Type` BETWEEN 0 AND 99) OR " +
                "(`OwnerKind` = 1 AND `Type` BETWEEN 100 AND 199))");
        });

        entity.HasDiscriminator<EventOwnerKind>("OwnerKind")
            .HasValue<ContractEvent>(EventOwnerKind.Contract)
            .HasValue<PropertyEvent>(EventOwnerKind.Property);

        entity.Property<EventOwnerKind>("OwnerKind").HasConversion<int>();

        // Same treatment, same reason (issue #154 §4): stored as the int ordinal, User is both the
        // entity default and every pre-#154 row's value, and the sentinel keeps a hand-written row
        // out of the INSERT's column list. Declared here rather than left to the migration's raw
        // default, so the model snapshot and the database agree on it.
        entity.Property(e => e.Source)
            .IsRequired()
            .HasDefaultValue(ContractEventSource.User)
            .HasSentinel(ContractEventSource.User)
            .HasConversion<int>();
    }
}
