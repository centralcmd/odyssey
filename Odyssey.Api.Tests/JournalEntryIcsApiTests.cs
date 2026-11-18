using Odyssey.Context;
using Odyssey.Dtos;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Odyssey.Dtos.Authorization;
using Odyssey.Core.Journal;
using Odyssey.Dtos.Journal;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Testing.FileStorage;

namespace Odyssey.Api.Tests;

/// <summary>VJOURNAL/.ics journal-entry import/export endpoints (issue #339).</summary>
public class JournalEntryIcsApiTests
{
    private const string ActorUserId = "journal-entry-ics-actor-id";
    private const string Path = "/api/journal-entries";
    private const string IcsPath = "/api/journal-entries/vjournal";

    // The shipped counts are unlimited (entries carry their files and photos inline), so the cap tests pin an
    // administrator's finite value instead.
    private const int AdminCountCap = 5;

    private static readonly string[] ReadOnly = [PermissionClaims.JournalRead];

    private static readonly string[] ReadWrite =
    [
        PermissionClaims.JournalRead, PermissionClaims.JournalCreate, PermissionClaims.JournalUpdate,
    ];

    // Photo links need photos.read since issue #428 §3.3; attachments still need files.read.
    private static readonly string[] ReadWriteWithFiles =
    [
        PermissionClaims.JournalRead, PermissionClaims.JournalCreate,
        PermissionClaims.JournalUpdate, PermissionClaims.FilesRead, PermissionClaims.PhotosRead,
    ];

    // Importing inline content needs the create claims too: files.read + files.create, photos.read + photos.create.
    private static readonly string[] ReadWriteWithUploads =
    [
        PermissionClaims.JournalRead, PermissionClaims.JournalCreate, PermissionClaims.JournalUpdate,
        PermissionClaims.FilesRead, PermissionClaims.FilesCreate, PermissionClaims.PhotosRead, PermissionClaims.PhotosCreate,
    ];

    private static readonly byte[] NoteBytes = Encoding.UTF8.GetBytes("Packing list: tent, stove, map.");

    private static readonly string[] ReadWriteWithContacts =
    [
        PermissionClaims.JournalRead, PermissionClaims.JournalCreate,
        PermissionClaims.JournalUpdate, PermissionClaims.ContactsRead,
    ];

    private static readonly string[] ReadWithContacts =
    [
        PermissionClaims.JournalRead, PermissionClaims.ContactsRead,
    ];

    // ------------------------------------------------------------------ export

    [Fact]
    public async Task Export_EmptyBoard_ReturnsValidVCalendar_WithNoSniff()
    {
        await using var factory = new ApiFactory(ReadOnly);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(IcsPath);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/calendar", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("nosniff", response.Headers.TryGetValues("X-Content-Type-Options", out var v) ? string.Join("", v) : null);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("BEGIN:VCALENDAR", body);
        Assert.DoesNotContain("BEGIN:VJOURNAL", body);
    }

    [Fact]
    public async Task ExportAll_FileName_IsTimestampPrefixed()
    {
        await using var factory = new ApiFactory(ReadOnly);
        using var client = factory.CreateClient();

        var fileName = await FileNameOf(await client.GetAsync(IcsPath));
        Assert.Matches(@"^odyssey-journal-entries-\d{8}-\d{6}Z\.ics$", fileName!);
    }

    [Fact]
    public async Task ExportFiltered_FileName_HasFilteredSegment()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        await CreateAsync(client, NewEntry("A"));

        var fileName = await FileNameOf(await client.GetAsync($"{IcsPath}?search=A"));
        Assert.Matches(@"^odyssey-journal-entries-filtered-\d{8}-\d{6}Z\.ics$", fileName!);
    }

    // The attachment filter narrows the set like any other, so the filename has to say so. Caught in
    // review: the filter was added to the query model without extending HasAnyFilter, which would have
    // handed the user a narrowed export named as the full one.
    [Theory]
    [InlineData("hasPhotos=true")]
    [InlineData("hasFiles=true")]
    [InlineData("hasPhotos=false")]
    public async Task ExportFilteredByAttachmentPresence_FileName_HasFilteredSegment(string filter)
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        await CreateAsync(client, NewEntry("A"));

        var fileName = await FileNameOf(await client.GetAsync($"{IcsPath}?{filter}"));
        Assert.Matches(@"^odyssey-journal-entries-filtered-\d{8}-\d{6}Z\.ics$", fileName!);
    }

    [Fact]
    public async Task Export_WithoutReadClaim_ReturnsForbidden()
    {
        await using var factory = new ApiFactory([]);
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(IcsPath)).StatusCode);
    }

    [Fact]
    public async Task Export_Entry_EmitsVJournal_WithAllFields()
    {
        await using var factory = new ApiFactory(ReadWriteWithContacts.Concat([PermissionClaims.FilesRead, PermissionClaims.PhotosRead]).ToArray());
        using var client = factory.CreateClient();
        var tagId = await SeedJournalTagAsync(factory, "Travel");
        var cpUid = "urn:uuid:cp-acme";
        var cpId = await SeedContactAsync(factory, "Acme", cpUid);
        var imageId = await SeedPhotoAsync(factory, "beach.jpg");
        var pdfId = await SeedFileAsync(factory, "notes.pdf", "application/pdf");

        var created = await CreateAsync(client, NewEntry(
            title: "Hiking",
            content: "Great day, near; the fjord",
            location: "Oslo, Norway",
            entryDate: new DateTime(2026, 7, 30, 0, 0, 0, DateTimeKind.Utc),
            tagIds: [tagId],
            contactIds: [cpId],
            photoIds: [imageId],
            attachmentFileIds: [pdfId]));

        var body = await (await client.GetAsync(IcsPath)).Content.ReadAsStringAsync();

        Assert.Equal(1, Count(body, "BEGIN:VJOURNAL"));
        Assert.Contains($"UID:{created.ExternalUid}", body);
        Assert.Contains("SUMMARY:Hiking", body);
        Assert.Contains("STATUS:FINAL", body);
        Assert.Contains("DTSTART;VALUE=DATE:20260730", body);
        Assert.Contains("CATEGORIES:Travel", body);
        Assert.Contains("X-ODYSSEY-LOCATION:Oslo\\, Norway", body);
        Assert.Contains($"X-ODYSSEY-CONTACT:{cpUid}", body);
        // The photo and the file travel inline — bytes, name, type, id and hash — not as bare references.
        var unfolded = Unfold(body);
        Assert.DoesNotContain($"ATTACH:odyssey-photo:{imageId}", body);
        Assert.DoesNotContain($"ATTACH:odyssey-file:{pdfId}", body);
        Assert.Contains($";X-ODYSSEY-KIND=PHOTO;X-ODYSSEY-PHOTO-ID={imageId}", unfolded);
        Assert.Contains($"ATTACH;FMTTYPE=application/pdf;ENCODING=BASE64;VALUE=BINARY;FILENAME=\"notes.pdf\";SIZE=3;X-ODYSSEY-FILE-ID={pdfId}", unfolded);
    }

    // RFC 5545 unfolding: a CRLF followed by one space or tab is removed.
    private static string Unfold(string ics) => ics.Replace("\r\n ", string.Empty).Replace("\r\n\t", string.Empty);

    [Fact]
    public async Task ExportAll_IncludesArchivedByDefault_StatusFilters()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        var active = await CreateAsync(client, NewEntry("Active one"));
        var archived = await ArchiveAsync(client, await CreateAsync(client, NewEntry("Archived one")));

        var all = await (await client.GetAsync(IcsPath)).Content.ReadAsStringAsync();
        Assert.Contains(active.ExternalUid, all);
        Assert.Contains(archived.ExternalUid, all);
        Assert.Contains("STATUS:CANCELLED", all);

        var onlyActive = await (await client.GetAsync($"{IcsPath}?status=Active")).Content.ReadAsStringAsync();
        Assert.Contains(active.ExternalUid, onlyActive);
        Assert.DoesNotContain(archived.ExternalUid, onlyActive);

        var onlyArchived = await (await client.GetAsync($"{IcsPath}?status=Archived")).Content.ReadAsStringAsync();
        Assert.DoesNotContain(active.ExternalUid, onlyArchived);
        Assert.Contains(archived.ExternalUid, onlyArchived);
    }

    [Fact]
    public async Task ExportSingle_ReturnsOneVJournal()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        var keep = await CreateAsync(client, NewEntry("Keep"));
        await CreateAsync(client, NewEntry("Drop"));

        var response = await client.GetAsync($"{Path}/{keep.JournalEntryId}/vjournal");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(1, Count(body, "BEGIN:VJOURNAL"));
        Assert.Contains(keep.ExternalUid, body);
        var fileName = await FileNameOf(response);
        Assert.Matches(@"^odyssey-journal-entry-\d{8}-\d{6}Z\.ics$", fileName!);
    }

    [Fact]
    public async Task ExportSingle_NotFound_Returns404()
    {
        await using var factory = new ApiFactory(ReadOnly);
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"{Path}/{Guid.NewGuid()}/vjournal");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Export_WithoutContactsRead_OmitsContactProperty()
    {
        await using var factory = new ApiFactory(ReadWrite); // no contacts.read
        using var client = factory.CreateClient();
        var cpId = await SeedContactAsync(factory, "Acme", "urn:uuid:cp-1");
        await CreateAsync(client, NewEntry("Linked", contactIds: [cpId]));

        var body = await (await client.GetAsync(IcsPath)).Content.ReadAsStringAsync();
        Assert.DoesNotContain("X-ODYSSEY-CONTACT", body);
    }

    [Fact]
    public async Task ExportFiltered_BySearchAndTag_ReturnsMatchingSet()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        var tagId = await SeedJournalTagAsync(factory, "Work");
        var match = await CreateAsync(client, NewEntry("Quarterly review", tagIds: [tagId]));
        await CreateAsync(client, NewEntry("Grocery run"));

        var body = await (await client.GetAsync($"{IcsPath}?search=Quarterly&tagIds={tagId}")).Content.ReadAsStringAsync();
        Assert.Equal(1, Count(body, "BEGIN:VJOURNAL"));
        Assert.Contains(match.ExternalUid, body);
    }

    // The export binds the SAME query model as the list, which is what lets the page's "Export
    // filtered" describe exactly the set on screen. The attachment filter is the one that had no
    // server predicate, so it is the one worth pinning here.
    [Fact]
    public async Task ExportFiltered_ByAttachmentPresence_ReturnsMatchingSet()
    {
        await using var factory = new ApiFactory(ReadWriteWithContacts.Concat([PermissionClaims.FilesRead, PermissionClaims.PhotosRead]).ToArray());
        var imageId = await SeedPhotoAsync(factory, "beach.jpg");
        using var client = factory.CreateClient();
        var withPhoto = await CreateAsync(client, NewEntry("Trip", photoIds: [imageId]));
        await CreateAsync(client, NewEntry("Grocery run"));

        var body = await (await client.GetAsync($"{IcsPath}?hasPhotos=true")).Content.ReadAsStringAsync();

        Assert.Equal(1, Count(body, "BEGIN:VJOURNAL"));
        Assert.Contains(withPhoto.ExternalUid, body);
    }

    [Fact]
    public async Task ExportReservedCharacters_RoundTripUnchanged()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        await CreateAsync(client, NewEntry(
            title: "Trip",
            content: "Comma, semicolon; backslash\\ and\nnewline",
            location: "A, B; C\\D"));

        var exported = await (await client.GetAsync(IcsPath)).Content.ReadAsStringAsync();
        var result = await ImportAsync(client, exported);

        Assert.Equal(0, result.ImportedCount);
        Assert.Equal(1, result.UpdatedCount);
        using var scope = factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var row = await ctx.JournalEntries.SingleAsync();
        Assert.Equal("Comma, semicolon; backslash\\ and\nnewline", row.Content);
        Assert.Equal("A, B; C\\D", row.Location);
    }

    // ------------------------------------------------------------------ import

    [Fact]
    public async Task Import_HandAuthored_CreatesEntries_WithVerbatimUid()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var ics = Vcalendar(
            Vjournal("urn:uuid:e1", "SUMMARY:One", "DESCRIPTION:First", "DTSTART;VALUE=DATE:20260101"),
            Vjournal("external-2", "SUMMARY:Two", "DESCRIPTION:Second", "DTSTART;VALUE=DATE:20260102"));

        var result = await ImportAsync(client, ics);

        Assert.Equal(2, result.ImportedCount);
        Assert.Equal(0, result.UpdatedCount);
        Assert.Empty(result.Skipped);

        using var scope = factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        Assert.Contains(await ctx.JournalEntries.ToListAsync(), r => r.ExternalUid == "external-2");
    }

    [Fact]
    public async Task Import_ExportedFile_IsIdempotent_UpdatesInPlace()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        await CreateAsync(client, NewEntry("Alpha"));
        await CreateAsync(client, NewEntry("Beta"));

        var exported = await (await client.GetAsync(IcsPath)).Content.ReadAsStringAsync();
        var result = await ImportAsync(client, exported);

        Assert.Equal(0, result.ImportedCount);
        Assert.Equal(2, result.UpdatedCount);

        using var scope = factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        Assert.Equal(2, await ctx.JournalEntries.CountAsync()); // no duplicates
    }

    [Fact]
    public async Task Import_UpdateInPlace_PreservesCreatedBy_StampsUpdatedBy()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        var created = await CreateAsync(client, NewEntry("Original"));

        var ics = Vcalendar(Vjournal(created.ExternalUid, "SUMMARY:Renamed", "DESCRIPTION:Edited", "DTSTART;VALUE=DATE:20260601"));
        var result = await ImportAsync(client, ics);

        Assert.Equal(0, result.ImportedCount);
        Assert.Equal(1, result.UpdatedCount);
        using var scope = factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var row = await ctx.JournalEntries.SingleAsync();
        Assert.Equal("Renamed", row.Title);
        Assert.Equal(ActorUserId, row.CreatedByUserId);
        Assert.Equal(ActorUserId, row.UpdatedByUserId);
    }

    [Fact]
    public async Task Import_CancelledStatus_ArchivesOnCreateAndUpdate()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var created = await ImportAsync(client, Vcalendar(
            Vjournal("c1", "SUMMARY:Cancelled", "DESCRIPTION:x", "DTSTART;VALUE=DATE:20260101", "STATUS:CANCELLED")));
        Assert.Equal(1, created.ImportedCount);

        using (var scope = factory.Services.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            Assert.NotNull((await ctx.JournalEntries.SingleAsync(e => e.ExternalUid == "c1")).Archived);
        }
    }

    [Fact]
    public async Task Import_AbsentStatusOnUpdate_LeavesArchivedUnchanged()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        // Create archived, then re-import the same UID with NO STATUS: it must stay archived (§5, AC 17).
        var archived = await ImportAsync(client, Vcalendar(
            Vjournal("a1", "SUMMARY:Was archived", "DESCRIPTION:x", "DTSTART;VALUE=DATE:20260101", "STATUS:CANCELLED")));
        Assert.Equal(1, archived.ImportedCount);

        await ImportAsync(client, Vcalendar(
            Vjournal("a1", "SUMMARY:Re-imported", "DESCRIPTION:x", "DTSTART;VALUE=DATE:20260101")));

        using var scope = factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        Assert.NotNull((await ctx.JournalEntries.SingleAsync()).Archived); // NOT un-archived by absent STATUS
    }

    [Fact]
    public async Task Import_FinalStatusOnUpdate_UnarchivesEntry()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        await ImportAsync(client, Vcalendar(
            Vjournal("u1", "SUMMARY:Archived", "DESCRIPTION:x", "DTSTART;VALUE=DATE:20260101", "STATUS:CANCELLED")));

        await ImportAsync(client, Vcalendar(
            Vjournal("u1", "SUMMARY:Active again", "DESCRIPTION:x", "DTSTART;VALUE=DATE:20260101", "STATUS:FINAL")));

        using var scope = factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        Assert.Null((await ctx.JournalEntries.SingleAsync()).Archived);
    }

    [Fact]
    public async Task Import_MissingSummary_IsSkippedWithSample()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var result = await ImportAsync(client, Vcalendar(Vjournal("n1", "DESCRIPTION:x", "DTSTART;VALUE=DATE:20260101")));

        Assert.Equal(0, result.ImportedCount);
        var group = Assert.Single(result.Skipped);
        Assert.Contains("SUMMARY", group.Reason);
        Assert.Contains("(untitled)", group.SampleTitles);
    }

    [Fact]
    public async Task Import_MissingDescription_IsSkipped()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var result = await ImportAsync(client, Vcalendar(Vjournal("n2", "SUMMARY:No body", "DTSTART;VALUE=DATE:20260101")));

        Assert.Equal(0, result.ImportedCount);
        Assert.Contains("DESCRIPTION", Assert.Single(result.Skipped).Reason);
    }

    [Fact]
    public async Task Import_MissingDtstart_IsSkipped()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var result = await ImportAsync(client, Vcalendar(Vjournal("n3", "SUMMARY:No date", "DESCRIPTION:x")));

        Assert.Equal(0, result.ImportedCount);
        Assert.Equal("Entry date (DTSTART) is required.", Assert.Single(result.Skipped).Reason);
    }

    [Fact]
    public async Task Import_TitleTooLong_IsSkipped()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var ics = Vcalendar(Vjournal("l1", $"SUMMARY:{new string('x', 201)}", "DESCRIPTION:x", "DTSTART;VALUE=DATE:20260101"));
        var result = await ImportAsync(client, ics);

        Assert.Equal(0, result.ImportedCount);
        Assert.Contains("Title", Assert.Single(result.Skipped).Reason);
    }

    [Fact]
    public async Task Import_ControlCharUid_IsSkipped()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        // A UID with an embedded tab (a C0 control character) is rejected before any DB write.
        var ics = Vcalendar(Vjournal("bad\tuid", "SUMMARY:Sneaky", "DESCRIPTION:x", "DTSTART;VALUE=DATE:20260101"));
        var result = await ImportAsync(client, ics);

        Assert.Equal(0, result.ImportedCount);
        Assert.Contains("Invalid UID", Assert.Single(result.Skipped).Reason);
        using var scope = factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        Assert.Equal(0, await ctx.JournalEntries.CountAsync());
    }

    [Fact]
    public async Task Import_C1ControlCharUid_IsImported()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        // The import runs the create/update DTO's own pattern (issue #287 L8), which bounds the C0 range
        // and DEL only. A C1 character is therefore accepted here exactly as the API accepts it.
        const string uid = "c1\u0085uid";
        var ics = Vcalendar(Vjournal(uid, "SUMMARY:C1", "DESCRIPTION:x", "DTSTART;VALUE=DATE:20260101"));
        var result = await ImportAsync(client, ics);

        Assert.Equal(1, result.ImportedCount);
        Assert.Empty(result.Skipped);
        using var scope = factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        Assert.Equal(uid, (await ctx.JournalEntries.SingleAsync()).ExternalUid);
    }

    [Fact]
    public async Task Import_DuplicateUidWithinFile_LastWriteWins()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var ics = Vcalendar(
            Vjournal("dupe", "SUMMARY:First", "DESCRIPTION:x", "DTSTART;VALUE=DATE:20260101"),
            Vjournal("dupe", "SUMMARY:Second", "DESCRIPTION:y", "DTSTART;VALUE=DATE:20260101"));
        var result = await ImportAsync(client, ics);

        Assert.Equal(1, result.ImportedCount);
        using var scope = factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        Assert.Equal("Second", (await ctx.JournalEntries.SingleAsync()).Title);
    }

    [Fact]
    public async Task Import_CaseVariantUid_DoesNotMatchExisting()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        await ImportAsync(client, Vcalendar(Vjournal("Entry-ABC", "SUMMARY:Upper", "DESCRIPTION:x", "DTSTART;VALUE=DATE:20260101")));

        var result = await ImportAsync(client, Vcalendar(Vjournal("entry-abc", "SUMMARY:Lower", "DESCRIPTION:x", "DTSTART;VALUE=DATE:20260101")));

        Assert.Equal(1, result.ImportedCount); // distinct, not an update
        Assert.Equal(0, result.UpdatedCount);
    }

    [Fact]
    public async Task Import_MatchedCategory_LinksTag_UnmatchedCounted()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        var tagId = await SeedJournalTagAsync(factory, "Errands");

        var ics = Vcalendar(Vjournal("c1", "SUMMARY:Shop", "DESCRIPTION:x", "DTSTART;VALUE=DATE:20260101", "CATEGORIES:errands,Nonexistent"));
        var result = await ImportAsync(client, ics);

        Assert.Equal(1, result.ImportedCount);
        Assert.Equal(1, result.SkippedTagLinkCount);
        using var scope = factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var row = await ctx.JournalEntries.Include(e => e.EntryTags).SingleAsync();
        Assert.Equal(tagId, Assert.Single(row.EntryTags).JournalTagId);
    }

    [Fact]
    public async Task Import_Contact_WithClaim_LinksIt()
    {
        await using var factory = new ApiFactory(ReadWriteWithContacts);
        using var client = factory.CreateClient();
        var cpId = await SeedContactAsync(factory, "Acme", "urn:uuid:cp-acme");

        var ics = Vcalendar(Vjournal("cp1", "SUMMARY:Met Acme", "DESCRIPTION:x", "DTSTART;VALUE=DATE:20260101",
            "X-ODYSSEY-CONTACT:urn:uuid:cp-acme"));
        var result = await ImportAsync(client, ics);

        Assert.Equal(1, result.ImportedCount);
        Assert.Equal(0, result.SkippedContactLinkCount);
        using var scope = factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var row = await ctx.JournalEntries.Include(e => e.Contacts).SingleAsync();
        Assert.Equal(cpId, Assert.Single(row.Contacts).ContactId);
    }

    [Fact]
    public async Task Import_Contact_WithClaim_UnmatchedUid_IsSkippedButEntryImports()
    {
        await using var factory = new ApiFactory(ReadWriteWithContacts);
        using var client = factory.CreateClient();

        var ics = Vcalendar(Vjournal("cp2", "SUMMARY:Ghost contact", "DESCRIPTION:x", "DTSTART;VALUE=DATE:20260101",
            "X-ODYSSEY-CONTACT:urn:uuid:nobody"));
        var result = await ImportAsync(client, ics);

        Assert.Equal(1, result.ImportedCount);
        Assert.Equal(1, result.SkippedContactLinkCount);
    }

    [Fact]
    public async Task Import_Contact_WithoutClaim_IsSkippedButEntryImports()
    {
        await using var factory = new ApiFactory(ReadWrite); // no contacts.read
        using var client = factory.CreateClient();
        await SeedContactAsync(factory, "Acme", "urn:uuid:cp-acme");

        var ics = Vcalendar(Vjournal("cp3", "SUMMARY:Gated", "DESCRIPTION:x", "DTSTART;VALUE=DATE:20260101",
            "X-ODYSSEY-CONTACT:urn:uuid:cp-acme"));
        var result = await ImportAsync(client, ics);

        Assert.Equal(1, result.ImportedCount);
        Assert.Equal(1, result.SkippedContactLinkCount);
        using var scope = factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        Assert.Empty((await ctx.JournalEntries.Include(e => e.Contacts).SingleAsync()).Contacts);
    }

    [Fact]
    public async Task Import_UpdateWithoutClaim_LeavesExistingContactLinkUntouched()
    {
        await using var factory = new ApiFactory(ReadWrite); // no contacts.read
        using var client = factory.CreateClient();
        var cpId = await SeedContactAsync(factory, "Acme", "urn:uuid:cp-acme");
        // Creating with a contact link needs only journal.create (not contacts.read).
        var created = await CreateAsync(client, NewEntry("Linked", contactIds: [cpId]));

        // Re-import matching UID with an unresolvable X-ODYSSEY-CONTACT: N1 → link left untouched.
        var ics = Vcalendar(Vjournal(created.ExternalUid, "SUMMARY:Linked", "DESCRIPTION:x", "DTSTART;VALUE=DATE:20260601",
            "X-ODYSSEY-CONTACT:urn:uuid:cp-acme"));
        var result = await ImportAsync(client, ics);

        Assert.Equal(1, result.UpdatedCount);
        Assert.Equal(1, result.SkippedContactLinkCount);
        using var scope = factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var row = await ctx.JournalEntries.Include(e => e.Contacts).SingleAsync();
        Assert.Equal(cpId, Assert.Single(row.Contacts).ContactId); // still linked
    }

    [Fact]
    public async Task Import_UpdateWithClaim_ZeroReferences_ClearsContacts()
    {
        await using var factory = new ApiFactory(ReadWriteWithContacts);
        using var client = factory.CreateClient();
        var cpId = await SeedContactAsync(factory, "Acme", "urn:uuid:cp-acme");
        var created = await CreateAsync(client, NewEntry("Linked", contactIds: [cpId]));

        var ics = Vcalendar(Vjournal(created.ExternalUid, "SUMMARY:Linked", "DESCRIPTION:x", "DTSTART;VALUE=DATE:20260601"));
        await ImportAsync(client, ics);

        using var scope = factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        Assert.Empty((await ctx.JournalEntries.Include(e => e.Contacts).SingleAsync()).Contacts);
    }

    [Fact]
    public async Task Import_ResolvableAttachment_WithFilesRead_LinksIt()
    {
        await using var factory = new ApiFactory(ReadWriteWithFiles);
        using var client = factory.CreateClient();
        var fileId = await SeedFileAsync(factory, "doc.pdf", "application/pdf");

        var ics = Vcalendar(Vjournal("at1", "SUMMARY:With file", "DESCRIPTION:x", "DTSTART;VALUE=DATE:20260101",
            $"ATTACH:odyssey-file:{fileId}"));
        var result = await ImportAsync(client, ics);

        Assert.Equal(1, result.ImportedCount);
        Assert.Equal(0, result.SkippedAttachmentCount);
        using var scope = factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        Assert.Equal(fileId, Assert.Single((await ctx.JournalEntries.Include(e => e.Attachments).SingleAsync()).Attachments).FileId);
    }

    [Fact]
    public async Task Import_Attachment_WithoutFilesRead_IsSkipped()
    {
        await using var factory = new ApiFactory(ReadWrite); // no files.read
        using var client = factory.CreateClient();
        var fileId = await SeedFileAsync(factory, "doc.pdf", "application/pdf");

        var ics = Vcalendar(Vjournal("at2", "SUMMARY:Gated", "DESCRIPTION:x", "DTSTART;VALUE=DATE:20260101",
            $"ATTACH:odyssey-file:{fileId}"));
        var result = await ImportAsync(client, ics);

        Assert.Equal(1, result.ImportedCount);
        Assert.Equal(1, result.SkippedAttachmentCount);
    }

    [Fact]
    public async Task Import_PhotoReference_WithPhotosRead_LinksIt()
    {
        await using var factory = new ApiFactory(ReadWriteWithFiles);
        using var client = factory.CreateClient();
        var imageId = await SeedPhotoAsync(factory, "pic.jpg");

        var ics = Vcalendar(Vjournal("ph1", "SUMMARY:With photo", "DESCRIPTION:x", "DTSTART;VALUE=DATE:20260101",
            $"ATTACH:odyssey-photo:{imageId}"));
        var result = await ImportAsync(client, ics);

        Assert.Equal(1, result.ImportedCount);
        Assert.Equal(0, result.SkippedPhotoCount);
        using var scope = factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        Assert.Single((await ctx.JournalEntries.Include(e => e.Photos).SingleAsync()).Photos);
    }

    /// <summary>
    /// Issue #428 §3.3: an old export names a photo by the file it wrapped. That id still resolves — through the
    /// legacy photo's current FileId, and after the backfill through its LegacyFileId.
    /// </summary>
    [Fact]
    public async Task Import_PhotoReferenceByLegacyOrBackfilledFileId_ResolvesToThePhoto()
    {
        await using var factory = new ApiFactory(ReadWriteWithFiles);
        using var client = factory.CreateClient();
        var (legacyPhoto, legacyFile) = await PhotoTestSupport.SeedLegacyPhotoAsync(factory, null, "legacy.jpg");
        var (movedPhoto, movedFile) = await PhotoTestSupport.SeedLegacyPhotoAsync(factory, null, "moved.jpg");
        using (var scope = factory.Services.CreateScope())
        {
            // What the backfill leaves behind: the old file id kept, no file any more (the original is irrelevant here).
            var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            var moved = await context.Photos.SingleAsync(p => p.PhotoId == movedPhoto);
            moved.LegacyFileId = movedFile;
            moved.FileId = null;
            await context.SaveChangesAsync();
        }

        var ics = Vcalendar(Vjournal("ph-legacy", "SUMMARY:Old export", "DESCRIPTION:x", "DTSTART;VALUE=DATE:20260101",
            $"ATTACH:odyssey-photo:{legacyFile}", $"ATTACH:odyssey-photo:{movedFile}"));
        var result = await ImportAsync(client, ics);

        Assert.Equal(0, result.SkippedPhotoCount);
        using var assertScope = factory.Services.CreateScope();
        var ctx = assertScope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var linked = (await ctx.JournalEntries.Include(e => e.Photos).SingleAsync()).Photos.Select(p => p.PhotoId).ToHashSet();
        Assert.Equal(new HashSet<Guid> { legacyPhoto, movedPhoto }, linked);
    }

    /// <summary>Issue #428 §3.3: an image file no photo wraps is skipped and counted — the import never creates a photo.</summary>
    [Fact]
    public async Task Import_ImageFileNoPhotoWraps_IsSkipped_AndNoPhotoIsCreated()
    {
        await using var factory = new ApiFactory(ReadWriteWithFiles);
        using var client = factory.CreateClient();
        var imageFile = await SeedFileAsync(factory, "receipt.jpg", "image/jpeg");

        var ics = Vcalendar(Vjournal("ph-file", "SUMMARY:A file", "DESCRIPTION:x", "DTSTART;VALUE=DATE:20260101",
            $"ATTACH:odyssey-photo:{imageFile}"));
        var result = await ImportAsync(client, ics);

        Assert.Equal(1, result.SkippedPhotoCount);
        using var scope = factory.Services.CreateScope();
        Assert.Empty(scope.ServiceProvider.GetRequiredService<OdysseyContext>().Photos);
    }

    [Fact]
    public async Task Import_NonImagePhotoReference_IsSkippedButEntryImports()
    {
        await using var factory = new ApiFactory(ReadWriteWithFiles);
        using var client = factory.CreateClient();
        var pdfId = await SeedFileAsync(factory, "not-a-photo.pdf", "application/pdf");

        var ics = Vcalendar(Vjournal("ph2", "SUMMARY:Bad photo", "DESCRIPTION:x", "DTSTART;VALUE=DATE:20260101",
            $"ATTACH:odyssey-photo:{pdfId}"));
        var result = await ImportAsync(client, ics);

        Assert.Equal(1, result.ImportedCount);
        Assert.Equal(1, result.SkippedPhotoCount);
        using var scope = factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        Assert.Empty((await ctx.JournalEntries.Include(e => e.Photos).SingleAsync()).Photos);
    }

    [Fact]
    public async Task Import_OverLengthLocationOnUpdate_LeavesExistingUnchanged()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        var created = await CreateAsync(client, NewEntry("Trip", location: "Bergen"));

        var ics = Vcalendar(Vjournal(created.ExternalUid, "SUMMARY:Trip", "DESCRIPTION:x", "DTSTART;VALUE=DATE:20260601",
            $"X-ODYSSEY-LOCATION:{new string('z', 301)}"));
        await ImportAsync(client, ics);

        using var scope = factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        Assert.Equal("Bergen", (await ctx.JournalEntries.SingleAsync()).Location); // not cleared
    }

    [Fact]
    public async Task Import_TooManyVJournals_ReturnsBadRequest_NothingImported()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        await SystemSettingsSeed.SetAsync(
            factory.Services, SystemSettingsKeys.JournalIcsMaxImportEntries, AdminCountCap.ToString(System.Globalization.CultureInfo.InvariantCulture));

        var blocks = Enumerable.Range(0, AdminCountCap + 1)
            .Select(i => Vjournal($"u{i}", $"SUMMARY:T{i}", "DESCRIPTION:x", "DTSTART;VALUE=DATE:20260101"));
        var response = await PostIcsAsync(client, Vcalendar(blocks.ToArray()));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var scope = factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        Assert.Equal(0, await ctx.JournalEntries.CountAsync());
    }

    [Fact]
    public async Task Import_MalformedFile_ReturnsBadRequest()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.BadRequest, (await PostIcsAsync(client, "not an ics file")).StatusCode);
    }

    [Fact]
    public async Task Import_NonIcsExtension_ReturnsBadRequest()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var ics = Vcalendar(Vjournal("x1", "SUMMARY:x", "DESCRIPTION:x", "DTSTART;VALUE=DATE:20260101"));
        Assert.Equal(HttpStatusCode.BadRequest, (await PostIcsAsync(client, ics, "data.txt")).StatusCode);
    }

    [Fact]
    public async Task Import_OnlyVEvents_IsNoOp()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var ics = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//t//EN\r\n" +
                  "BEGIN:VEVENT\r\nUID:e1\r\nDTSTART:20300101T090000Z\r\nSUMMARY:Event\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var result = await ImportAsync(client, ics);

        Assert.Equal(0, result.ImportedCount);
        Assert.Equal(0, result.UpdatedCount);
    }

    [Fact]
    public async Task Import_WithOnlyCreateClaim_ReturnsForbidden()
    {
        await using var factory = new ApiFactory([PermissionClaims.JournalCreate, PermissionClaims.JournalRead]);
        using var client = factory.CreateClient();

        var ics = Vcalendar(Vjournal("p1", "SUMMARY:x", "DESCRIPTION:x", "DTSTART;VALUE=DATE:20260101"));
        Assert.Equal(HttpStatusCode.Forbidden, (await PostIcsAsync(client, ics)).StatusCode);
    }

    [Fact]
    public async Task Import_WithoutContactsRead_NoReferences_Succeeds()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var ics = Vcalendar(Vjournal("nc1", "SUMMARY:Plain", "DESCRIPTION:x", "DTSTART;VALUE=DATE:20260101"));
        var result = await ImportAsync(client, ics);

        Assert.Equal(1, result.ImportedCount);
        Assert.Equal(0, result.SkippedContactLinkCount);
    }

    // ------------------------------------------------------------------ update: link removal / reorder

    [Fact]
    public async Task Import_UpdateNamingFewerTags_RemovesTheDroppedLink()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        var alpha = await SeedJournalTagAsync(factory, "Alpha");
        var beta = await SeedJournalTagAsync(factory, "Beta");
        var created = await CreateAsync(client, NewEntry("Tagged", tagIds: [alpha, beta]));

        // Re-import the same UID naming only Alpha → Beta must be diffed off (full-replace, not merge).
        var ics = Vcalendar(Vjournal(created.ExternalUid, "SUMMARY:Tagged", "DESCRIPTION:x", "DTSTART;VALUE=DATE:20260601", "CATEGORIES:Alpha"));
        var result = await ImportAsync(client, ics);

        Assert.Equal(1, result.UpdatedCount);
        using var scope = factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var row = await ctx.JournalEntries.Include(e => e.EntryTags).SingleAsync();
        Assert.Equal(alpha, Assert.Single(row.EntryTags).JournalTagId);
    }

    [Fact]
    public async Task Import_UpdateWithNoAttach_RemovesExistingAttachment()
    {
        await using var factory = new ApiFactory(ReadWriteWithFiles);
        using var client = factory.CreateClient();
        var fileId = await SeedFileAsync(factory, "doc.pdf", "application/pdf");
        var created = await CreateAsync(client, NewEntry("Filed", attachmentFileIds: [fileId]));

        var ics = Vcalendar(Vjournal(created.ExternalUid, "SUMMARY:Filed", "DESCRIPTION:x", "DTSTART;VALUE=DATE:20260601"));
        await ImportAsync(client, ics);

        using var scope = factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        Assert.Empty((await ctx.JournalEntries.Include(e => e.Attachments).SingleAsync()).Attachments);
    }

    [Fact]
    public async Task Import_UpdateReorderingPhotos_ResequencesPosition()
    {
        await using var factory = new ApiFactory(ReadWriteWithFiles);
        using var client = factory.CreateClient();
        var imgA = await SeedPhotoAsync(factory, "a.jpg");
        var imgB = await SeedPhotoAsync(factory, "b.jpg");
        var created = await CreateAsync(client, NewEntry("Gallery", photoIds: [imgA, imgB]));
        Assert.Equal(imgA, created.Photos.OrderBy(p => p.Position).First().PhotoId);

        // Re-import the same entry with the two photos in reverse order → Position must re-sequence
        // (B→0, A→1) over the existing library Photo links, not create new ones.
        var ics = Vcalendar(Vjournal(created.ExternalUid, "SUMMARY:Gallery", "DESCRIPTION:x", "DTSTART;VALUE=DATE:20260601",
            $"ATTACH:odyssey-photo:{imgB}", $"ATTACH:odyssey-photo:{imgA}"));
        var result = await ImportAsync(client, ics);
        Assert.Equal(1, result.UpdatedCount);

        var reloaded = await GetAsync(client, created.JournalEntryId);
        var ordered = reloaded.Photos.OrderBy(p => p.Position).ToList();
        Assert.Equal(2, ordered.Count);
        Assert.Equal(imgB, ordered[0].PhotoId);
        Assert.Equal(0, ordered[0].Position);
        Assert.Equal(imgA, ordered[1].PhotoId);
        Assert.Equal(1, ordered[1].Position);
    }

    // ------------------------------------------------------------------ bounded fields / caps

    [Fact]
    public async Task Import_OverLengthUid_IsSkipped_NothingImported()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        // A UID longer than the varchar(255) column would 500 the whole batch on a strict MariaDB; it must
        // instead be a clean per-block skip, preserving skip-and-continue.
        var ics = Vcalendar(Vjournal(new string('u', 256), "SUMMARY:Too long", "DESCRIPTION:x", "DTSTART;VALUE=DATE:20260101"));
        var result = await ImportAsync(client, ics);

        Assert.Equal(0, result.ImportedCount);
        Assert.Contains("UID exceeds", Assert.Single(result.Skipped).Reason);
        using var scope = factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        Assert.Equal(0, await ctx.JournalEntries.CountAsync());
    }

    [Fact]
    public async Task Import_DescriptionTooLong_IsSkipped()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var ics = Vcalendar(Vjournal("d-long", "SUMMARY:Big", $"DESCRIPTION:{new string('d', 4097)}", "DTSTART;VALUE=DATE:20260101"));
        var result = await ImportAsync(client, ics);

        Assert.Equal(0, result.ImportedCount);
        Assert.Contains("Description", Assert.Single(result.Skipped).Reason);
    }

    [Fact]
    public async Task Import_EntryDateOutOfRange_IsSkipped()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var ics = Vcalendar(Vjournal("y1", "SUMMARY:Ancient", "DESCRIPTION:x", "DTSTART;VALUE=DATE:18000101"));
        var result = await ImportAsync(client, ics);

        Assert.Equal(0, result.ImportedCount);
        Assert.Contains("1900", Assert.Single(result.Skipped).Reason);
    }

    [Fact]
    public async Task Import_TagLinks_CappedAtMaxLinksPerKind()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        var names = await SeedManyTagsAsync(factory, 55);

        // 55 resolvable CATEGORIES against a cap of 50. The excess used to be dropped SILENTLY here —
        // issue #434 §9-A declares that a deliberate contract change rather than smuggling it in: a
        // silent cap is indistinguishable from data loss to the user, and the task import already
        // reported its own capped links. The remainder is now counted AND carries a named skip reason
        // that interpolates the effective cap, so the text cannot go stale when an administrator
        // changes it.
        var ics = Vcalendar(Vjournal("cap1", "SUMMARY:Many tags", "DESCRIPTION:x", "DTSTART;VALUE=DATE:20260101",
            "CATEGORIES:" + string.Join(",", names)));
        var result = await ImportAsync(client, ics);

        Assert.Equal(1, result.ImportedCount);
        Assert.Equal(5, result.SkippedTagLinkCount);
        var capped = Assert.Single(result.Skipped, group => group.Reason.Contains("per-entry cap"));
        Assert.Equal(5, capped.Count);
        Assert.Contains("50", capped.Reason);

        using var scope = factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var row = await ctx.JournalEntries.Include(e => e.EntryTags).SingleAsync();
        Assert.Equal(50, row.EntryTags.Count);
    }

    /// <summary>
    /// A repeat of a tag already linked is a duplicate, not a link over the cap — the rule the task
    /// import already applied, and the one the shared <c>ImportLinks.Resolve</c> now applies to both
    /// (issue #287 M8). This path used to test the cap first, so the repeat was reported as capped.
    /// </summary>
    [Fact]
    public async Task Import_ADuplicateTagAtTheCap_IsNotReportedAsCapped()
    {
        await using var factory = new ApiFactory(ReadWrite);
        await SystemSettingsSeed.SetAsync(
            factory.Services, SystemSettingsKeys.JournalEntryMaxLinksPerKind, "2");
        using var client = factory.CreateClient();
        var names = await SeedManyTagsAsync(factory, 2);

        var ics = Vcalendar(Vjournal("dup-cap", "SUMMARY:Repeat", "DESCRIPTION:x",
            "DTSTART;VALUE=DATE:20260101", $"CATEGORIES:{names[0]},{names[1]},{names[0]}"));
        var result = await ImportAsync(client, ics);

        Assert.Equal(1, result.ImportedCount);
        Assert.Equal(0, result.SkippedTagLinkCount);
        Assert.DoesNotContain(result.Skipped, group => group.Reason.Contains("per-entry cap"));
    }

    /// <summary>
    /// The defect fix (issue #434 §9-A). Before it, this service enforced a hardcoded 50 while the
    /// administrator's <c>JournalEntryMaxLinksPerKind</c> setting was honoured on the create/update path
    /// and silently ignored here — so lowering the limit took effect on one path and not the other.
    /// This test fails against <c>main</c>.
    /// </summary>
    [Fact]
    public async Task Import_TagLinks_HonourTheConfiguredCap_NotAHardcodedFifty()
    {
        await using var factory = new ApiFactory(ReadWrite);
        await SystemSettingsSeed.SetAsync(
            factory.Services, SystemSettingsKeys.JournalEntryMaxLinksPerKind, "5");
        using var client = factory.CreateClient();
        var names = await SeedManyTagsAsync(factory, 20);

        var ics = Vcalendar(Vjournal("cap-cfg", "SUMMARY:Twenty tags", "DESCRIPTION:x",
            "DTSTART;VALUE=DATE:20260101", "CATEGORIES:" + string.Join(",", names)));
        var result = await ImportAsync(client, ics);

        Assert.Equal(1, result.ImportedCount);
        Assert.Equal(15, result.SkippedTagLinkCount);

        var capped = Assert.Single(result.Skipped, group => group.Reason.Contains("per-entry cap"));
        Assert.Equal(15, capped.Count);
        Assert.Contains("5", capped.Reason);

        using var scope = factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var row = await ctx.JournalEntries.Include(e => e.EntryTags).SingleAsync();
        Assert.Equal(5, row.EntryTags.Count);
    }

    /// <summary>
    /// The same cap, read by the create/update path, resolves to the same number — which is the property
    /// mirroring the setting onto a second lookup record would have broken. A descriptor can evict only
    /// one cache entry, so a mirrored value would have gone stale on one of its two readers for up to
    /// 30 seconds: an intermittent rerun of the very divergence the fix removes.
    /// </summary>
    [Fact]
    public async Task Import_And_Create_ResolveTheSameLinkCap()
    {
        await using var factory = new ApiFactory(ReadWrite);
        await SystemSettingsSeed.SetAsync(
            factory.Services, SystemSettingsKeys.JournalEntryMaxLinksPerKind, "3");
        using var client = factory.CreateClient();
        var names = await SeedManyTagsAsync(factory, 10);

        using var scope = factory.Services.CreateScope();
        var tagIds = await scope.ServiceProvider.GetRequiredService<OdysseyContext>()
            .JournalTags.Select(tag => tag.JournalTagId).ToListAsync();

        // The create path rejects an over-cap request outright (422 — an unprocessable domain rule,
        // not a malformed body) and names the effective cap...
        var created = await client.PostAsJsonAsync(Path, new
        {
            Title = "Direct",
            Content = "x",
            EntryDate = "2026-01-01",
            TagIds = tagIds,
        });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, created.StatusCode);
        Assert.Contains("3", await created.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // ...and the import path caps at the same number rather than at 50.
        var ics = Vcalendar(Vjournal("same-cap", "SUMMARY:Ten tags", "DESCRIPTION:x",
            "DTSTART;VALUE=DATE:20260101", "CATEGORIES:" + string.Join(",", names)));
        await ImportAsync(client, ics);

        var ctx = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var row = await ctx.JournalEntries.Include(e => e.EntryTags).SingleAsync();
        Assert.Equal(3, row.EntryTags.Count);
    }

    /// <summary>
    /// The cost of the fix, pinned: the link caps arrive on ONE snapshot awaited once per import, not
    /// once per link. Threading them through the resolution loops instead would make a 500-category
    /// entry perform 500 lookups.
    /// </summary>
    [Fact]
    public async Task Import_AwaitsTheJournalLimitsSnapshotExactlyOnce()
    {
        var counter = new CountingJournalLimitsLookup();
        await using var factory = new CountingApiFactory(ReadWrite, counter);
        using var client = factory.CreateClient();
        var names = await SeedManyTagsAsync(factory, 60);

        var ics = Vcalendar(Vjournal("count1", "SUMMARY:Many", "DESCRIPTION:x",
            "DTSTART;VALUE=DATE:20260101", "CATEGORIES:" + string.Join(",", names)));
        await ImportAsync(client, ics);

        Assert.Equal(1, counter.Calls);
    }

    // ------------------------------------------------------------------ export cap discriminator

    [Fact]
    public async Task Export_ExceedingCap_Returns400_WithMachineReadableCode()
    {
        await using var factory = new ApiFactory(ReadOnly);
        using var client = factory.CreateClient();
        await SystemSettingsSeed.SetAsync(
            factory.Services, SystemSettingsKeys.JournalIcsMaxExportRows, AdminCountCap.ToString(System.Globalization.CultureInfo.InvariantCulture));
        await SeedManyEntriesAsync(factory, AdminCountCap + 1);

        var response = await client.GetAsync(IcsPath);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.NotNull(problem);
        Assert.True(problem!.Extensions.TryGetValue("code", out var code));
        Assert.Contains(ExportLimitExceededException.DiscriminatorCode, code!.ToString());
    }

    // ------------------------------------------------------------------ direct API ExternalUid

    [Fact]
    public async Task Create_WithoutExternalUid_GeneratesUrnUuid()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        var created = await CreateAsync(client, NewEntry("Auto"));
        Assert.StartsWith("urn:uuid:", created.ExternalUid);
    }

    [Fact]
    public async Task Create_WithSuppliedExternalUid_StoresVerbatim()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        var request = NewEntry("Given");
        request.ExternalUid = "my-own-id";
        var post = await client.PostAsJsonAsync(Path, request);
        post.EnsureSuccessStatusCode();
        var created = (await post.Content.ReadFromJsonAsync<ExistingJournalEntry>())!;
        Assert.Equal("my-own-id", created.ExternalUid);
    }

    [Fact]
    public async Task Create_WithExternalUidAlreadyUsed_ReturnsBadRequest()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        var first = NewEntry("First");
        first.ExternalUid = "shared-id";
        (await client.PostAsJsonAsync(Path, first)).EnsureSuccessStatusCode();

        var second = NewEntry("Second");
        second.ExternalUid = "shared-id";
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(Path, second)).StatusCode);
    }

    [Fact]
    public async Task Create_WithControlCharExternalUid_ReturnsBadRequest()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        var request = NewEntry("Bad");
        request.ExternalUid = "line1\nline2";
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(Path, request)).StatusCode);
    }

    // ------------------------------------------------------------------ helpers

    // ------------------------------------------------------------------ attached files and photos travel inline

    [Fact]
    public async Task Export_WithAttachedFile_WithoutFilesRead_IsForbidden_AndSoIsTheSummary()
    {
        await using var factory = new ApiFactory(ReadWriteWithFiles);
        using var client = factory.CreateClient();
        var fileId = await SeedFileWithContentAsync(factory, "note.txt", "text/plain", NoteBytes);
        var entry = await CreateAsync(client, NewEntry("With file", attachmentFileIds: [fileId]));

        await using var readOnly = new ApiFactory([PermissionClaims.JournalRead, PermissionClaims.PhotosRead], sharingStoreWith: factory);
        using var readOnlyClient = readOnly.CreateClient();

        Assert.Equal(HttpStatusCode.Forbidden, (await readOnlyClient.GetAsync(IcsPath)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await readOnlyClient.GetAsync(IcsPath + "/summary")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await readOnlyClient.GetAsync($"{Path}/{entry.JournalEntryId}/vjournal")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await readOnlyClient.GetAsync($"{Path}/{entry.JournalEntryId}/vjournal/summary")).StatusCode);
    }

    [Fact]
    public async Task Export_WithPhoto_WithoutPhotosRead_IsForbidden_AndSoIsTheSummary()
    {
        await using var factory = new ApiFactory(ReadWriteWithFiles);
        using var client = factory.CreateClient();
        var photoId = await SeedPhotoAsync(factory, "beach.jpg");
        await CreateAsync(client, NewEntry("With photo", photoIds: [photoId]));

        await using var readOnly = new ApiFactory([PermissionClaims.JournalRead, PermissionClaims.FilesRead], sharingStoreWith: factory);
        using var readOnlyClient = readOnly.CreateClient();

        Assert.Equal(HttpStatusCode.Forbidden, (await readOnlyClient.GetAsync(IcsPath)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await readOnlyClient.GetAsync(IcsPath + "/summary")).StatusCode);
    }

    [Fact]
    public async Task Export_WithoutFilesOrPhotos_NeedsOnlyJournalRead()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        await CreateAsync(client, NewEntry("Plain"));

        await using var readOnly = new ApiFactory(ReadOnly, sharingStoreWith: factory);
        using var readOnlyClient = readOnly.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await readOnlyClient.GetAsync(IcsPath)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await readOnlyClient.GetAsync(IcsPath + "/summary")).StatusCode);
    }

    [Fact]
    public async Task Summary_CountsEntriesFilesAndPhotos_ForAllFilteredAndSingle()
    {
        await using var factory = new ApiFactory(ReadWriteWithFiles);
        using var client = factory.CreateClient();
        var fileId = await SeedFileWithContentAsync(factory, "note.txt", "text/plain", NoteBytes);
        var photoBytes = PhotoTestData.Jpeg(PhotoTestData.NextSeed());
        var (photoId, _) = await PhotoTestSupport.SeedLegacyPhotoAsync(factory, null, "beach.jpg", photoBytes);
        var withBoth = await CreateAsync(client, NewEntry("Alpha", photoIds: [photoId], attachmentFileIds: [fileId]));
        await CreateAsync(client, NewEntry("Beta"));

        var all = await client.GetFromJsonAsync<JournalEntryIcsExportSummary>(IcsPath + "/summary");
        var filtered = await client.GetFromJsonAsync<JournalEntryIcsExportSummary>(IcsPath + "/summary?search=Beta");
        var single = await client.GetFromJsonAsync<JournalEntryIcsExportSummary>($"{Path}/{withBoth.JournalEntryId}/vjournal/summary");

        Assert.Equal((2, 1, 1, (long)(NoteBytes.Length + photoBytes.Length)),
            (all!.EntryCount, all.AttachmentCount, all.PhotoCount, all.AttachmentBytes));
        Assert.Equal((1, 0, 0), (filtered!.EntryCount, filtered.AttachmentCount, filtered.PhotoCount));
        Assert.Equal((1, 1, 1), (single!.EntryCount, single.AttachmentCount, single.PhotoCount));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{Path}/{Guid.NewGuid()}/vjournal/summary")).StatusCode);
    }

    [Fact]
    public async Task Export_AllFilteredAndSingle_CarryFileAndPhotoInline_WithThePhotosTitleAndCaption()
    {
        await using var factory = new ApiFactory(ReadWriteWithFiles);
        using var client = factory.CreateClient();
        var fileId = await SeedFileWithContentAsync(factory, "note.txt", "text/plain", NoteBytes);
        var photoBytes = PhotoTestData.Jpeg(PhotoTestData.NextSeed());
        var photoId = await SeedTitledPhotoAsync(factory, photoBytes, "Sunset", "Over the fjord");
        var entry = await CreateAsync(client, NewEntry("Camping", photoIds: [photoId], attachmentFileIds: [fileId]));

        var all = Unfold(await client.GetStringAsync(IcsPath));
        var filtered = Unfold(await client.GetStringAsync(IcsPath + "?search=Camp"));
        var single = Unfold(await client.GetStringAsync($"{Path}/{entry.JournalEntryId}/vjournal"));

        foreach (var body in new[] { all, filtered, single })
        {
            Assert.Contains(";ENCODING=BASE64;VALUE=BINARY;FILENAME=\"note.txt\";", body);
            Assert.Contains($"X-ODYSSEY-SHA256={Sha256(NoteBytes)}:" + Convert.ToBase64String(NoteBytes), body);
            Assert.Contains($";X-ODYSSEY-KIND=PHOTO;X-ODYSSEY-PHOTO-ID={photoId}", body);
            Assert.Contains(";X-ODYSSEY-TITLE=\"Sunset\";X-ODYSSEY-CAPTION=\"Over the fjord\"", body);
            Assert.Contains(Convert.ToBase64String(photoBytes), body);
        }
    }

    [Fact]
    public async Task Export_OverTheByteCap_IsRefusedBeforeAnyByte()
    {
        await using var factory = new ApiFactory(ReadWriteWithFiles);
        using var client = factory.CreateClient();
        await SystemSettingsSeed.SetAsync(factory.Services, SystemSettingsKeys.JournalIcsMaxExportMegabytes, "1");
        var big = new byte[1024 * 1024]; // 1 MB of content is ~1.4 MB once base64-encoded and folded
        var fileId = await SeedFileWithContentAsync(factory, "big.bin", "application/octet-stream", big);
        await CreateAsync(client, NewEntry("Big", attachmentFileIds: [fileId]));

        var summary = await client.GetAsync(IcsPath + "/summary");
        var export = await client.GetAsync(IcsPath);

        Assert.Equal(HttpStatusCode.BadRequest, summary.StatusCode);
        Assert.Contains("1 MB export limit", await summary.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, export.StatusCode);
    }

    [Fact]
    public async Task RoundTrip_SameInstance_ReusesTheFileAndPhoto_NoDuplicate()
    {
        await using var factory = new ApiFactory(ReadWriteWithUploads);
        using var client = factory.CreateClient();
        var fileId = await SeedFileWithContentAsync(factory, "note.txt", "text/plain", NoteBytes);
        var photoId = await SeedPhotoAsync(factory, "beach.jpg");
        var entry = await CreateAsync(client, NewEntry("Camping", photoIds: [photoId], attachmentFileIds: [fileId]));

        var result = await ImportAsync(client, await client.GetStringAsync(IcsPath));

        Assert.Equal((1, 0, 1, 0, 1), (result.UpdatedCount, result.StoredFileCount, result.ReusedFileCount, result.StoredPhotoCount, result.ReusedPhotoCount));
        Assert.Equal((0, 0), (result.SkippedAttachmentCount, result.SkippedPhotoCount));
        Assert.Single(await PhotosAsync(factory));
        Assert.Equal(2, (await FilesAsync(factory)).Count); // the file and the legacy photo's wrapped file, nothing new
        var row = await EntryAsync(factory, entry.ExternalUid!);
        Assert.Equal(fileId, Assert.Single(row.Attachments).FileId);
        Assert.Equal(photoId, Assert.Single(row.Photos).PhotoId);
    }

    [Fact]
    public async Task RoundTrip_IntoAnotherInstance_StoresTheFile_AndUploadsThePhoto_WithTitleAndCaption()
    {
        await using var source = new ApiFactory(ReadWriteWithFiles);
        using var sourceClient = source.CreateClient();
        var fileId = await SeedFileWithContentAsync(source, "note.txt", "text/plain", NoteBytes);
        var photoBytes = PhotoTestData.Jpeg(PhotoTestData.NextSeed());
        var photoId = await SeedTitledPhotoAsync(source, photoBytes, "Sunset", "Over the fjord");
        var entry = await CreateAsync(sourceClient, NewEntry("Camping", photoIds: [photoId], attachmentFileIds: [fileId]));
        var exported = await sourceClient.GetStringAsync(IcsPath);

        await using var target = new ApiFactory(ReadWriteWithUploads);
        using var targetClient = target.CreateClient();
        var result = await ImportAsync(targetClient, exported);

        Assert.Equal((1, 1, 0, 1, 0), (result.ImportedCount, result.StoredFileCount, result.ReusedFileCount, result.StoredPhotoCount, result.ReusedPhotoCount));
        var file = Assert.Single(await FilesAsync(target));
        Assert.Equal(("note.txt", "text/plain", Sha256(NoteBytes)), (file.FileName, file.ContentType, file.Sha256Hash));
        Assert.NotNull(file.JournalStoredObjectId);
        var photo = Assert.Single(await PhotosAsync(target));
        Assert.Equal(("Sunset", "Over the fjord"), (photo.Title, photo.Caption));
        Assert.NotNull(photo.StoredObjectId); // an owned original on the object store, not a Files-store row
        var row = await EntryAsync(target, entry.ExternalUid!);
        Assert.Equal(file.Id, Assert.Single(row.Attachments).FileId);
        Assert.Equal(photo.PhotoId, Assert.Single(row.Photos).PhotoId);

        // Importing the same file again matches what the first import stored.
        var again = await ImportAsync(targetClient, exported);
        Assert.Equal((1, 0, 1, 0, 1), (again.UpdatedCount, again.StoredFileCount, again.ReusedFileCount, again.StoredPhotoCount, again.ReusedPhotoCount));
        Assert.Single(await PhotosAsync(target));
        Assert.Single(await FilesAsync(target));
    }

    [Fact]
    public async Task Import_InlinePhoto_MatchingAStoredPhoto_IsReusedByHash_AndKeepsItsOwnTitle()
    {
        await using var factory = new ApiFactory(ReadWriteWithUploads);
        using var client = factory.CreateClient();
        var photoBytes = PhotoTestData.Jpeg(PhotoTestData.NextSeed());
        var existingId = await SeedTitledPhotoAsync(factory, photoBytes, "Library title", null);

        // No X-ODYSSEY-PHOTO-ID: a photo from elsewhere whose bytes the library already holds.
        var result = await ImportAsync(client, Vcalendar(Vjournal("p1", "SUMMARY:Hash match", "DESCRIPTION:x", "DTSTART;VALUE=DATE:20260101",
            InlinePhoto(photoBytes, "renamed.jpg", title: "File title"))));

        Assert.Equal((0, 1), (result.StoredPhotoCount, result.ReusedPhotoCount));
        var photo = Assert.Single(await PhotosAsync(factory));
        Assert.Equal((existingId, "Library title"), (photo.PhotoId, photo.Title)); // a match is reused as-is
        Assert.Equal(existingId, Assert.Single((await EntryAsync(factory, "p1")).Photos).PhotoId);
    }

    [Fact]
    public async Task Import_SameInlinePhotoOnTwoEntries_IsUploadedOnce()
    {
        await using var factory = new ApiFactory(ReadWriteWithUploads);
        using var client = factory.CreateClient();
        var photo = InlinePhoto(PhotoTestData.Jpeg(PhotoTestData.NextSeed()), "beach.jpg");

        var result = await ImportAsync(client, Vcalendar(
            Vjournal("d1", "SUMMARY:One", "DESCRIPTION:x", "DTSTART;VALUE=DATE:20260101", photo),
            Vjournal("d2", "SUMMARY:Two", "DESCRIPTION:x", "DTSTART;VALUE=DATE:20260102", photo)));

        Assert.Equal((2, 1, 1), (result.ImportedCount, result.StoredPhotoCount, result.ReusedPhotoCount));
        var stored = Assert.Single(await PhotosAsync(factory));
        Assert.Equal(stored.PhotoId, Assert.Single((await EntryAsync(factory, "d1")).Photos).PhotoId);
        Assert.Equal(stored.PhotoId, Assert.Single((await EntryAsync(factory, "d2")).Photos).PhotoId);
    }

    [Theory]
    [InlineData(PermissionClaims.FilesCreate)]
    [InlineData(PermissionClaims.FilesRead)]
    public async Task Import_InlineFile_WithoutBothFileClaims_IsForbidden_NothingImported(string missing)
    {
        await using var factory = new ApiFactory(ReadWriteWithUploads.Where(c => c != missing).ToArray());
        using var client = factory.CreateClient();

        var response = await PostIcsAsync(client, Vcalendar(Vjournal("f1", "SUMMARY:Gated", "DESCRIPTION:x", "DTSTART;VALUE=DATE:20260101",
            InlineAttach(NoteBytes, "note.txt", "text/plain"))));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var scope = factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        Assert.Equal(0, await ctx.JournalEntries.CountAsync());
        Assert.Empty(await FilesAsync(factory));
    }

    [Theory]
    [InlineData(PermissionClaims.PhotosCreate)]
    [InlineData(PermissionClaims.PhotosRead)]
    public async Task Import_InlinePhoto_WithoutBothPhotoClaims_IsForbidden_NothingImported(string missing)
    {
        await using var factory = new ApiFactory(ReadWriteWithUploads.Where(c => c != missing).ToArray());
        using var client = factory.CreateClient();

        var response = await PostIcsAsync(client, Vcalendar(Vjournal("g1", "SUMMARY:Gated", "DESCRIPTION:x", "DTSTART;VALUE=DATE:20260101",
            InlinePhoto(PhotoTestData.Jpeg(PhotoTestData.NextSeed()), "beach.jpg"))));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var scope = factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        Assert.Equal(0, await ctx.JournalEntries.CountAsync());
        Assert.Empty(await PhotosAsync(factory));
    }

    [Fact]
    public async Task Import_InlinePhoto_ThatIsNotAnImage_IsSkipped_ButTheEntryImports()
    {
        await using var factory = new ApiFactory(ReadWriteWithUploads);
        using var client = factory.CreateClient();

        var result = await ImportAsync(client, Vcalendar(Vjournal("n1", "SUMMARY:Not a photo", "DESCRIPTION:x", "DTSTART;VALUE=DATE:20260101",
            InlinePhoto(NoteBytes, "fake.jpg"))));

        Assert.Equal((1, 1, 0), (result.ImportedCount, result.SkippedPhotoCount, result.StoredPhotoCount));
        var group = Assert.Single(result.Skipped);
        Assert.StartsWith("Attached photo", group.Reason);
        Assert.Equal(["Not a photo"], group.SampleTitles);
        Assert.Empty(await PhotosAsync(factory));
    }

    [Fact]
    public async Task Import_InlineFile_LargerThanTheImportCap_IsRefusedWhole()
    {
        await using var factory = new ApiFactory(ReadWriteWithUploads);
        using var client = factory.CreateClient();
        await SystemSettingsSeed.SetAsync(factory.Services, SystemSettingsKeys.JournalIcsMaxImportMegabytes, "1");

        var response = await PostIcsAsync(client, Vcalendar(Vjournal("b1", "SUMMARY:Big", "DESCRIPTION:x", "DTSTART;VALUE=DATE:20260101",
            InlineAttach(new byte[1024 * 1024], "big.txt", "text/plain"))));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(await FilesAsync(factory));
    }

    [Fact]
    public async Task Import_Category_MatchesAnArchivedTag_PreferringAnActiveOne()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        var archivedOnly = await SeedJournalTagAsync(factory, "Retired", archived: true);
        await SeedJournalTagAsync(factory, "Both", archived: true);
        var activeBoth = await SeedJournalTagAsync(factory, "Both");

        var result = await ImportAsync(client, Vcalendar(Vjournal("t1", "SUMMARY:Tagged", "DESCRIPTION:x", "DTSTART;VALUE=DATE:20260101",
            "CATEGORIES:retired,BOTH")));

        Assert.Equal(0, result.SkippedTagLinkCount);
        var tags = (await EntryAsync(factory, "t1")).EntryTags.Select(t => t.JournalTagId).Order().ToArray();
        Assert.Equal(new[] { archivedOnly, activeBoth }.Order().ToArray(), tags);
    }

    [Fact]
    public async Task Import_NewInlinePhoto_OverTheAdminUploadCap_IsSkipped_ButTheEntryImports()
    {
        await using var factory = new ApiFactory(ReadWriteWithUploads);
        using var client = factory.CreateClient();
        await SystemSettingsSeed.SetAsync(factory.Services, SystemSettingsKeys.FileStorageMaxUploadMegabytes, "1");
        // A JPEG header padded past the 1 MB cap: the size check runs before the image is decoded.
        var big = PhotoTestData.Jpeg(PhotoTestData.NextSeed()).Concat(new byte[1536 * 1024]).ToArray();

        var result = await ImportAsync(client, Vcalendar(Vjournal("big1", "SUMMARY:Big photo", "DESCRIPTION:x", "DTSTART;VALUE=DATE:20260101",
            InlinePhoto(big, "big.jpg"))));

        Assert.Equal((1, 1, 0), (result.ImportedCount, result.SkippedPhotoCount, result.StoredPhotoCount));
        var group = Assert.Single(result.Skipped);
        Assert.Contains("1 MB limit", group.Reason);
        Assert.Equal(["Big photo"], group.SampleTitles);
        Assert.Empty(await PhotosAsync(factory));
    }

    // The two kinds are gated apart: photo claims never stand in for file claims, nor the reverse.
    [Fact]
    public async Task Import_PhotoClaimsAlone_AcceptAnInlinePhoto_ButRefuseAnInlineFile()
    {
        string[] photosOnly =
        [
            PermissionClaims.JournalRead, PermissionClaims.JournalCreate, PermissionClaims.JournalUpdate,
            PermissionClaims.PhotosRead, PermissionClaims.PhotosCreate,
        ];
        await using var factory = new ApiFactory(photosOnly);
        using var client = factory.CreateClient();

        var photo = await ImportAsync(client, Vcalendar(Vjournal("po1", "SUMMARY:Photo", "DESCRIPTION:x", "DTSTART;VALUE=DATE:20260101",
            InlinePhoto(PhotoTestData.Jpeg(PhotoTestData.NextSeed()), "beach.jpg"))));
        var file = await PostIcsAsync(client, Vcalendar(Vjournal("po2", "SUMMARY:File", "DESCRIPTION:x", "DTSTART;VALUE=DATE:20260101",
            InlineAttach(NoteBytes, "note.txt", "text/plain"))));

        Assert.Equal((1, 1), (photo.ImportedCount, photo.StoredPhotoCount));
        Assert.Equal(HttpStatusCode.Forbidden, file.StatusCode);
    }

    [Fact]
    public async Task Import_FileClaimsAlone_AcceptAnInlineFile_ButRefuseAnInlinePhoto()
    {
        string[] filesOnly =
        [
            PermissionClaims.JournalRead, PermissionClaims.JournalCreate, PermissionClaims.JournalUpdate,
            PermissionClaims.FilesRead, PermissionClaims.FilesCreate,
        ];
        await using var factory = new ApiFactory(filesOnly);
        using var client = factory.CreateClient();

        var file = await ImportAsync(client, Vcalendar(Vjournal("fo1", "SUMMARY:File", "DESCRIPTION:x", "DTSTART;VALUE=DATE:20260101",
            InlineAttach(NoteBytes, "note.txt", "text/plain"))));
        var photo = await PostIcsAsync(client, Vcalendar(Vjournal("fo2", "SUMMARY:Photo", "DESCRIPTION:x", "DTSTART;VALUE=DATE:20260101",
            InlinePhoto(PhotoTestData.Jpeg(PhotoTestData.NextSeed()), "beach.jpg"))));

        Assert.Equal((1, 1), (file.ImportedCount, file.StoredFileCount));
        Assert.Equal(HttpStatusCode.Forbidden, photo.StatusCode);
        Assert.Empty(await PhotosAsync(factory));
    }

    [Fact]
    public async Task Export_ThatGrowsPastTheByteCapWhileStreaming_IsAborted_NotTruncated()
    {
        // The pre-flight check judges files and photos exactly, but entry text only as it is written. Text alone over
        // the cap must end the response abnormally, so a browser reports a failed download instead of saving part of it.
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        await SystemSettingsSeed.SetAsync(factory.Services, SystemSettingsKeys.JournalIcsMaxExportMegabytes, "1");
        using (var scope = factory.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            var now = DateTime.UtcNow;
            for (var i = 0; i < 400; i++)
            {
                context.JournalEntries.Add(new JournalEntry
                {
                    ExternalUid = $"urn:uuid:bulk-{i}", Title = $"Bulk {i}", Content = new string('x', 4000),
                    EntryDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                    CreatedByUserId = ActorUserId, CreatedAt = now, UpdatedAt = now,
                });
            }

            await context.SaveChangesAsync();
        }

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(IcsPath + "/summary")).StatusCode); // nothing to judge up front
        var failure = await Record.ExceptionAsync(async () =>
        {
            using var response = await client.GetAsync(IcsPath);
            var body = await response.Content.ReadAsStringAsync();
            Assert.Fail($"The export completed ({body.Length} chars) instead of being aborted.");
        });

        Assert.True(failure is HttpRequestException or IOException or OperationCanceledException, failure?.ToString());
    }

    // Both export actions stream gigabytes of inline content, so both take the export permits and deadline; a
    // single-entry route without them would be an unthrottled path to the same streams.
    [Theory]
    [InlineData(nameof(Odyssey.Api.Controllers.JournalEntriesController.ExportVJournal))]
    [InlineData(nameof(Odyssey.Api.Controllers.JournalEntriesController.ExportSingleVJournal))]
    public void EveryStreamingExport_TakesTheExportConcurrencyLimiters(string action)
    {
        var method = typeof(Odyssey.Api.Controllers.JournalEntriesController).GetMethod(action)!;

        var rateLimit = method.GetCustomAttributes(typeof(Microsoft.AspNetCore.RateLimiting.EnableRateLimitingAttribute), false)
            .Cast<Microsoft.AspNetCore.RateLimiting.EnableRateLimitingAttribute>().SingleOrDefault();
        Assert.Equal(Odyssey.Api.ImportExportRateLimiting.ExportConcurrencyPolicy, rateLimit?.PolicyName);
        Assert.Contains(method.GetCustomAttributes(typeof(TypeFilterAttribute), false).Cast<TypeFilterAttribute>(),
            a => a.ImplementationType == typeof(Odyssey.Api.ExportConcurrencyFilter));
    }

    private static async Task<Guid> SeedTitledPhotoAsync(
        WebApplicationFactory<Program> factory, byte[] bytes, string? title, string? caption)
    {
        var (photoId, _) = await PhotoTestSupport.SeedLegacyPhotoAsync(factory, null, "beach.jpg", bytes);
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var photo = await context.Photos.SingleAsync(p => p.PhotoId == photoId);
        photo.Title = title;
        photo.Caption = caption;
        await context.SaveChangesAsync();
        return photoId;
    }

    private static int Count(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    private static async Task<string?> FileNameOf(HttpResponseMessage response) =>
        response.Content.Headers.ContentDisposition?.FileNameStar
        ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"');

    private static string Vcalendar(params string[] journals) =>
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//test//EN\r\n" + string.Concat(journals) + "END:VCALENDAR\r\n";

    private static string Vjournal(string uid, params string[] lines) =>
        "BEGIN:VJOURNAL\r\nUID:" + uid + "\r\n" + string.Join("\r\n", lines) + "\r\nEND:VJOURNAL\r\n";

    private static NewJournalEntry NewEntry(
        string title,
        string? content = null,
        string? location = null,
        DateTime? entryDate = null,
        Guid[]? tagIds = null,
        Guid[]? contactIds = null,
        Guid[]? photoIds = null,
        Guid[]? attachmentFileIds = null) => new()
    {
        Title = title,
        Content = content ?? "Dear journal, today was a day.",
        EntryDate = entryDate ?? new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        Location = location,
        TagIds = tagIds ?? [],
        ContactIds = contactIds ?? [],
        PhotoIds = photoIds ?? [],
        AttachmentFileIds = attachmentFileIds ?? [],
    };

    private static async Task<ExistingJournalEntry> CreateAsync(HttpClient client, NewJournalEntry request)
    {
        var post = await client.PostAsJsonAsync(Path, request);
        post.EnsureSuccessStatusCode();
        return (await post.Content.ReadFromJsonAsync<ExistingJournalEntry>())!;
    }

    private static async Task<ExistingJournalEntry> GetAsync(HttpClient client, Guid id) =>
        (await client.GetFromJsonAsync<ExistingJournalEntry>($"{Path}/{id}"))!;

    private static async Task<ExistingJournalEntry> ArchiveAsync(HttpClient client, ExistingJournalEntry entry)
    {
        var update = new UpdateJournalEntry
        {
            Title = entry.Title,
            Content = entry.Content,
            EntryDate = entry.EntryDate,
            Location = entry.Location,
            Archived = true,
        };
        var put = await client.PutAsJsonAsync($"{Path}/{entry.JournalEntryId}", update);
        put.EnsureSuccessStatusCode();
        return (await put.Content.ReadFromJsonAsync<ExistingJournalEntry>())!;
    }

    private static async Task<JournalEntryIcsImportResult> ImportAsync(HttpClient client, string ics)
    {
        var response = await PostIcsAsync(client, ics);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JournalEntryIcsImportResult>())!;
    }

    private static async Task<HttpResponseMessage> PostIcsAsync(
        HttpClient client, string ics, string fileName = "entries.ics", string contentType = "text/calendar")
    {
        using var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(Encoding.UTF8.GetBytes(ics));
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        content.Add(fileContent, "file", fileName);
        return await client.PostAsync(IcsPath, content);
    }

    private static async Task<Guid> SeedJournalTagAsync(
        WebApplicationFactory<Program> factory, string name, bool archived = false)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        await context.Database.EnsureCreatedAsync();

        var id = Guid.NewGuid();
        context.JournalTags.Add(new JournalTag { JournalTagId = id, Name = name, Archived = archived ? DateTime.UtcNow : null });
        await context.SaveChangesAsync();
        return id;
    }

    private static async Task<List<string>> SeedManyTagsAsync(WebApplicationFactory<Program> factory, int count)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        await context.Database.EnsureCreatedAsync();

        var names = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            var name = $"captag{i}";
            names.Add(name);
            context.JournalTags.Add(new JournalTag { JournalTagId = Guid.NewGuid(), Name = name });
        }

        await context.SaveChangesAsync();
        return names;
    }

    private static async Task<Guid> SeedContactAsync(WebApplicationFactory<Program> factory, string name, string externalUid)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        await context.Database.EnsureCreatedAsync();

        var id = Guid.NewGuid();
        context.Contacts.Add(new Contact
        {
            ContactId = id,
            ExternalUid = externalUid,
            NormalizedName = name.Trim().ToLowerInvariant(),
            Type = Odyssey.Dtos.ContactType.Organization,
            OrganizationDetails = new() { LegalName = name },
        });
        await context.SaveChangesAsync();
        return id;
    }

    private static async Task<Guid> SeedPhotoAsync(WebApplicationFactory<Program> factory, string fileName) =>
        (await PhotoTestSupport.SeedLegacyPhotoAsync(factory, null, fileName)).PhotoId;

    // A legacy file with real content and its real hash, so an import can match it.
    private static async Task<Guid> SeedFileWithContentAsync(
        WebApplicationFactory<Program> factory, string fileName, string contentType, byte[] content)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        await context.Database.EnsureCreatedAsync();

        var blob = new FileBlob { Id = Guid.NewGuid(), Content = content };
        var metadataId = Guid.NewGuid();
        context.FileBlob.Add(blob);
        context.FileMetadata.Add(new FileMetadata
        {
            Id = metadataId,
            UploadedByUserId = ActorUserId,
            FileName = fileName,
            ContentType = contentType,
            SizeBytes = content.Length,
            Sha256Hash = Sha256(content),
            UploadedAtUtc = DateTime.UtcNow,
            FileBlobId = blob.Id,
            FileBlob = blob,
        });
        await context.SaveChangesAsync();
        return metadataId;
    }

    private static string Sha256(byte[] content) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(content));

    // One inline attachment line, folded the way a producer might: base64 split over continuation lines.
    private static string InlineAttach(byte[] content, string fileName, string contentType, string? extraParams = null)
    {
        var base64 = Convert.ToBase64String(content);
        var folded = string.Join("\r\n ", Enumerable.Range(0, (base64.Length + 59) / 60).Select(i => base64.Substring(i * 60, Math.Min(60, base64.Length - i * 60))));
        return $"ATTACH;FMTTYPE={contentType};ENCODING=BASE64;VALUE=BINARY;FILENAME=\"{fileName}\"{extraParams}:{folded}";
    }

    private static string InlinePhoto(byte[] content, string fileName, string? title = null, string? caption = null) =>
        InlineAttach(content, fileName, "image/jpeg",
            ";X-ODYSSEY-KIND=PHOTO"
            + (title is null ? "" : $";X-ODYSSEY-TITLE=\"{title}\"")
            + (caption is null ? "" : $";X-ODYSSEY-CAPTION=\"{caption}\""));

    private static async Task<List<FileMetadata>> FilesAsync(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        return await context.FileMetadata.AsNoTracking().ToListAsync();
    }

    private static async Task<List<Photo>> PhotosAsync(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        return await context.Photos.AsNoTracking().ToListAsync();
    }

    private static async Task<JournalEntry> EntryAsync(WebApplicationFactory<Program> factory, string uid)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        return await context.JournalEntries.AsNoTracking()
            .Include(e => e.Attachments).Include(e => e.Photos).Include(e => e.EntryTags)
            .SingleAsync(e => e.ExternalUid == uid);
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

    private static async Task SeedManyEntriesAsync(WebApplicationFactory<Program> factory, int count)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        await context.Database.EnsureCreatedAsync();

        var now = DateTime.UtcNow;
        for (var i = 0; i < count; i++)
        {
            context.JournalEntries.Add(new JournalEntry
            {
                ExternalUid = $"urn:uuid:bulk-{i}",
                Title = $"Entry {i}",
                Content = "seeded",
                EntryDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                CreatedByUserId = ActorUserId,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }

        await context.SaveChangesAsync();
    }

    /// <summary>
    /// Counts snapshot reads while delegating to the shipped defaults, for the once-per-import assertion
    /// above. A record rather than a mock so the values it serves are the real ones.
    /// </summary>
    private sealed class CountingJournalLimitsLookup : Odyssey.Core.Journal.IJournalLimitsLookup
    {
        public int Calls { get; private set; }

        public Task<Odyssey.Core.Journal.JournalLimits> GetAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new Odyssey.Core.Journal.JournalLimits(
                PhotoMaxLinksPerKind: SystemSettingsDefaults.PhotoMaxLinksPerKind,
                PhotoMaxAlbumMembers: SystemSettingsDefaults.PhotoMaxAlbumMembers,
                JournalEntryMaxLinksPerKind: SystemSettingsDefaults.JournalEntryMaxLinksPerKind,
                JournalTaskMaxLinksPerKind: SystemSettingsDefaults.JournalTaskMaxLinksPerKind,
                PhotoMetadataReadBytes: SystemSettingsDefaults.PhotoMetadataReadMegabytes * 1024L * 1024,
                PhotoMetadataExtractionTimeoutSeconds:
                    SystemSettingsDefaults.PhotoMetadataExtractionTimeoutSeconds,
                CalendarMaxWindowDays: SystemSettingsDefaults.CalendarMaxWindowDays,
                CalendarMaxEventDurationDays: SystemSettingsDefaults.CalendarMaxEventDurationDays,
                RecurrenceMaxGeneratedOccurrences: SystemSettingsDefaults.RecurrenceMaxGeneratedOccurrences,
                IsDegraded: false));
        }
    }

    private sealed class CountingApiFactory : OdysseyApiFactory
    {
        public CountingApiFactory(
            IReadOnlyCollection<string>? permissions, Odyssey.Core.Journal.IJournalLimitsLookup lookup)
            : base(permissions, ActorUserId, configureServices: services =>
            {
                var journalDatabaseName = $"journal-entry-ics-counting-{Guid.NewGuid()}";
                services.RemoveAll<DbContextOptions<OdysseyContext>>();
                services.AddDbContext<OdysseyContext>(o => o.UseInMemoryDatabase(journalDatabaseName));
                services.RemoveAll<Odyssey.Core.Journal.IJournalLimitsLookup>();
                services.AddSingleton(lookup);
            })
        {
        }
    }

    private sealed class ApiFactory : OdysseyApiFactory
    {
        private readonly string databaseName;

        // sharingStoreWith: a second caller (other claims) over the same database and file volume.
        public ApiFactory(IReadOnlyCollection<string>? permissions, ApiFactory? sharingStoreWith = null)
            : this(permissions, sharingStoreWith?.databaseName ?? $"journal-entry-ics-{Guid.NewGuid()}", sharingStoreWith)
        {
        }

        private ApiFactory(IReadOnlyCollection<string>? permissions, string databaseName, ApiFactory? sharingStoreWith)
            : base(permissions, ActorUserId, configureServices: services => IsolateDomainContext(services, databaseName),
                sharingStoreWith: sharingStoreWith)
        {
            this.databaseName = databaseName;
        }

        private static void IsolateDomainContext(IServiceCollection services, string databaseName)
        {
            services.RemoveAll<DbContextOptions<OdysseyContext>>();
            services.AddDbContext<OdysseyContext>(options => options.UseInMemoryDatabase(databaseName));
        }
    }
}
