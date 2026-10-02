using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Odyssey.Context.ModelConfiguration;

internal sealed class ContactConfiguration : IEntityTypeConfiguration<Contact>
{
    public void Configure(EntityTypeBuilder<Contact> entity)
    {
        // Contacts are a Journal-domain concept, referenced from both halves of the model. The
        // aggregate's internal relationships are declared here; the Finance references to it are
        // declared with the other cross-module keys in OdysseyContext.ConfigureCrossModuleForeignKeys.
        entity.Property(c => c.Type).HasConversion<int>();

        // 1:1 detail sub-records sharing the parent PK; cascade-delete with the contact.
        entity.HasOne(c => c.PersonDetails)
            .WithOne(p => p.Contact)
            .HasForeignKey<PersonDetails>(p => p.ContactId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasOne(c => c.OrganizationDetails)
            .WithOne(o => o.Contact)
            .HasForeignKey<OrganizationDetails>(o => o.ContactId)
            .OnDelete(DeleteBehavior.Cascade);

        // Aliases (issue #48): the fourth child collection. CASCADE like its three siblings — an
        // alias has no independent existence and no cross-module referent, so the SET NULL of the
        // optional cross-module links does not apply. Declared BEFORE the unique (ContactId, Value) index is considered so EF's
        // FK-index suppression sees the composite and emits no redundant IX_ContactAliases_ContactId.
        entity.HasMany(c => c.Aliases)
            .WithOne(a => a.Contact)
            .HasForeignKey(a => a.ContactId)
            .OnDelete(DeleteBehavior.Cascade);

        // n:1 contact collections; cascade-delete with the contact.
        entity.HasMany(c => c.Addresses)
            .WithOne(a => a.Contact)
            .HasForeignKey(a => a.ContactId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasMany(c => c.EmailAddresses)
            .WithOne(e => e.Contact)
            .HasForeignKey(e => e.ContactId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasMany(c => c.PhoneNumbers)
            .WithOne(p => p.Contact)
            .HasForeignKey(p => p.ContactId)
            .OnDelete(DeleteBehavior.Cascade);

        // The binary collation for the reason JournalTaskConfiguration gives. Contact is the vCard case
        // that comment names, and the only one of the three whose ExternalUid index is UNIQUE — so
        // without the binary collation two vCards whose UIDs differ only in case collide: ContactService.FindIdByExternalUid resolves the second onto the first
        // (import treats it as an update, not a create) and the unique index rejects the distinct UID.
        entity.Property(contact => contact.ExternalUid)
            .UseCollation("utf8mb4_bin");
    }
}
