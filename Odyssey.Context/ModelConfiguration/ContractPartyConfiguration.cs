using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Odyssey.Context.ModelConfiguration;

internal sealed class ContractPartyConfiguration : IEntityTypeConfiguration<ContractParty>
{
    public void Configure(EntityTypeBuilder<ContractParty> entity)
    {
        // One-of-three invariant: a party links to exactly one target (issue #174 §6, widened by
        // issue #208 to add PropertyId; the name is kept because its meaning is unchanged).
        // The service layer is the real guard (returns 400); this CHECK is a DB backstop declared
        // on the model so it lands in the snapshot. The app only runs on MariaDB, which honours it.
        // The role is orthogonal to it, so the constraint needs no edit for issue #121.
        entity.ToTable(tb => tb.HasCheckConstraint(
            "CK_ContractParties_ExactlyOneTarget",
            "((`AccountId` IS NOT NULL) + (`ContactId` IS NOT NULL) + (`PropertyId` IS NOT NULL)) = 1"));

        // Stored as int, the ordinal contract issue #121 §4 pins. Deliberately NO HasDefaultValue
        // / HasSentinel pair (unlike Contract.Type): Unspecified is a value a caller can mean, and
        // a sentinel would make EF omit it from the INSERT. The migration's AddColumn default is
        // the one-time backfill for pre-#121 rows and nothing more.
        entity.Property(p => p.Role)
            .IsRequired()
            .HasConversion<int>();
    }
}
