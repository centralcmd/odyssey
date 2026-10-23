using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Odyssey.Context.ModelConfiguration;

internal sealed class LocationConfiguration : IEntityTypeConfiguration<Location>
{
    public void Configure(EntityTypeBuilder<Location> entity)
    {
        entity.Property(location => location.Source).HasConversion<int>();

        // The database-side twins of the DTO bounds (issue #350 §4, N7). decimal(7,5) alone admits ±99.99999
        // and decimal(8,5) ±999.99999, so the ranges are stated here too; a hand edit or a restore cannot
        // bypass model validation the way a request cannot. 0 is a permanent hole in LocationSource.
        entity.ToTable(tb =>
        {
            tb.HasCheckConstraint("CK_Locations_SourceKnown", "`Source` IN (1, 2, 3)");
            tb.HasCheckConstraint("CK_Locations_ManualHasNoReference", "`Source` <> 1 OR `SourceReference` IS NULL");
            tb.HasCheckConstraint("CK_Locations_LatitudeRange", "`Latitude` BETWEEN -90 AND 90");
            tb.HasCheckConstraint("CK_Locations_LongitudeRange", "`Longitude` BETWEEN -180 AND 180");
            tb.HasCheckConstraint(
                "CK_Locations_AltitudeRange", "`AltitudeMeters` IS NULL OR `AltitudeMeters` BETWEEN -500 AND 9000");
        });
    }
}
