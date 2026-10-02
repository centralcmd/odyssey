using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Odyssey.Dtos.Finance;

namespace Odyssey.Context.ModelConfiguration;

internal sealed class TaxStatementConfiguration : IEntityTypeConfiguration<TaxStatement>
{
    public void Configure(EntityTypeBuilder<TaxStatement> entity)
    {
        entity.Property(s => s.Status)
            .IsRequired()
            .HasDefaultValue(TaxStatementStatus.New)
            .HasSentinel(TaxStatementStatus.New)
            .HasConversion<int>();
    }
}
