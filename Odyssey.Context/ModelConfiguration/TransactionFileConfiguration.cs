using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Odyssey.Context.ModelConfiguration;

internal sealed class TransactionFileConfiguration : IEntityTypeConfiguration<TransactionFile>
{
    public void Configure(EntityTypeBuilder<TransactionFile> entity)
    {
        entity.Property(tf => tf.Type)
            .IsRequired()
            .HasDefaultValue(TransactionFileType.Other)
            .HasSentinel(TransactionFileType.Other)
            .HasConversion<int>();

        entity.ToTable(tb => tb.HasCheckConstraint(
            "CK_TransactionFiles_Type_AllowedValues",
            "`Type` IN (0, 1, 2, 3, 4, 5, 6)"));
    }
}
