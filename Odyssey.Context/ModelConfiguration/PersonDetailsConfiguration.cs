using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Odyssey.Dtos;

namespace Odyssey.Context.ModelConfiguration;

internal sealed class PersonDetailsConfiguration : IEntityTypeConfiguration<PersonDetails>
{
    public void Configure(EntityTypeBuilder<PersonDetails> entity)
    {
        entity.Property(p => p.Sex).HasConversion<int>();
    }
}
