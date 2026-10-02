using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Odyssey.Context.ModelConfiguration;

internal sealed class TermsOfServiceVersionConfiguration : IEntityTypeConfiguration<TermsOfServiceVersion>
{
    public void Configure(EntityTypeBuilder<TermsOfServiceVersion> entity)
    {
        entity.Property(version => version.Content).HasColumnType("longtext");

        entity.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(version => version.PublishedByUserId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
