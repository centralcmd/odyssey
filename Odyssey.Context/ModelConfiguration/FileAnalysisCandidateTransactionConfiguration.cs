using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Odyssey.Context.ModelConfiguration;

internal sealed class FileAnalysisCandidateTransactionConfiguration
    : IEntityTypeConfiguration<FileAnalysisCandidateTransaction>
{
    public void Configure(EntityTypeBuilder<FileAnalysisCandidateTransaction> entity)
    {
        // The concurrency token is what makes "a candidate is imported once" hold under two racing
        // import requests (issue #237): both can read Pending, but only the first UPDATE matches
        // `WHERE ReviewStatus = 0`, so the second save — its ledger rows included — rolls back.
        entity.Property(ct => ct.ReviewStatus)
            .IsRequired()
            .HasDefaultValue(CandidateTransactionReviewStatus.Pending)
            .HasSentinel(CandidateTransactionReviewStatus.Pending)
            .HasConversion<int>()
            .IsConcurrencyToken();

        entity.Property(ct => ct.MatchMethod)
            .IsRequired()
            .HasDefaultValue(MatchMethod.None)
            .HasSentinel(MatchMethod.None)
            .HasConversion<int>();

        // MatchedContactId's FK to Contact is declared with the other cross-module keys in
        // OdysseyContext.ConfigureCrossModuleForeignKeys.
    }
}
