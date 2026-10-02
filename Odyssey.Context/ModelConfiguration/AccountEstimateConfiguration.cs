using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Odyssey.Context.ModelConfiguration;

internal sealed class AccountEstimateConfiguration : IEntityTypeConfiguration<AccountEstimate>
{
    public void Configure(EntityTypeBuilder<AccountEstimate> entity)
    {
        // Cascade-delete an estimate history along with its parent account: the timeline is
        // meaningless once the account is gone, and estimates are only reachable through it.
        entity.HasOne(estimate => estimate.Account)
            .WithMany(account => account.AccountEstimates)
            .HasForeignKey(estimate => estimate.AccountId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
