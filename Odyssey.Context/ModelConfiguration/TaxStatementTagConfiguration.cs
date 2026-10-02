using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Odyssey.Context.ModelConfiguration;

internal sealed class TaxStatementTagConfiguration : IEntityTypeConfiguration<TaxStatementTag>
{
    public void Configure(EntityTypeBuilder<TaxStatementTag> entity)
    {
        entity.Property(t => t.Role)
            .IsRequired()
            .HasConversion<int>();
    }
}
