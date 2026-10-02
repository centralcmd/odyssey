using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Odyssey.Context.ModelConfiguration;

internal sealed class PhotoPersonConfiguration : IEntityTypeConfiguration<PhotoPerson>
{
    public void Configure(EntityTypeBuilder<PhotoPerson> entity)
    {
        entity.HasIndex(link => new { link.PhotoId, link.ContactId }).IsUnique();

        entity.HasOne(link => link.Photo)
            .WithMany(photo => photo.People)
            .HasForeignKey(link => link.PhotoId)
            .OnDelete(DeleteBehavior.Cascade);

        // Contact moved into this context: the person link is a real FK. Cascade so a photo's person
        // links die with the contact.
        entity.HasOne<Contact>()
            .WithMany()
            .HasForeignKey(link => link.ContactId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
