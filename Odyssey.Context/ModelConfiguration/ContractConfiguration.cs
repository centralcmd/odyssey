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
    }
}
