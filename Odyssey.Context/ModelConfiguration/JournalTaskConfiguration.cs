using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Odyssey.Context.ModelConfiguration;

internal sealed class JournalTaskConfiguration : IEntityTypeConfiguration<JournalTask>
{
    public void Configure(EntityTypeBuilder<JournalTask> entity)
    {
        // ExternalUid is the row's external identity anchor (issues #337/#339). VTODO/VJOURNAL/vCard UIDs
        // are case-sensitive, and the import matches them with StringComparer.Ordinal — so the unique index
        // must be case-sensitive too. A binary collation makes DB uniqueness agree with that matching
        // (the default utf8mb4 collation is case-insensitive, which would reject case-variant UIDs).
        entity.Property(task => task.ExternalUid)
            .UseCollation("utf8mb4_bin");
    }
}
