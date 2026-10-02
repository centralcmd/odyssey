using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Odyssey.Context.ModelConfiguration;

internal sealed class FileAnalysisCandidateTagConfiguration : IEntityTypeConfiguration<FileAnalysisCandidateTag>
{
    public void Configure(EntityTypeBuilder<FileAnalysisCandidateTag> entity)
    {
        // Cascade from the candidate (its match set is meaningless once it's gone); cascade from
        // the tag too — a deleted tag drops its candidate links.
        entity.HasOne(ct => ct.CandidateTransaction)
            .WithMany(c => c.MatchedTags)
            .HasForeignKey(ct => ct.CandidateTransactionId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasOne(ct => ct.TransactionTag)
            .WithMany()
            .HasForeignKey(ct => ct.TransactionTagId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
