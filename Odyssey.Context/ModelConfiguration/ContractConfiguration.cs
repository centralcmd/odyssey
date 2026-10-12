using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Odyssey.Context.ModelConfiguration;

internal sealed class ContractConfiguration : IEntityTypeConfiguration<Contract>
{
    public void Configure(EntityTypeBuilder<Contract> entity)
    {
        entity.Property(c => c.Type)
            .IsRequired()
            .HasDefaultValue(ContractType.Other)
            .HasSentinel(ContractType.Other)
            .HasConversion<int>();

        // The service's 422 is the real guard; this CHECK is the backstop (issue #308 §4). Both
        // bounds are on one row, so the database can hold the order outright. Same day is valid.
        entity.ToTable(tb => tb.HasCheckConstraint(
            "CK_Contracts_SmartTagScopeOrder",
            "`SmartTagScopeStart` IS NULL OR `SmartTagScopeEnd` IS NULL OR `SmartTagScopeStart` <= `SmartTagScopeEnd`"));
    }
}
