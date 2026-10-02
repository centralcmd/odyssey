using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Odyssey.Context.ModelConfiguration;

internal sealed class AccountSmartTagConfiguration : IEntityTypeConfiguration<AccountSmartTag>
{
    public void Configure(EntityTypeBuilder<AccountSmartTag> entity)
    {
        // Composite key: one association per (account, tag) pair, no surrogate id.
        entity.HasKey(smartTag => new { smartTag.AccountId, smartTag.TransactionTagId });

        // Cascade-delete an account's smart-tag links along with the account itself: the saved
        // filter is meaningless once the account is gone, and links are only reachable through it.
        entity.HasOne(smartTag => smartTag.Account)
            .WithMany(account => account.SmartTags)
            .HasForeignKey(smartTag => smartTag.AccountId)
            .OnDelete(DeleteBehavior.Cascade);

        // Restrict on the tag side so an in-use tag cannot be hard-deleted out from under an
        // account's smart-tag configuration (mirrors the TransactionTagLink precedent).
        entity.HasOne(smartTag => smartTag.TransactionTag)
            .WithMany(tag => tag.AccountSmartTags)
            .HasForeignKey(smartTag => smartTag.TransactionTagId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
