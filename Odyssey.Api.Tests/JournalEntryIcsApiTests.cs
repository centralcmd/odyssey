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

namespace Odyssey.Api.Tests;

/// <summary>VJOURNAL/.ics journal-entry import/export endpoints (issue #339).</summary>
public class JournalEntryIcsApiTests
{
    private const string ActorUserId = "journal-entry-ics-actor-id";
    private const string Path = "/api/journal-entries";
    private const string IcsPath = "/api/journal-entries/vjournal";

    // The shipped defaults for JournalIcsMaxImportEntries / JournalIcsMaxExportRows (issue #343 §6) —
    // behavior-preserving with the old hard-coded JournalEntryIcsService.MaxVJournals/MaxExportRows.
    private const int DefaultMaxImportEntries = 2000;
    private const int DefaultMaxExportRows = 2000;

    private static readonly string[] ReadOnly = [PermissionClaims.JournalRead];

    private static readonly string[] ReadWrite =
    [
        PermissionClaims.JournalRead, PermissionClaims.JournalCreate, PermissionClaims.JournalUpdate,
    ];

    private static readonly string[] ReadWriteWithFiles =
    [
        PermissionClaims.JournalRead, PermissionClaims.JournalCreate,
        PermissionClaims.JournalUpdate, PermissionClaims.FilesRead,
    ];

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
        await using var factory = new ApiFactory(ReadWriteWithContacts.Concat([PermissionClaims.FilesRead]).ToArray());
        using var client = factory.CreateClient();
        var tagId = await SeedJournalTagAsync(factory, "Travel");
        var cpUid = "urn:uuid:cp-acme";
        var cpId = await SeedContactAsync(factory, "Acme", cpUid);
        var imageId = await SeedFileAsync(factory, "beach.jpg", "image/jpeg");
        var pdfId = await SeedFileAsync(factory, "notes.pdf", "application/pdf");

        var created = await CreateAsync(client, NewEntry(
            title: "Hiking",
            content: "Great day, near; the fjord",
            location: "Oslo, Norway",
            entryDate: new DateTime(2026, 7, 30, 0, 0, 0, DateTimeKind.Utc),
            tagIds: [tagId],
            contactIds: [cpId],
            photoFileIds: [imageId],
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
        Assert.Contains($"ATTACH:odyssey-photo:{imageId}", body);
        Assert.Contains($"ATTACH:odyssey-file:{pdfId}", body);
    }

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
    public async Task Import_PhotoImage_WithFilesRead_LinksIt()
    {
        await using var factory = new ApiFactory(ReadWriteWithFiles);
        using var client = factory.CreateClient();
        var imageId = await SeedFileAsync(factory, "pic.jpg", "image/jpeg");

        var ics = Vcalendar(Vjournal("ph1", "SUMMARY:With photo", "DESCRIPTION:x", "DTSTART;VALUE=DATE:20260101",
            $"ATTACH:odyssey-photo:{imageId}"));
        var result = await ImportAsync(client, ics);

        Assert.Equal(1, result.ImportedCount);
        Assert.Equal(0, result.SkippedPhotoCount);
        using var scope = factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        Assert.Single((await ctx.JournalEntries.Include(e => e.Photos).SingleAsync()).Photos);
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

        var blocks = Enumerable.Range(0, DefaultMaxImportEntries + 1)
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
        var imgA = await SeedFileAsync(factory, "a.jpg", "image/jpeg");
        var imgB = await SeedFileAsync(factory, "b.jpg", "image/jpeg");
        var created = await CreateAsync(client, NewEntry("Gallery", photoFileIds: [imgA, imgB]));
        Assert.Equal(imgA, created.Photos.OrderBy(p => p.Position).First().FileId);

        // Re-import the same entry with the two photos in reverse order → Position must re-sequence
        // (B→0, A→1) over the existing library Photo links, not create new ones.
        var ics = Vcalendar(Vjournal(created.ExternalUid, "SUMMARY:Gallery", "DESCRIPTION:x", "DTSTART;VALUE=DATE:20260601",
            $"ATTACH:odyssey-photo:{imgB}", $"ATTACH:odyssey-photo:{imgA}"));
        var result = await ImportAsync(client, ics);
        Assert.Equal(1, result.UpdatedCount);

        var reloaded = await GetAsync(client, created.JournalEntryId);
        var ordered = reloaded.Photos.OrderBy(p => p.Position).ToList();
        Assert.Equal(2, ordered.Count);
        Assert.Equal(imgB, ordered[0].FileId);
        Assert.Equal(0, ordered[0].Position);
        Assert.Equal(imgA, ordered[1].FileId);
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
        await SeedManyEntriesAsync(factory, DefaultMaxExportRows + 1);

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
        public ApiFactory(IReadOnlyCollection<string>? permissions)
            : base(permissions, ActorUserId, configureServices: IsolateDomainContext)
        {
        }

        private static void IsolateDomainContext(IServiceCollection services)
        {
            var databaseName = $"journal-entry-ics-{Guid.NewGuid()}";
            services.RemoveAll<DbContextOptions<OdysseyContext>>();
            services.AddDbContext<OdysseyContext>(options => options.UseInMemoryDatabase(databaseName));
        }
    }
}
