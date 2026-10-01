using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Odyssey.Context.ModelConfiguration;

internal sealed class JournalEntryConfiguration : IEntityTypeConfiguration<JournalEntry>
{
    public void Configure(EntityTypeBuilder<JournalEntry> entity)
    {
        // Binary collation for a case-sensitive UID; JournalTaskConfiguration gives the reason.
        entity.Property(entry => entry.ExternalUid)
            .UseCollation("utf8mb4_bin");
    }
}
