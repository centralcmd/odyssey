using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Odyssey.Context.ModelConfiguration;

internal sealed class ContractFileConfiguration : IEntityTypeConfiguration<ContractFile>
{
    public void Configure(EntityTypeBuilder<ContractFile> entity)
    {
        entity.Property(f => f.FileType)
            .IsRequired()
            .HasDefaultValue(ContractFileType.Other)
            .HasSentinel(ContractFileType.Other)
            .HasConversion<int>();
    }
}
