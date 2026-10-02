using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Odyssey.Context.ModelConfiguration;

internal sealed class TermsOfServiceAcceptanceConfiguration : IEntityTypeConfiguration<TermsOfServiceAcceptance>
{
    public void Configure(EntityTypeBuilder<TermsOfServiceAcceptance> entity)
    {
        // Legal acceptance records (issue #354 §6). The two acceptance logs intentionally carry no FK to
        // AspNetUsers — they are compliance records that outlive the account, pseudonymized rather than
        // deleted with it. That stays true now that identity shares this context: they are the deliberate
        // exception to the keys in OdysseyContext.ConfigureUserAttribution, and must not be "fixed" into
        // one. The ToS version link is a real FK with Restrict: nothing deletes a version, and if
        // something ever tried, failing loudly is the correct outcome for an acceptance record.
        entity.HasOne<TermsOfServiceVersion>()
            .WithMany()
            .HasForeignKey(acceptance => acceptance.TermsOfServiceVersionId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
