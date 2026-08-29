using System.Net;
using System.Net.Http.Json;
using Odyssey.Dtos.Authorization;
using Odyssey.Context;
using Odyssey.Dtos.Journal;
using Odyssey.Dtos;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;
using Odyssey.Api.Tests.Infrastructure;

namespace Odyssey.Api.Tests;

public class JournalEntriesApiTests
{
    private const string ActorUserId = "journal-entries-actor-id";
    private const string Path = "/api/journal-entries";

    private static readonly string[] ReadOnly = [PermissionClaims.JournalRead];

    private static readonly string[] ReadWrite =
    [
        PermissionClaims.JournalRead, PermissionClaims.JournalCreate,
        PermissionClaims.JournalUpdate, PermissionClaims.JournalDelete,
    ];

    private static readonly string[] ReadWriteWithFiles =
    [
        PermissionClaims.JournalRead, PermissionClaims.JournalCreate,
        PermissionClaims.JournalUpdate, PermissionClaims.JournalDelete, PermissionClaims.FilesRead,
    ];

    // ── Authorization matrix (criterion #8) ───────────────────────────────────

    [Fact]
    public async Task List_Unauthenticated_ReturnsUnauthorized()
    {
        await using var factory = new ApiFactory(permissions: null);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(Path);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task List_Guest_WithoutJournalClaims_ReturnsForbidden()
    {
        // A Guest holds none of the Journal claims — the whole module is 403 for them.
        await using var factory = new ApiFactory(permissions: []);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(Path);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Mutations_WithReadOnlyPermission_ReturnForbidden()
    {
        await using var factory = new ApiFactory(ReadOnly);
        using var client = factory.CreateClient();

        var post = await client.PostAsJsonAsync(Path, NewEntry());
        Assert.Equal(HttpStatusCode.Forbidden, post.StatusCode);

        var delete = await client.DeleteAsync($"{Path}/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.Forbidden, delete.StatusCode);
    }

    // ── CRUD happy path (criterion #1) ─────────────────────────────────────────

    [Fact]
    public async Task Post_Get_Update_Archive_Unarchive_Delete_RoundTrip()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var create = NewEntry(title: "First light", location: "Oslo");
        var post = await client.PostAsJsonAsync(Path, create);
        Assert.Equal(HttpStatusCode.Created, post.StatusCode);
        var created = await post.Content.ReadFromJsonAsync<ExistingJournalEntry>();
        Assert.Equal("First light", created!.Title);
        Assert.Equal("Oslo", created.Location);
        Assert.Equal(ActorUserId, created.CreatedByUserId);
        Assert.Null(created.Archived);

        // Read back.
        var fetched = await client.GetFromJsonAsync<ExistingJournalEntry>($"{Path}/{created.JournalEntryId}");
        Assert.Equal("First light", fetched!.Title);
        Assert.Equal(create.Content, fetched.Content);

        // List reflects it.
        var list = await client.GetPagedItemsAsync<JournalEntrySummary>(Path);
        Assert.Contains(list!, e => e.JournalEntryId == created.JournalEntryId);

        // Update.
        var update = UpdateEntry(title: "First light (edited)", location: "Bergen");
        var put = await client.PutAsJsonAsync($"{Path}/{created.JournalEntryId}", update);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var updated = await put.Content.ReadFromJsonAsync<ExistingJournalEntry>();
        Assert.Equal("First light (edited)", updated!.Title);
        Assert.Equal("Bergen", updated.Location);
        Assert.Equal(ActorUserId, updated.UpdatedByUserId);

        // Archive via PUT (Archived = true) → still readable, Archived set.
        var archive = await client.PutAsJsonAsync($"{Path}/{created.JournalEntryId}",
            UpdateEntry(title: "First light (edited)", location: "Bergen", archived: true));
        Assert.Equal(HttpStatusCode.OK, archive.StatusCode);
        var archived = await client.GetFromJsonAsync<ExistingJournalEntry>($"{Path}/{created.JournalEntryId}");
        Assert.NotNull(archived!.Archived);

        // Default list (active only) excludes it; the archived-status filter surfaces it.
        var activeList = await client.GetPagedItemsAsync<JournalEntrySummary>(Path);
        Assert.DoesNotContain(activeList!, e => e.JournalEntryId == created.JournalEntryId);
        var archivedList = await client.GetPagedItemsAsync<JournalEntrySummary>($"{Path}?status=Archived");
        Assert.Contains(archivedList!, e => e.JournalEntryId == created.JournalEntryId);

        // Restore via PUT (Archived = false).
        var unarchive = await client.PutAsJsonAsync($"{Path}/{created.JournalEntryId}",
            UpdateEntry(title: "First light (edited)", location: "Bergen", archived: false));
        Assert.Equal(HttpStatusCode.OK, unarchive.StatusCode);
        var restored = await client.GetFromJsonAsync<ExistingJournalEntry>($"{Path}/{created.JournalEntryId}");
        Assert.Null(restored!.Archived);

        // Delete (hard).
        var delete = await client.DeleteAsync($"{Path}/{created.JournalEntryId}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        var afterDelete = await client.GetAsync($"{Path}/{created.JournalEntryId}");
        Assert.Equal(HttpStatusCode.NotFound, afterDelete.StatusCode);
    }

    [Fact]
    public async Task Get_UnknownEntry_ReturnsNotFound()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"{Path}/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Post_EntryDateOutsideWindow_ReturnsBadRequest()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var request = NewEntry(entryDate: new DateTime(3000, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var post = await client.PostAsJsonAsync(Path, request);

        Assert.Equal(HttpStatusCode.BadRequest, post.StatusCode);
    }

    // ── Tags (criterion #3 filter) & validation ────────────────────────────────

    [Fact]
    public async Task Post_UnknownTag_ReturnsUnprocessableEntity()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var request = NewEntry(tagIds: [Guid.NewGuid()]);
        var post = await client.PostAsJsonAsync(Path, request);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, post.StatusCode);
    }

    [Fact]
    public async Task Post_ArchivedTag_ReturnsUnprocessableEntity()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var tagId = await SeedJournalTagAsync(factory, "OldTag", archived: true);
        using var client = factory.CreateClient();

        var request = NewEntry(tagIds: [tagId]);
        var post = await client.PostAsJsonAsync(Path, request);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, post.StatusCode);
    }

    [Fact]
    public async Task List_FilteredByTag_ReturnsMatchingSubset()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var tagId = await SeedJournalTagAsync(factory, "Travel");
        using var client = factory.CreateClient();

        var tagged = await CreateAsync(client, NewEntry(title: "Trip", tagIds: [tagId]));
        var untagged = await CreateAsync(client, NewEntry(title: "Home"));

        var results = await client.GetPagedItemsAsync<JournalEntrySummary>($"{Path}?tagIds={tagId}");
        Assert.Contains(results!, e => e.JournalEntryId == tagged);
        Assert.DoesNotContain(results!, e => e.JournalEntryId == untagged);
    }

    // ── Contact link is ids-only (criterion #4) ───────────────────────────

    [Fact]
    public async Task Post_WithContact_ReturnsIdsOnly_NoNestedFields()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var contactId = await SeedContactAsync(factory, "Acme Trading Co");
        using var client = factory.CreateClient();

        var id = await CreateAsync(client, NewEntry(contactIds: [contactId]));

        var fetched = await client.GetFromJsonAsync<ExistingJournalEntry>($"{Path}/{id}");
        Assert.Contains(contactId, fetched!.ContactIds);

        // The read DTO exposes only the id; the contact name must not leak into journal.read.
        var json = await client.GetStringAsync($"{Path}/{id}");
        Assert.DoesNotContain("Acme Trading Co", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Post_UnknownContact_ReturnsUnprocessableEntity()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var request = NewEntry(contactIds: [Guid.NewGuid()]);
        var post = await client.PostAsJsonAsync(Path, request);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, post.StatusCode);
    }

    // ── Photos & attachments (criterion #5) ────────────────────────────────────

    [Fact]
    public async Task Post_ImageAsPhoto_AppearsInGallery()
    {
        await using var factory = new ApiFactory(ReadWriteWithFiles);
        var imageId = await SeedFileAsync(factory, "beach.jpg", "image/jpeg");
        using var client = factory.CreateClient();

        var id = await CreateAsync(client, NewEntry(photoFileIds: [imageId]));

        var fetched = await client.GetFromJsonAsync<ExistingJournalEntry>($"{Path}/{id}");
        var photo = Assert.Single(fetched!.Photos);
        Assert.Equal(imageId, photo.FileId);
        Assert.Equal(0, photo.Position);
    }

    [Fact]
    public async Task Post_NonImageAsPhoto_ReturnsUnprocessableEntity()
    {
        await using var factory = new ApiFactory(ReadWriteWithFiles);
        var pdfId = await SeedFileAsync(factory, "notes.pdf", "application/pdf");
        using var client = factory.CreateClient();

        var request = NewEntry(photoFileIds: [pdfId]);
        var post = await client.PostAsJsonAsync(Path, request);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, post.StatusCode);
    }

    [Fact]
    public async Task Post_PdfAsAttachment_IsLinkedByIdOnly()
    {
        await using var factory = new ApiFactory(ReadWriteWithFiles);
        var pdfId = await SeedFileAsync(factory, "receipt.pdf", "application/pdf");
        using var client = factory.CreateClient();

        var id = await CreateAsync(client, NewEntry(attachmentFileIds: [pdfId]));

        var fetched = await client.GetFromJsonAsync<ExistingJournalEntry>($"{Path}/{id}");
        var attachment = Assert.Single(fetched!.Attachments);
        Assert.Equal(pdfId, attachment.FileId);
    }

    [Fact]
    public async Task Post_UnknownAttachment_ReturnsUnprocessableEntity()
    {
        await using var factory = new ApiFactory(ReadWriteWithFiles);
        using var client = factory.CreateClient();

        var request = NewEntry(attachmentFileIds: [Guid.NewGuid()]);
        var post = await client.PostAsJsonAsync(Path, request);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, post.StatusCode);
    }

    [Fact]
    public async Task Post_LinkingPhoto_WithoutFilesReadClaim_ReturnsForbidden()
    {
        // Confused-deputy guard (criterion #5): journal.create alone cannot link a file.
        await using var factory = new ApiFactory(ReadWrite);
        var imageId = await SeedFileAsync(factory, "beach.jpg", "image/jpeg");
        using var client = factory.CreateClient();

        var post = await client.PostAsJsonAsync(Path, NewEntry(photoFileIds: [imageId]));

        Assert.Equal(HttpStatusCode.Forbidden, post.StatusCode);
    }

    [Fact]
    public async Task Post_LinkingAttachment_WithoutFilesReadClaim_ReturnsForbidden()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var pdfId = await SeedFileAsync(factory, "receipt.pdf", "application/pdf");
        using var client = factory.CreateClient();

        var post = await client.PostAsJsonAsync(Path, NewEntry(attachmentFileIds: [pdfId]));

        Assert.Equal(HttpStatusCode.Forbidden, post.StatusCode);
    }

    // ── Mass-assignment guard (criterion #6) ───────────────────────────────────

    [Fact]
    public async Task Post_WithNestedForeignObjects_DoesNotCreateContactsTagsOrFiles()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        // A body carrying populated nested "contacts"/"tags"/"photos" objects (beyond the scalar
        // id arrays) must be ignored: no contact, tag, or file row is created.
        var body = new
        {
            title = "Sneaky entry",
            content = "content",
            entryDate = "2026-06-01T00:00:00Z",
            tagIds = Array.Empty<Guid>(),
            contactIds = Array.Empty<Guid>(),
            photoFileIds = Array.Empty<Guid>(),
            attachmentFileIds = Array.Empty<Guid>(),
            contacts = new[] { new { contactId = Guid.NewGuid(), name = "INJECTED", normalizedName = "injected" } },
            tags = new[] { new { journalTagId = Guid.NewGuid(), name = "INJECTED", normalizedName = "injected" } },
            photos = new[] { new { fileId = Guid.NewGuid(), position = 0 } },
        };

        var post = await client.PostAsJsonAsync(Path, body);
        Assert.Equal(HttpStatusCode.Created, post.StatusCode);

        using var scope = factory.Services.CreateScope();
        var journal = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        Assert.False(await journal.Contacts.AnyAsync());
        Assert.False(await journal.JournalTags.AnyAsync());
        Assert.False(await journal.JournalEntryPhotos.AnyAsync());
        Assert.False(await journal.JournalEntryTags.AnyAsync());
        Assert.False(await journal.JournalEntryContacts.AnyAsync());
    }

    // ── Search / filter / sort / pagination (criterion #7) ─────────────────────

    [Fact]
    public async Task List_Search_MatchesTitleContentAndLocation()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var byTitle = await CreateAsync(client, NewEntry(title: "Zebra sighting"));
        var byLocation = await CreateAsync(client, NewEntry(title: "Ordinary day", location: "Zebra Street"));
        var unrelated = await CreateAsync(client, NewEntry(title: "Nothing here"));

        var results = await client.GetPagedItemsAsync<JournalEntrySummary>($"{Path}?search=zebra");
        Assert.Contains(results!, e => e.JournalEntryId == byTitle);
        Assert.Contains(results!, e => e.JournalEntryId == byLocation);
        Assert.DoesNotContain(results!, e => e.JournalEntryId == unrelated);
    }

    // The summary Snippet previews Content, truncated to 200 chars. Entry Content is required (never
    // null), so this covers the short (≤ 200, returned whole) and long (> 200, truncated) branches.
    [Fact]
    public async Task List_Summary_Snippet_TruncatesContentToMax()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var shortContent = "A short note.";
        var longContent = new string('x', 250);

        var shortId = await CreateAsync(client, NewEntry(title: "Short", content: shortContent));
        var longId = await CreateAsync(client, NewEntry(title: "Long", content: longContent));

        var results = await client.GetPagedItemsAsync<JournalEntrySummary>(Path);

        Assert.Equal(shortContent, results!.Single(e => e.JournalEntryId == shortId).Snippet);

        var longSnippet = results!.Single(e => e.JournalEntryId == longId).Snippet;
        Assert.Equal(200, longSnippet.Length);
        Assert.Equal(longContent[..200], longSnippet);
    }

    // Author-name attribution (#316): the controller resolves CreatedByUserId → the profile's display
    // name via the claim-aware resolver, on both the list and the detail read. A journal reader without
    // users.read still gets the display name (it is not the email), demonstrating the minimisation fix.
    [Fact]
    public async Task Author_DisplayName_Resolves_When_The_User_Exists()
    {
        await using var factory = new ApiFactory(ReadWrite);
        await factory.SeedActorUserAsync(displayName: "Ada L.");
        using var client = factory.CreateClient();

        var id = await CreateAsync(client, NewEntry(title: "Mine"));

        var results = await client.GetPagedItemsAsync<JournalEntrySummary>(Path);
        Assert.Equal("Ada L.", results!.Single(e => e.JournalEntryId == id).CreatedByName);

        var detail = await client.GetFromJsonAsync<ExistingJournalEntry>($"{Path}/{id}");
        Assert.Equal("Ada L.", detail!.CreatedByName);
    }

    // A journal reader (no users.read) whose author has no profile name gets "Unknown user", NEVER the
    // author's email (#315 data-minimisation fix, #316 §9).
    [Fact]
    public async Task Author_NoProfileName_And_NoUsersRead_ReturnsUnknownUser_NotEmail()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var userName = await factory.SeedActorUserAsync();
        using var client = factory.CreateClient();

        var id = await CreateAsync(client, NewEntry(title: "Mine"));

        var results = await client.GetPagedItemsAsync<JournalEntrySummary>(Path);
        var name = results!.Single(e => e.JournalEntryId == id).CreatedByName;
        Assert.Equal("Unknown user", name);
        Assert.NotEqual(userName, name);
    }

    // When the author id resolves to no user, the name degrades to "Unknown user" (never null → the
    // client no longer falls back to a raw GUID, closing #314).
    [Fact]
    public async Task Author_DisplayName_IsUnknownUser_When_The_User_Is_Unresolvable()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var id = await CreateAsync(client, NewEntry(title: "Orphan"));

        var results = await client.GetPagedItemsAsync<JournalEntrySummary>(Path);
        Assert.Equal("Unknown user", results!.Single(e => e.JournalEntryId == id).CreatedByName);
    }

    [Fact]
    public async Task List_DefaultSort_IsEntryDateDescending()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var older = await CreateAsync(client, NewEntry(title: "Older", entryDate: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
        var newer = await CreateAsync(client, NewEntry(title: "Newer", entryDate: new DateTime(2026, 12, 1, 0, 0, 0, DateTimeKind.Utc)));

        var results = await client.GetPagedItemsAsync<JournalEntrySummary>(Path);
        var newerIndex = results!.FindIndex(e => e.JournalEntryId == newer);
        var olderIndex = results!.FindIndex(e => e.JournalEntryId == older);
        Assert.True(newerIndex < olderIndex, "newer entry should sort before older by EntryDate DESC");
    }

    [Fact]
    public async Task List_DateRangeFilter_NarrowsByEntryDate()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var january = await CreateAsync(client, NewEntry(title: "Jan", entryDate: new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc)));
        var june = await CreateAsync(client, NewEntry(title: "Jun", entryDate: new DateTime(2026, 6, 15, 0, 0, 0, DateTimeKind.Utc)));

        var results = await client.GetPagedItemsAsync<JournalEntrySummary>(
            $"{Path}?from=2026-05-01T00:00:00Z&to=2026-07-01T00:00:00Z");
        Assert.Contains(results!, e => e.JournalEntryId == june);
        Assert.DoesNotContain(results!, e => e.JournalEntryId == january);
    }

    [Fact]
    public async Task List_Pagination_LimitAndOffset_WindowResults()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        for (var i = 0; i < 5; i++)
        {
            await CreateAsync(client, NewEntry(title: $"Entry {i}"));
        }

        var page = await client.GetFromJsonAsync<Odyssey.Dtos.PagedResult<JournalEntrySummary>>($"{Path}?limit=2&offset=0");
        Assert.Equal(5, page!.TotalCount);
        Assert.Equal(2, page.Items.Count);
    }

    [Fact]
    public async Task List_LimitAboveMaximum_ReturnsBadRequest()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"{Path}?limit=100000");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task List_NegativeOffset_ReturnsBadRequest()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"{Path}?offset=-1");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task List_UnknownSortKey_ReturnsBadRequest()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"{Path}?sortBy=NotAKey");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ── Soft-reference resilience (criterion #12) ──────────────────────────────

    [Fact]
    public async Task Get_AfterLinkedContactDeleted_StillReadsWithDanglingId()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var contactId = await SeedContactAsync(factory, "Since-deleted Ltd");
        using var client = factory.CreateClient();

        var id = await CreateAsync(client, NewEntry(contactIds: [contactId]));

        // Delete the contact out from under the soft reference.
        using (var scope = factory.Services.CreateScope())
        {
            var journal = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            var cp = await journal.Contacts.FirstAsync(c => c.ContactId == contactId);
            journal.Contacts.Remove(cp);
            await journal.SaveChangesAsync();
        }

        // The entry still reads (no FK, tolerated) and returns the now-dangling id.
        var fetched = await client.GetFromJsonAsync<ExistingJournalEntry>($"{Path}/{id}");
        Assert.NotNull(fetched);
        Assert.Contains(contactId, fetched!.ContactIds);
    }

    [Fact]
    public async Task Get_AfterLinkedPhotoFileDeleted_StillReadsWithDanglingId()
    {
        await using var factory = new ApiFactory(ReadWriteWithFiles);
        var imageId = await SeedFileAsync(factory, "beach.jpg", "image/jpeg");
        using var client = factory.CreateClient();

        var id = await CreateAsync(client, NewEntry(photoFileIds: [imageId]));

        using (var scope = factory.Services.CreateScope())
        {
            var finance = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            var file = await finance.FileMetadata.FirstAsync(f => f.Id == imageId);
            finance.FileMetadata.Remove(file);
            await finance.SaveChangesAsync();
        }

        var fetched = await client.GetFromJsonAsync<ExistingJournalEntry>($"{Path}/{id}");
        Assert.NotNull(fetched);
        Assert.Contains(fetched!.Photos, p => p.FileId == imageId);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    // ── PUT link diffing (criterion #1) ───────────────────────────────────────

    [Fact]
    public async Task Put_DiffsLinkSets_AddsRemovesAndKeepsStableRows()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var tagA = await SeedJournalTagAsync(factory, "A");
        var tagB = await SeedJournalTagAsync(factory, "B");
        var tagC = await SeedJournalTagAsync(factory, "C");
        var cpA = await SeedContactAsync(factory, "CpA");
        var cpB = await SeedContactAsync(factory, "CpB");

        var id = await CreateAsync(client, NewEntry(tagIds: [tagA, tagB], contactIds: [cpA]));

        var put = await client.PutAsJsonAsync($"{Path}/{id}", new UpdateJournalEntry
        {
            Title = "Revised",
            Content = "Dear journal, today was a day.",
            EntryDate = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            TagIds = [tagB, tagC],           // removes A, keeps B, adds C
            ContactIds = [cpB],         // removes cpA, adds cpB
        });
        put.EnsureSuccessStatusCode();

        var entry = (await client.GetFromJsonAsync<ExistingJournalEntry>($"{Path}/{id}"))!;
        Assert.Equal(new[] { tagB, tagC }.OrderBy(x => x), entry.TagIds.OrderBy(x => x));
        Assert.Equal(new[] { cpB }, entry.ContactIds);
        Assert.Equal("Revised", entry.Title);
    }

    [Fact]
    public async Task Put_WithUnknownTag_ReturnsUnprocessableEntity()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var id = await CreateAsync(client, NewEntry());

        var put = await client.PutAsJsonAsync($"{Path}/{id}", new UpdateJournalEntry
        {
            Title = "Revised",
            Content = "Dear journal, today was a day.",
            EntryDate = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            TagIds = [Guid.NewGuid()],
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, put.StatusCode);
    }

    [Fact]
    public async Task Put_LinkingFile_WithoutFilesRead_ReturnsForbidden()
    {
        await using var factory = new ApiFactory(ReadWrite); // no files.read
        using var client = factory.CreateClient();

        var id = await CreateAsync(client, NewEntry());

        var put = await client.PutAsJsonAsync($"{Path}/{id}", new UpdateJournalEntry
        {
            Title = "Revised",
            Content = "Dear journal, today was a day.",
            EntryDate = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            AttachmentFileIds = [Guid.NewGuid()],
        });

        Assert.Equal(HttpStatusCode.Forbidden, put.StatusCode);
    }

    // ── Pagination window (criterion #7) ──────────────────────────────────────

    [Fact]
    public async Task List_WithNonZeroOffset_ReturnsRequestedWindow()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        // Default sort is EntryDate descending, so newest first.
        await CreateAsync(client, NewEntry(title: "Newest", entryDate: new DateTime(2026, 6, 3, 0, 0, 0, DateTimeKind.Utc)));
        await CreateAsync(client, NewEntry(title: "Middle", entryDate: new DateTime(2026, 6, 2, 0, 0, 0, DateTimeKind.Utc)));
        await CreateAsync(client, NewEntry(title: "Oldest", entryDate: new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc)));

        var page = (await client.GetFromJsonAsync<PagedResult<JournalEntrySummary>>($"{Path}?offset=1&limit=1"))!;

        Assert.Equal(3, page.TotalCount);
        Assert.Equal(1, page.Offset);
        Assert.Single(page.Items);
        Assert.Equal("Middle", page.Items[0].Title);
    }

    /// <summary>
    /// The journal link cap is no longer a <c>private const</c> — it is an admin setting (issue #421
    /// Wave 3). Two tag links are far under the shipped 50, so a rejection here can only come from the
    /// seeded row, which is what proves a lowered cap binds without a restart.
    /// </summary>
    [Fact]
    public async Task Post_OverALoweredTagLinkCap_ReturnsUnprocessableEntity()
    {
        await using var factory = new ApiFactory(ReadWrite);
        await SystemSettingsSeed.SetAsync(
            factory.Services, SystemSettingsKeys.JournalEntryMaxLinksPerKind, "1");
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(Path,
            NewEntry(tagIds: [Guid.NewGuid(), Guid.NewGuid()]));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    private static NewJournalEntry NewEntry(
        string title = "A day",
        DateTime? entryDate = null,
        string? location = null,
        string? content = null,
        Guid[]? tagIds = null,
        Guid[]? contactIds = null,
        Guid[]? photoFileIds = null,
        Guid[]? attachmentFileIds = null) => new()
    {
        Title = title,
        Content = content ?? "Dear journal, today was a day.",
        EntryDate = entryDate ?? new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        Location = location,
        TagIds = tagIds ?? [],
        ContactIds = contactIds ?? [],
        PhotoFileIds = photoFileIds ?? [],
        AttachmentFileIds = attachmentFileIds ?? [],
    };

    private static UpdateJournalEntry UpdateEntry(string title, string? location = null, bool archived = false) => new()
    {
        Title = title,
        Content = "Dear journal, today was a day.",
        EntryDate = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        Location = location,
        Archived = archived,
    };

    private static async Task<Guid> CreateAsync(HttpClient client, NewJournalEntry request)
    {
        var post = await client.PostAsJsonAsync(Path, request);
        post.EnsureSuccessStatusCode();
        var created = await post.Content.ReadFromJsonAsync<ExistingJournalEntry>();
        return created!.JournalEntryId;
    }

    private static async Task<Guid> SeedJournalTagAsync(
        WebApplicationFactory<Program> factory, string name = "Tag", bool archived = false)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        await context.Database.EnsureCreatedAsync();

        var id = Guid.NewGuid();
        context.JournalTags.Add(new JournalTag
        {
            JournalTagId = id,
            Name = name,
            Archived = archived ? DateTime.UtcNow : null,
        });
        await context.SaveChangesAsync();
        return id;
    }

    private static async Task<Guid> SeedContactAsync(WebApplicationFactory<Program> factory, string name = "Acme")
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        await context.Database.EnsureCreatedAsync();

        var id = Guid.NewGuid();
        context.Contacts.Add(new Contact
        {
            ContactId = id,
            ExternalUid = $"urn:uuid:{Guid.NewGuid()}",
            NormalizedName = name.Trim().ToLowerInvariant(),
            Type = Odyssey.Dtos.ContactType.Organization,
            OrganizationDetails = new() { LegalName = name },
        });
        await context.SaveChangesAsync();
        return id;
    }

    private static async Task<Guid> SeedFileAsync(WebApplicationFactory<Program> factory, string fileName, string contentType)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        await context.Database.EnsureCreatedAsync();

        var blob = new FileBlob { Id = Guid.NewGuid(), Content = [1, 2, 3] };
        var metadataId = Guid.NewGuid();
        context.FileBlob.Add(blob);
        context.FileMetadata.Add(new FileMetadata
        {
            Id = metadataId,
            UploadedByUserId = ActorUserId,
            FileName = fileName,
            ContentType = contentType,
            SizeBytes = 3,
            Sha256Hash = Guid.NewGuid().ToString("N"),
            UploadedAtUtc = DateTime.UtcNow,
            FileBlobId = blob.Id,
            FileBlob = blob,
        });
        await context.SaveChangesAsync();
        return metadataId;
    }

    private sealed class ApiFactory : OdysseyApiFactory
    {
        public ApiFactory(IReadOnlyCollection<string>? permissions)
            : base(permissions, ActorUserId, configureServices: IsolateDomainContext)
        {
        }

        // A second isolation on top of the base factory's, giving this test class its own in-memory
        // database rather than sharing the factory-wide one.
        private static void IsolateDomainContext(IServiceCollection services)
        {
            var databaseName = $"domain-{Guid.NewGuid()}";
            services.RemoveAll<DbContextOptions<OdysseyContext>>();
            services.AddDbContext<OdysseyContext>(options => options.UseInMemoryDatabase(databaseName));
        }
    }
}
