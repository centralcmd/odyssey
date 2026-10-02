using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Odyssey.Context.ModelConfiguration;

internal sealed class FileAnalysisJobConfiguration : IEntityTypeConfiguration<FileAnalysisJob>
{
    public void Configure(EntityTypeBuilder<FileAnalysisJob> entity)
    {
        entity.Property(j => j.Status)
            .IsRequired()
            .HasDefaultValue(FileAnalysisJobStatus.New)
            .HasSentinel(FileAnalysisJobStatus.New)
            .HasConversion<int>();

        entity.Property(j => j.AnalyzerProvider)
            .IsRequired()
            .HasDefaultValue(AnalyzerProvider.None)
            .HasSentinel(AnalyzerProvider.None)
            .HasConversion<int>();

        entity.Property(j => j.MatchStatus)
            .IsRequired()
            .HasDefaultValue(FileAnalysisMatchStatus.NotRun)
            .HasSentinel(FileAnalysisMatchStatus.NotRun)
            .HasConversion<int>();
    }
}
