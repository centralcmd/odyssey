using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Odyssey.Context.ModelConfiguration;

internal sealed class PropertySmartTagConfiguration : IEntityTypeConfiguration<PropertySmartTag>
{
    public void Configure(EntityTypeBuilder<PropertySmartTag> entity)
    {
        // Composite key, exactly as ContractSmartTag: one association per pair, idempotent at the
        // database level, and addressable by the route pair rather than a link-row id.
        entity.HasKey(smartTag => new { smartTag.PropertyId, smartTag.TransactionTagId });

        entity.HasOne(smartTag => smartTag.Property)
            .WithMany(property => property.SmartTags)
            .HasForeignKey(smartTag => smartTag.PropertyId)
            .OnDelete(DeleteBehavior.Cascade);

        // RESTRICT is the backstop, not the guard: TransactionTagService.Delete pre-checks it,
        // because the violation reaches GlobalExceptionHandler as a generic 409 naming no surface
        // and the EF InMemory tiers enforce no foreign keys at all.
        entity.HasOne(smartTag => smartTag.TransactionTag)
            .WithMany(tag => tag.PropertySmartTags)
            .HasForeignKey(smartTag => smartTag.TransactionTagId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
