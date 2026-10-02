using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Odyssey.Context.ModelConfiguration;

internal sealed class ContractSmartTagConfiguration : IEntityTypeConfiguration<ContractSmartTag>
{
    public void Configure(EntityTypeBuilder<ContractSmartTag> entity)
    {
        // Composite key: one association per (contract, tag) pair, no surrogate id. This is also
        // what makes add/remove idempotent at the database level and what lets the route address a
        // link by its pair rather than by a link-row id (issue #166 §4).
        entity.HasKey(smartTag => new { smartTag.ContractId, smartTag.TransactionTagId });

        // Cascade-delete a contract's smart-tag links along with the contract itself: the saved
        // filter is meaningless once the contract is gone, and links are only reachable through it.
        entity.HasOne(smartTag => smartTag.Contract)
            .WithMany(contract => contract.SmartTags)
            .HasForeignKey(smartTag => smartTag.ContractId)
            .OnDelete(DeleteBehavior.Cascade);

        // Restrict on the tag side so an in-use tag cannot be hard-deleted out from under a
        // contract's smart-tag configuration (mirrors AccountSmartTag and TransactionTagLink).
        // TransactionTagService.Delete pre-checks for it, because this key's violation reaches
        // GlobalExceptionHandler as a generic 409 naming no surface, and because the EF InMemory
        // tiers enforce no foreign keys at all.
        entity.HasOne(smartTag => smartTag.TransactionTag)
            .WithMany(tag => tag.ContractSmartTags)
            .HasForeignKey(smartTag => smartTag.TransactionTagId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
