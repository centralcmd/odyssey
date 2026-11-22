using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Odyssey.Context;
using Odyssey.Core;
using Odyssey.Core.Books;
using Odyssey.Core.Finance;
using Odyssey.Core.Journal;
using Odyssey.Dtos.Books;
using Odyssey.Testing.FileStorage;
using Xunit;

namespace Odyssey.IntegrationTests;

/// <summary>
/// Books behaviour EF InMemory cannot verify (issue #527 §12): the <c>RESTRICT</c> keys to <c>Contacts</c> behind the
/// credit guard, the unique indexes a diff-in-place update must never trip, and the <c>CASCADE</c> keys from a library
/// photo and a Files-store file.
/// </summary>
[Collection(MariaDbSharedDatabaseCollection.Name)]
public class BookRelationalIntegrationTests(MariaDbFixture fixture)
{
    private const string User = "user";

    private static readonly BookCaller Caller = new(User, true, true, true, true);

    [SkippableTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_credited_contact_cannot_be_deleted_and_survives(bool asMainAuthor)
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await using var host = await CreateHostAsync();

        var author = await SeedContactAsync(host, "Primary Author");
        var contributor = await SeedContactAsync(host, "Second Hand");
        await CreateBookAsync(host, author, contributors: [contributor]);
        var target = asMainAuthor ? author : contributor;

        await using (var scope = host.Host.Scope())
        {
            var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            var service = new ContactService(
                context, new ContactReferenceGuard(context), bookCredits: new ContactBookCredits(context));
            var conflict = await Assert.ThrowsAsync<DomainConflictException>(() => service.Delete(target));
            Assert.Equal(ContactService.BookCreditMessage, conflict.Message);
        }

        await AssertContactExistsAsync(host, target);
    }

    /// <summary>
    /// The key is the backstop, not the guard: a service built without the credit check still cannot delete the
    /// contact, and the <c>1451</c> surfaces as the same <c>409</c> rather than a raw <c>DbUpdateException</c> — with the
    /// delete's transaction rolled back.
    /// </summary>
    [SkippableTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_restrict_key_backstops_a_delete_that_skips_the_guard(bool asMainAuthor)
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await using var host = await CreateHostAsync();

        var author = await SeedContactAsync(host, "Primary Author");
        var contributor = await SeedContactAsync(host, "Second Hand");
        await CreateBookAsync(host, author, contributors: [contributor]);
        var target = asMainAuthor ? author : contributor;

        await using (var scope = host.Host.Scope())
        {
            var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            var service = new ContactService(context, new ContactReferenceGuard(context));
            var conflict = await Assert.ThrowsAsync<DomainConflictException>(() => service.Delete(target));
            Assert.Equal(ContactService.BookCreditMessage, conflict.Message);
        }

        await AssertContactExistsAsync(host, target);
    }

    [SkippableFact]
    public async Task A_book_with_no_credit_on_a_contact_does_not_block_its_delete()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await using var host = await CreateHostAsync();

        var author = await SeedContactAsync(host, "Primary Author");
        var bystander = await SeedContactAsync(host, "Unrelated");
        await CreateBookAsync(host, author);

        await using var scope = host.Host.Scope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var service = new ContactService(
            context, new ContactReferenceGuard(context), bookCredits: new ContactBookCredits(context));
        await service.Delete(bystander);
        Assert.False(await context.Contacts.AnyAsync(c => c.ContactId == bystander));
    }

    /// <summary>
    /// One <c>PUT</c> that moves the cover from A to B, reorders the contributors, removes one and adds one, and removes
    /// a photo while adding another — every unique index (<c>(BookId, ContactId)</c>, <c>(BookId, PhotoId)</c>) holds
    /// across the save, and exactly one image is the cover.
    /// </summary>
    [SkippableFact]
    public async Task A_full_replacement_diffs_in_place_without_a_duplicate_key()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await using var host = await CreateHostAsync();

        var author = await SeedContactAsync(host, "Primary Author");
        var first = await SeedContactAsync(host, "First");
        var second = await SeedContactAsync(host, "Second");
        var third = await SeedContactAsync(host, "Third");
        var photoA = (await host.UploadAsync(PhotoTestData.Jpeg(PhotoTestData.NextSeed()))).PhotoId;
        var photoB = (await host.UploadAsync(PhotoTestData.Jpeg(PhotoTestData.NextSeed()))).PhotoId;
        var photoC = (await host.UploadAsync(PhotoTestData.Jpeg(PhotoTestData.NextSeed()))).PhotoId;

        var created = await CreateBookAsync(
            host, author, contributors: [first, second], photos: [photoA, photoB], cover: photoA);

        ExistingBook updated;
        await using (var scope = host.Host.Scope())
        {
            var service = scope.ServiceProvider.GetRequiredService<BookService>();
            updated = (await service.UpdateAsync(created.BookId, new UpdateBook
            {
                Version = created.Version,
                Title = created.Title,
                Format = created.Format,
                Status = created.Status,
                MainAuthorContactId = author,
                Contributors =
                [
                    new BookContributorRequest { ContactId = second, Role = "Translator" },
                    new BookContributorRequest { ContactId = third },
                    new BookContributorRequest { ContactId = first, Role = "Editor" },
                ],
                PhotoIds = [photoB, photoC],
                CoverPhotoId = photoB,
            }, Caller))!;
        }

        Assert.Equal(created.Version + 1, updated.Version);
        Assert.Equal([second, third, first], updated.Contributors.OrderBy(c => c.Position).Select(c => c.Contact.ContactId));
        Assert.Equal(photoB, updated.CoverPhotoId);

        await using (var scope = host.Host.Scope())
        {
            var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            var images = await context.BookImages.Where(i => i.BookId == created.BookId).ToListAsync();
            Assert.Equal(new[] { photoB, photoC }.Order(), images.Select(i => i.PhotoId).Order());
            Assert.Equal(photoB, Assert.Single(images, i => i.IsCover).PhotoId);

            // The surviving contributor rows kept their ids: an update, not a delete and re-insert.
            var rows = await context.BookContributors.Where(c => c.BookId == created.BookId).ToListAsync();
            Assert.Equal(3, rows.Count);
            Assert.Equal(
                created.Contributors.Single(c => c.Contact.ContactId == first).BookContributorId,
                rows.Single(r => r.ContactId == first).BookContributorId);
        }
    }

    [SkippableFact]
    public async Task Deleting_a_library_photo_cascades_its_book_image_and_keeps_the_book()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await using var host = await CreateHostAsync();

        var author = await SeedContactAsync(host, "Primary Author");
        var photo = (await host.UploadAsync(PhotoTestData.Jpeg(PhotoTestData.NextSeed()))).PhotoId;
        var book = await CreateBookAsync(host, author, photos: [photo], cover: photo);

        await using (var scope = host.Host.Scope())
        {
            await scope.ServiceProvider.GetRequiredService<PhotoService>().Delete(photo);
        }

        await using (var scope = host.Host.Scope())
        {
            var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            Assert.True(await context.Books.AnyAsync(b => b.BookId == book.BookId));
            Assert.False(await context.BookImages.AnyAsync(i => i.BookId == book.BookId));
        }
    }

    [SkippableFact]
    public async Task Deleting_a_book_cascades_every_child_and_leaves_the_linked_records()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await using var host = await CreateHostAsync();

        var author = await SeedContactAsync(host, "Primary Author");
        var contributor = await SeedContactAsync(host, "Second Hand");
        var photo = (await host.UploadAsync(PhotoTestData.Jpeg(PhotoTestData.NextSeed()))).PhotoId;
        var book = await CreateBookAsync(host, author, contributors: [contributor], photos: [photo], cover: photo);

        await using (var scope = host.Host.Scope())
        {
            Assert.True(await scope.ServiceProvider.GetRequiredService<BookService>().DeleteAsync(book.BookId, User));
        }

        await using (var scope = host.Host.Scope())
        {
            var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            Assert.False(await context.BookContributors.AnyAsync(c => c.BookId == book.BookId));
            Assert.False(await context.BookImages.AnyAsync(i => i.BookId == book.BookId));
            Assert.True(await context.Photos.AnyAsync(p => p.PhotoId == photo));
            Assert.True(await context.Contacts.AnyAsync(c => c.ContactId == contributor));
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private Task<PhotoStoreHost> CreateHostAsync() => PhotoStoreHost.CreateAsync(
        fixture.OdysseyConnectionString,
        configureServices: services =>
        {
            services.AddScoped<IPhotoLookup, PhotoLookup>();
            services.AddScoped<BookService>();
        });

    private static async Task<Guid> SeedContactAsync(PhotoStoreHost host, string name)
    {
        var id = Guid.NewGuid();
        await using var scope = host.Host.Scope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        context.Contacts.Add(new Contact
        {
            ContactId = id,
            ExternalUid = $"urn:uuid:{Guid.NewGuid()}",
            OrganizationDetails = new() { LegalName = name },
            NormalizedName = name.ToUpperInvariant(),
            Type = Odyssey.Dtos.ContactType.Organization,
        });
        await context.SaveChangesAsync();
        return id;
    }

    private static async Task<ExistingBook> CreateBookAsync(
        PhotoStoreHost host, Guid author, List<Guid>? contributors = null, List<Guid>? photos = null, Guid? cover = null)
    {
        await using var scope = host.Host.Scope();
        return await scope.ServiceProvider.GetRequiredService<BookService>().CreateAsync(new NewBook
        {
            Title = $"Book {Guid.NewGuid():N}",
            Format = BookFormat.Hardcover,
            Status = BookStatus.Owned,
            MainAuthorContactId = author,
            Contributors = contributors?.Select(c => new BookContributorRequest { ContactId = c }).ToList(),
            PhotoIds = photos,
            CoverPhotoId = cover,
        }, Caller);
    }

    private static async Task AssertContactExistsAsync(PhotoStoreHost host, Guid id)
    {
        await using var scope = host.Host.Scope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        Assert.True(await context.Contacts.AnyAsync(c => c.ContactId == id));
    }
}
