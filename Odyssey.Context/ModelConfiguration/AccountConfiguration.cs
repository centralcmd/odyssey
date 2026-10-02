using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Odyssey.Context.ModelConfiguration;

internal sealed class AccountConfiguration : IEntityTypeConfiguration<Account>
{
    public void Configure(EntityTypeBuilder<Account> entity)
    {
        entity.HasIndex(account => account.Name);

        // The retired Property (6) and Vehicle (7) ordinals are permanent holes (issue #218): every such
        // account moved onto a Property record, and this CHECK keeps a hand edit or a restore from
        // reintroducing one. [EnumDataType] is the wire-side twin.
        entity.ToTable(tb => tb.HasCheckConstraint(
            "CK_Accounts_AccountTypeNotRetired",
            "`AccountType` NOT IN (6, 7)"));
    }
}
