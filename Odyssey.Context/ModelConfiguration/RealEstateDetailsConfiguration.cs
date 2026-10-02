using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Odyssey.Context.ModelConfiguration;

internal sealed class RealEstateDetailsConfiguration : IEntityTypeConfiguration<RealEstateDetails>
{
    public void Configure(EntityTypeBuilder<RealEstateDetails> entity)
    {
        entity.Property(d => d.Kind).HasConversion<int>();
    }
}
