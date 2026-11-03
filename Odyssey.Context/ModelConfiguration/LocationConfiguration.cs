using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Odyssey.Context.ModelConfiguration;

internal sealed class LocationConfiguration : IEntityTypeConfiguration<Location>
{
    public void Configure(EntityTypeBuilder<Location> entity)
    {
        entity.Property(location => location.Source).HasConversion<int>();

        entity.Property(location => location.CountryCode).HasColumnType("char(2)");

        // Hex only, so ascii with a binary collation: the comparison is exact and the index is a quarter of
        // the utf8mb4 width. Computed by the application (LocationAddressKey), never by the database.
        entity.Property(location => location.AddressKey)
            .HasColumnType("char(64)")
            .HasCharSet("ascii")
            .UseCollation("ascii_bin");

        // A merged-away location forwards to its survivor (issue #380 §5.6). RESTRICT: a survivor cannot be
        // deleted while something still forwards to it.
        entity.HasOne<Location>()
            .WithMany()
            .HasForeignKey(location => location.MergedIntoLocationId)
            .OnDelete(DeleteBehavior.Restrict);

        // The weather link (issue #387 §4). SET NULL: deleting a point unlinks its places, never blocks.
        entity.HasOne<WeatherPoint>()
            .WithMany()
            .HasForeignKey(location => location.WeatherPointId)
            .OnDelete(DeleteBehavior.SetNull);

        // The database-side twins of the DTO bounds (issue #350 §4, N7). decimal(7,5) alone admits ±99.99999
        // and decimal(8,5) ±999.99999, so the ranges are stated here too; a hand edit or a restore cannot
        // bypass model validation the way a request cannot. 0 is a permanent hole in LocationSource.
        entity.ToTable(tb =>
        {
            // 4–7 are the issue #380 sources (Converted, ContactAddress, PropertyAddress, Imported).
            tb.HasCheckConstraint("CK_Locations_SourceKnown", "`Source` IN (1, 2, 3, 4, 5, 6, 7)");
            tb.HasCheckConstraint(
                "CK_Locations_CoordinatesAllOrNone",
                "(`Latitude` IS NULL AND `Longitude` IS NULL AND `TimeZoneId` IS NULL) OR " +
                "(`Latitude` IS NOT NULL AND `Longitude` IS NOT NULL AND `TimeZoneId` IS NOT NULL)");
            tb.HasCheckConstraint("CK_Locations_ManualHasNoReference", "`Source` <> 1 OR `SourceReference` IS NULL");
            tb.HasCheckConstraint("CK_Locations_LatitudeRange", "`Latitude` BETWEEN -90 AND 90");
            tb.HasCheckConstraint("CK_Locations_LongitudeRange", "`Longitude` BETWEEN -180 AND 180");
            tb.HasCheckConstraint(
                "CK_Locations_AltitudeRange", "`AltitudeMeters` IS NULL OR `AltitudeMeters` BETWEEN -500 AND 9000");
        });
    }
}
