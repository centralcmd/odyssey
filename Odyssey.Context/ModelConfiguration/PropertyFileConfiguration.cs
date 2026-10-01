using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Odyssey.Context.ModelConfiguration;

internal sealed class PropertyFileConfiguration : IEntityTypeConfiguration<PropertyFile>
{
    public void Configure(EntityTypeBuilder<PropertyFile> entity)
    {
        // Other is ordinal 0 here (issue #210 §4), so the default/sentinel pair names the CLR
        // default: an omitted type binds to Other and EF leaves it out of the INSERT.
        entity.Property(f => f.FileType)
            .IsRequired()
            .HasDefaultValue(PropertyFileType.Other)
            .HasSentinel(PropertyFileType.Other)
            .HasConversion<int>();

        entity.HasOne(f => f.Property)
            .WithMany(property => property.Files)
            .HasForeignKey(f => f.PropertyId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
