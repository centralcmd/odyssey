using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Odyssey.Context.ModelConfiguration;

internal sealed class VehicleDetailsConfiguration : IEntityTypeConfiguration<VehicleDetails>
{
    public void Configure(EntityTypeBuilder<VehicleDetails> entity)
    {
        entity.Property(d => d.Kind).HasConversion<int>();
    }
}
