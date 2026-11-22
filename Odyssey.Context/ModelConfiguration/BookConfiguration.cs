using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Odyssey.Context.ModelConfiguration;

/// <summary>
/// The book catalogue (issue #527 §4). Contacts are RESTRICT from both the main author and the contributor rows, so a
/// credited contact cannot be deleted; <c>IContactBookCredits</c> turns that into an explanatory 409 first. Photo and
/// file links cascade from their target, as the journal's links do. Every child unique index is safe because the
/// service diffs links in place rather than deleting and re-inserting a key pair in one save.
/// </summary>
internal sealed class BookConfiguration : IEntityTypeConfiguration<Book>
{
    public void Configure(EntityTypeBuilder<Book> entity)
    {
        entity.HasOne<Contact>()
            .WithMany()
            .HasForeignKey(book => book.MainAuthorContactId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class BookContributorConfiguration : IEntityTypeConfiguration<BookContributor>
{
    public void Configure(EntityTypeBuilder<BookContributor> entity)
    {
        entity.HasOne(contributor => contributor.Book)
            .WithMany(book => book.Contributors)
            .HasForeignKey(contributor => contributor.BookId)
            .OnDelete(DeleteBehavior.Cascade);
        entity.HasOne<Contact>()
            .WithMany()
            .HasForeignKey(contributor => contributor.ContactId)
            .OnDelete(DeleteBehavior.Restrict);
        entity.HasIndex(contributor => new { contributor.BookId, contributor.ContactId }).IsUnique();
    }
}

internal sealed class BookImageConfiguration : IEntityTypeConfiguration<BookImage>
{
    public void Configure(EntityTypeBuilder<BookImage> entity)
    {
        entity.HasOne(image => image.Book)
            .WithMany(book => book.Images)
            .HasForeignKey(image => image.BookId)
            .OnDelete(DeleteBehavior.Cascade);
        entity.HasOne<Photo>()
            .WithMany()
            .HasForeignKey(image => image.PhotoId)
            .OnDelete(DeleteBehavior.Cascade);
        entity.HasIndex(image => new { image.BookId, image.PhotoId }).IsUnique();
    }
}

internal sealed class BookAttachmentConfiguration : IEntityTypeConfiguration<BookAttachment>
{
    public void Configure(EntityTypeBuilder<BookAttachment> entity)
    {
        entity.HasOne(attachment => attachment.Book)
            .WithMany(book => book.Attachments)
            .HasForeignKey(attachment => attachment.BookId)
            .OnDelete(DeleteBehavior.Cascade);
        entity.HasOne<FileMetadata>()
            .WithMany()
            .HasForeignKey(attachment => attachment.FileId)
            .OnDelete(DeleteBehavior.Cascade);
        entity.HasIndex(attachment => new { attachment.BookId, attachment.FileId }).IsUnique();
    }
}

internal sealed class BookTagLinkConfiguration : IEntityTypeConfiguration<BookTagLink>
{
    public void Configure(EntityTypeBuilder<BookTagLink> entity)
    {
        entity.HasOne(link => link.Book)
            .WithMany(book => book.TagLinks)
            .HasForeignKey(link => link.BookId)
            .OnDelete(DeleteBehavior.Cascade);
        entity.HasOne(link => link.BookTag)
            .WithMany(tag => tag.Links)
            .HasForeignKey(link => link.BookTagId)
            .OnDelete(DeleteBehavior.Restrict);
        entity.HasIndex(link => new { link.BookId, link.BookTagId }).IsUnique();
    }
}
