using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Odyssey.Context.ModelConfiguration;

internal sealed class PropertyConfiguration : IEntityTypeConfiguration<Property>
{
    public void Configure(EntityTypeBuilder<Property> entity)
    {
        // A Contact-shaped aggregate: two 1:1 detail sub-records sharing the parent PK, plus two
        // sibling tables (estimates, smart tags) mirroring the account/contract ones.
        entity.Property(p => p.Type).HasConversion<int>();

        // The detail row is meaningless without its parent and reachable only through it.
        entity.HasOne(p => p.RealEstateDetails)
            .WithOne(d => d.Property)
            .HasForeignKey<RealEstateDetails>(d => d.PropertyId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasOne(p => p.VehicleDetails)
            .WithOne(d => d.Property)
            .HasForeignKey<VehicleDetails>(d => d.PropertyId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
