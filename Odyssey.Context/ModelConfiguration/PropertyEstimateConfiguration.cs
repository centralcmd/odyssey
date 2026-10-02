using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Odyssey.Context.ModelConfiguration;

internal sealed class PropertyEstimateConfiguration : IEntityTypeConfiguration<PropertyEstimate>
{
    public void Configure(EntityTypeBuilder<PropertyEstimate> entity)
    {
        // CASCADE, like AccountEstimate: the history is meaningless once its subject is gone.
        entity.HasOne(estimate => estimate.Property)
            .WithMany(property => property.Estimates)
            .HasForeignKey(estimate => estimate.PropertyId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
