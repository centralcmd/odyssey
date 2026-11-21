using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Odyssey.Context;
using Odyssey.Core.Journal;
using Odyssey.Core.Journal.Interop;
using Odyssey.Dtos;
using Odyssey.Dtos.Journal;
using Odyssey.Testing.FileStorage;
using Xunit;

namespace Odyssey.Core.Tests.Ics;

/// <summary>
/// VJOURNAL/.ics import and export rules (issue #339 and its follow-ups) at the service tier: field mapping, skips,
/// UID matching, link resolution and caps, the inline files and photos and their dedupe. The endpoints' claim gates,
/// status codes and headers stay in <c>Odyssey.Api.Tests/JournalEntryIcsApiTests</c>.
/// </summary>
public class JournalEntryIcsServiceTests : IcsServiceTestBase
{
    private const int AdminCountCap = 5;

    private static readonly byte[] NoteBytes = "Packing list: tent, stove, map."u8.ToArray();

    /// <summary>The claims a caller brings, as the controller hands them to the service.</summary>
    private sealed record Claims(
        bool FilesRead = false, bool FilesCreate = false, bool PhotosRead = false, bool PhotosCreate = false, bool ContactsRead = false);

    private static readonly Claims ReadWrite = new();
    private static readonly Claims WithFiles = new(FilesRead: true, PhotosRead: true);
    private static readonly Claims WithUploads = new(FilesRead: true, FilesCreate: true, PhotosRead: true, PhotosCreate: true);
    private static readonly Claims WithContacts = new(ContactsRead: true);
    private static readonly Claims Everything = new(true, true, true, true, true);

    // ------------------------------------------------------------------ export

    [Fact]
    public async Task Export_EmptyBoard_IsAValidVCalendar_WithNoVJournal()
    {
        var body = await ExportAsync();

        Assert.Contains("BEGIN:VCALENDAR", body);
        Assert.DoesNotContain("BEGIN:VJOURNAL", body);
    }

    [Fact]
    public async Task ExportAll_FileName_IsTimestampPrefixed_AndSingleNamesOneEntry()
    {
        var entry = await CreateAsync(NewEntry("A"));

        Assert.Matches(@"^odyssey-journal-entries-\d{8}-\d{6}Z\.ics$", await ExportFileNameAsync(new()));
        Assert.Matches(@"^odyssey-journal-entry-\d{8}-\d{6}Z\.ics$", await ExportFileNameAsync(new(), entry.JournalEntryId));
    }

    // The attachment filter narrows the set like any other, so the filename has to say so. Caught in review: the
    // filter was added to the query model without extending HasAnyFilter, which would have handed the user a narrowed
    // export named as the full one.
    [Theory]
    [InlineData("search")]
    [InlineData("hasPhotos=true")]
    [InlineData("hasFiles=true")]
    [InlineData("hasPhotos=false")]
    public async Task ExportFiltered_FileName_HasFilteredSegment(string filter)
    {
        await CreateAsync(NewEntry("A"));
        var query = filter switch
        {
            "search" => new JournalEntriesQueryParams { Search = "A" },
            "hasPhotos=true" => new JournalEntriesQueryParams { HasPhotos = true },
            "hasFiles=true" => new JournalEntriesQueryParams { HasFiles = true },
            _ => new JournalEntriesQueryParams { HasPhotos = false },
        };

        Assert.Matches(@"^odyssey-journal-entries-filtered-\d{8}-\d{6}Z\.ics$", await ExportFileNameAsync(query));
    }

    [Fact]
    public async Task Export_Entry_EmitsVJournal_WithAllFields()
    {
        var tagId = await SeedJournalTagAsync("Travel");
        const string cpUid = "urn:uuid:cp-acme";
        var cpId = await SeedContactAsync("Acme", cpUid);
        var imageId = await SeedPhotoAsync("beach.jpg");
        var pdfId = await SeedFileAsync("notes.pdf", "application/pdf");

        var created = await CreateAsync(NewEntry(
            title: "Hiking",
            content: "Great day, near; the fjord",
            location: "Oslo, Norway",
            entryDate: new DateTime(2026, 7, 30, 0, 0, 0, DateTimeKind.Utc),
            tagIds: [tagId],
            contactIds: [cpId],
            photoIds: [imageId],
            attachmentFileIds: [pdfId]));

        var body = await ExportAsync(claims: Everything);

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

    [Fact]
    public async Task ExportAll_IncludesArchivedByDefault_StatusFilters()
    {
        var active = await CreateAsync(NewEntry("Active one"));
        var archived = await ArchiveAsync(await CreateAsync(NewEntry("Archived one")));

        var all = await ExportAsync();
        Assert.Contains(active.ExternalUid, all);
        Assert.Contains(archived.ExternalUid, all);
        Assert.Contains("STATUS:CANCELLED", all);

        var onlyActive = await ExportAsync(new() { Status = ArchivalStatus.Active });
        Assert.Contains(active.ExternalUid, onlyActive);
        Assert.DoesNotContain(archived.ExternalUid, onlyActive);

        var onlyArchived = await ExportAsync(new() { Status = ArchivalStatus.Archived });
        Assert.DoesNotContain(active.ExternalUid, onlyArchived);
        Assert.Contains(archived.ExternalUid, onlyArchived);
    }

    [Fact]
    public async Task ExportSingle_EmitsOnlyThatEntry_AndAnUnknownIdSummarizesToNull()
    {
        var keep = await CreateAsync(NewEntry("Keep"));
        await CreateAsync(NewEntry("Drop"));

        var body = await ExportAsync(onlyId: keep.JournalEntryId);

        Assert.Equal(1, Count(body, "BEGIN:VJOURNAL"));
        Assert.Contains(keep.ExternalUid, body);
        Assert.Null(await SummarizeAsync(onlyId: Guid.NewGuid()));
    }

    [Fact]
    public async Task Export_WithoutContactsRead_OmitsContactProperty()
    {
        var cpId = await SeedContactAsync("Acme", "urn:uuid:cp-1");
        await CreateAsync(NewEntry("Linked", contactIds: [cpId]));

        Assert.DoesNotContain("X-ODYSSEY-CONTACT", await ExportAsync());
    }

    [Fact]
    public async Task ExportFiltered_BySearchAndTag_ReturnsMatchingSet()
    {
        var tagId = await SeedJournalTagAsync("Work");
        var match = await CreateAsync(NewEntry("Quarterly review", tagIds: [tagId]));
        await CreateAsync(NewEntry("Grocery run"));

        var body = await ExportAsync(new() { Search = "Quarterly", TagIds = [tagId] });

        Assert.Equal(1, Count(body, "BEGIN:VJOURNAL"));
        Assert.Contains(match.ExternalUid, body);
    }

    // The export takes the SAME query model as the list, which is what lets the page's "Export filtered" describe
    // exactly the set on screen. The attachment filter is the one that had no server predicate.
    [Fact]
    public async Task ExportFiltered_ByAttachmentPresence_ReturnsMatchingSet()
    {
        var imageId = await SeedPhotoAsync("beach.jpg");
        var withPhoto = await CreateAsync(NewEntry("Trip", photoIds: [imageId]));
        await CreateAsync(NewEntry("Grocery run"));

        var body = await ExportAsync(new() { HasPhotos = true }, claims: WithFiles);

        Assert.Equal(1, Count(body, "BEGIN:VJOURNAL"));
        Assert.Contains(withPhoto.ExternalUid, body);
    }

    [Fact]
    public async Task ExportReservedCharacters_RoundTripUnchanged()
    {
        await CreateAsync(NewEntry(title: "Trip", content: "Comma, semicolon; backslash\\ and\nnewline", location: "A, B; C\\D"));

        var result = await ImportAsync(await ExportAsync());

        Assert.Equal((0, 1), (result.ImportedCount, result.UpdatedCount));
        var row = await WithContextAsync(c => c.JournalEntries.SingleAsync());
        Assert.Equal("Comma, semicolon; backslash\\ and\nnewline", row.Content);
        Assert.Equal("A, B; C\\D", row.Location);
    }

    [Fact]
    public async Task Export_OverTheEntryCap_IsRefusedWithTheMachineReadableCode()
    {
        Limits.JournalIcsMaxExportRows = AdminCountCap;
        await WithContextAsync(async context =>
        {
            var now = DateTime.UtcNow;
            context.JournalEntries.AddRange(Enumerable.Range(0, AdminCountCap + 1).Select(i => new JournalEntry
            {
                ExternalUid = $"urn:uuid:bulk-{i}",
                Title = $"Entry {i}",
                Content = "seeded",
                EntryDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                CreatedByUserId = ActorUserId,
                CreatedAt = now,
                UpdatedAt = now,
            }));
            await context.SaveChangesAsync();
        });

        var summary = await Assert.ThrowsAsync<ExportLimitExceededException>(() => SummarizeAsync());
        var export = await Assert.ThrowsAsync<ExportLimitExceededException>(() => ExportAsync());

        Assert.Equal(ExportLimitExceededException.DiscriminatorCode, summary.Code);
        Assert.Equal(ExportLimitExceededException.DiscriminatorCode, export.Code);
    }

    // ------------------------------------------------------------------ export: attached files and photos

    [Fact]
    public async Task Export_WithAttachedFile_WithoutFilesRead_IsForbidden_AndSoIsTheSummary()
    {
        var fileId = await SeedFileWithContentAsync("note.txt", "text/plain", NoteBytes);
        var entry = await CreateAsync(NewEntry("With file", attachmentFileIds: [fileId]));
        var photosOnly = new Claims(PhotosRead: true);

        await Assert.ThrowsAsync<DomainForbiddenException>(() => ExportAsync(claims: photosOnly));
        await Assert.ThrowsAsync<DomainForbiddenException>(() => SummarizeAsync(claims: photosOnly));
        await Assert.ThrowsAsync<DomainForbiddenException>(() => ExportAsync(claims: photosOnly, onlyId: entry.JournalEntryId));
        await Assert.ThrowsAsync<DomainForbiddenException>(() => SummarizeAsync(claims: photosOnly, onlyId: entry.JournalEntryId));
    }

    [Fact]
    public async Task Export_WithPhoto_WithoutPhotosRead_IsForbidden_AndSoIsTheSummary()
    {
        var photoId = await SeedPhotoAsync("beach.jpg");
        await CreateAsync(NewEntry("With photo", photoIds: [photoId]));
        var filesOnly = new Claims(FilesRead: true);

        await Assert.ThrowsAsync<DomainForbiddenException>(() => ExportAsync(claims: filesOnly));
        await Assert.ThrowsAsync<DomainForbiddenException>(() => SummarizeAsync(claims: filesOnly));
    }

    [Fact]
    public async Task Summary_CountsEntriesFilesAndPhotos_ForAllFilteredAndSingle()
    {
        var fileId = await SeedFileWithContentAsync("note.txt", "text/plain", NoteBytes);
        var photoBytes = PhotoTestData.Jpeg(PhotoTestData.NextSeed());
        var photoId = await SeedPhotoAsync("beach.jpg", photoBytes);
        var withBoth = await CreateAsync(NewEntry("Alpha", photoIds: [photoId], attachmentFileIds: [fileId]));
        await CreateAsync(NewEntry("Beta"));

        var all = await SummarizeAsync(claims: WithFiles);
        var filtered = await SummarizeAsync(new() { Search = "Beta" }, claims: WithFiles);
        var single = await SummarizeAsync(claims: WithFiles, onlyId: withBoth.JournalEntryId);

        Assert.Equal((2, 1, 1, (long)(NoteBytes.Length + photoBytes.Length)),
            (all!.EntryCount, all.AttachmentCount, all.PhotoCount, all.AttachmentBytes));
        Assert.Equal((1, 0, 0), (filtered!.EntryCount, filtered.AttachmentCount, filtered.PhotoCount));
        Assert.Equal((1, 1, 1), (single!.EntryCount, single.AttachmentCount, single.PhotoCount));
        Assert.Null(await SummarizeAsync(claims: WithFiles, onlyId: Guid.NewGuid()));
    }

    [Fact]
    public async Task Export_AllFilteredAndSingle_CarryFileAndPhotoInline_WithThePhotosTitleAndCaption()
    {
        var fileId = await SeedFileWithContentAsync("note.txt", "text/plain", NoteBytes);
        var photoBytes = PhotoTestData.Jpeg(PhotoTestData.NextSeed());
        var photoId = await SeedPhotoAsync("beach.jpg", photoBytes, "Sunset", "Over the fjord");
        var entry = await CreateAsync(NewEntry("Camping", photoIds: [photoId], attachmentFileIds: [fileId]));

        var all = Unfold(await ExportAsync(claims: WithFiles));
        var filtered = Unfold(await ExportAsync(new() { Search = "Camp" }, claims: WithFiles));
        var single = Unfold(await ExportAsync(claims: WithFiles, onlyId: entry.JournalEntryId));

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
        Limits.JournalIcsMaxExportBytes = 1024L * 1024;
        var big = new byte[1024 * 1024]; // 1 MB of content is ~1.4 MB once base64-encoded and folded
        var fileId = await SeedFileWithContentAsync("big.pdf", "application/pdf", big);
        await CreateAsync(NewEntry("Big", attachmentFileIds: [fileId]));

        var summary = await Assert.ThrowsAsync<DomainValidationException>(() => SummarizeAsync(claims: WithFiles));
        var output = new MemoryStream();
        var named = false;
        await Assert.ThrowsAsync<DomainValidationException>(() => InScopeAsync(sp =>
            sp.GetRequiredService<JournalEntryIcsService>().ExportStreamingAsync(
                new(), false, output, (_, _) => named = true, canReadFiles: true, canReadPhotos: true)));

        Assert.Contains("1 MB export limit", summary.Message);
        Assert.False(named);
        Assert.Equal(0, output.Length);
    }

    // The pre-flight check judges files and photos exactly, but entry text only as it is written: text alone over the
    // cap must end the export with an exception (the controller then aborts the connection) rather than finish short.
    [Fact]
    public async Task Export_ThatGrowsPastTheByteCapWhileStreaming_Throws_AfterTheHeadersAreNamed()
    {
        Limits.JournalIcsMaxExportBytes = 1024L * 1024;
        await WithContextAsync(async context =>
        {
            var now = DateTime.UtcNow;
            context.JournalEntries.AddRange(Enumerable.Range(0, 400).Select(i => new JournalEntry
            {
                ExternalUid = $"urn:uuid:bulk-{i}",
                Title = $"Bulk {i}",
                Content = new string('x', 4000),
                EntryDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                CreatedByUserId = ActorUserId,
                CreatedAt = now,
                UpdatedAt = now,
            }));
            await context.SaveChangesAsync();
        });

        Assert.Equal(400, (await SummarizeAsync())!.EntryCount); // nothing to judge up front
        var named = false;
        await Assert.ThrowsAsync<IcsExportSizeExceededException>(() => InScopeAsync(sp =>
            sp.GetRequiredService<JournalEntryIcsService>().ExportStreamingAsync(new(), false, new MemoryStream(), (_, _) => named = true)));
        Assert.True(named);
    }

    // ------------------------------------------------------------------ import

    [Fact]
    public async Task Import_HandAuthored_CreatesEntries_WithVerbatimUid()
    {
        var ics = Vcalendar(
            Vjournal("urn:uuid:e1", "SUMMARY:One", "DESCRIPTION:First", "DTSTART;VALUE=DATE:20260101"),
            Vjournal("external-2", "SUMMARY:Two", "DESCRIPTION:Second", "DTSTART;VALUE=DATE:20260102"));

        var result = await ImportAsync(ics);

        Assert.Equal((2, 0), (result.ImportedCount, result.UpdatedCount));
        Assert.Empty(result.Skipped);
        Assert.Contains(await WithContextAsync(c => c.JournalEntries.ToListAsync()), r => r.ExternalUid == "external-2");
    }

    [Fact]
    public async Task Import_ExportedFile_IsIdempotent_UpdatesInPlace()
    {
        await CreateAsync(NewEntry("Alpha"));
        await CreateAsync(NewEntry("Beta"));

        var result = await ImportAsync(await ExportAsync());

        Assert.Equal((0, 2), (result.ImportedCount, result.UpdatedCount));
        Assert.Equal(2, await WithContextAsync(c => c.JournalEntries.CountAsync())); // no duplicates
    }

    [Fact]
    public async Task Import_UpdateInPlace_PreservesCreatedBy_StampsUpdatedBy()
    {
        var created = await CreateAsync(NewEntry("Original"), userId: "creator");

        var result = await ImportAsync(Vcalendar(Vjournal(created.ExternalUid, "SUMMARY:Renamed", "DESCRIPTION:Edited", "DTSTART;VALUE=DATE:20260601")));

        Assert.Equal((0, 1), (result.ImportedCount, result.UpdatedCount));
        var row = await WithContextAsync(c => c.JournalEntries.SingleAsync());
        Assert.Equal("Renamed", row.Title);
        Assert.Equal("creator", row.CreatedByUserId);
        Assert.Equal(ActorUserId, row.UpdatedByUserId);
    }

    [Fact]
    public async Task Import_CancelledStatus_ArchivesOnCreate()
    {
        var created = await ImportAsync(Vcalendar(
            Vjournal("c1", "SUMMARY:Cancelled", "DESCRIPTION:x", "DTSTART;VALUE=DATE:20260101", "STATUS:CANCELLED")));

        Assert.Equal(1, created.ImportedCount);
        Assert.NotNull((await WithContextAsync(c => c.JournalEntries.SingleAsync(e => e.ExternalUid == "c1"))).Archived);
    }

    [Fact]
    public async Task Import_AbsentStatusOnUpdate_LeavesArchivedUnchanged()
    {
        // Create archived, then re-import the same UID with NO STATUS: it must stay archived (§5, AC 17).
        Assert.Equal(1, (await ImportAsync(Vcalendar(
            Vjournal("a1", "SUMMARY:Was archived", "DESCRIPTION:x", "DTSTART;VALUE=DATE:20260101", "STATUS:CANCELLED")))).ImportedCount);

        await ImportAsync(Vcalendar(Vjournal("a1", "SUMMARY:Re-imported", "DESCRIPTION:x", "DTSTART;VALUE=DATE:20260101")));

        Assert.NotNull((await WithContextAsync(c => c.JournalEntries.SingleAsync())).Archived);
    }

    [Fact]
    public async Task Import_FinalStatusOnUpdate_UnarchivesEntry()
    {
        await ImportAsync(Vcalendar(Vjournal("u1", "SUMMARY:Archived", "DESCRIPTION:x", "DTSTART;VALUE=DATE:20260101", "STATUS:CANCELLED")));

        await ImportAsync(Vcalendar(Vjournal("u1", "SUMMARY:Active again", "DESCRIPTION:x", "DTSTART;VALUE=DATE:20260101", "STATUS:FINAL")));

        Assert.Null((await WithContextAsync(c => c.JournalEntries.SingleAsync())).Archived);
    }

    [Fact]
    public async Task Import_MissingSummary_IsSkippedWithSample()
    {
        var result = await ImportAsync(Vcalendar(Vjournal("n1", "DESCRIPTION:x", "DTSTART;VALUE=DATE:20260101")));

        Assert.Equal(0, result.ImportedCount);
        var group = Assert.Single(result.Skipped);
        Assert.Contains("SUMMARY", group.Reason);
        Assert.Contains("(untitled)", group.SampleTitles);
    }

    [Fact]
    public async Task Import_MissingDescription_IsSkipped()
    {
        var result = await ImportAsync(Vcalendar(Vjournal("n2", "SUMMARY:No body", "DTSTART;VALUE=DATE:20260101")));

        Assert.Equal(0, result.ImportedCount);
        Assert.Contains("DESCRIPTION", Assert.Single(result.Skipped).Reason);
    }

    [Fact]
    public async Task Import_MissingDtstart_IsSkipped()
    {
        var result = await ImportAsync(Vcalendar(Vjournal("n3", "SUMMARY:No date", "DESCRIPTION:x")));

        Assert.Equal(0, result.ImportedCount);
        Assert.Equal("Entry date (DTSTART) is required.", Assert.Single(result.Skipped).Reason);
    }

    [Fact]
    public async Task Import_TitleTooLong_IsSkipped()
    {
        var result = await ImportAsync(Vcalendar(Vjournal("l1", $"SUMMARY:{new string('x', 201)}", "DESCRIPTION:x", "DTSTART;VALUE=DATE:20260101")));

        Assert.Equal(0, result.ImportedCount);
        Assert.Contains("Title", Assert.Single(result.Skipped).Reason);
    }

    [Fact]
    public async Task Import_ControlCharUid_IsSkipped()
    {
        // A UID with an embedded tab (a C0 control character) is rejected before any write.
        var result = await ImportAsync(Vcalendar(Vjournal("bad\tuid", "SUMMARY:Sneaky", "DESCRIPTION:x", "DTSTART;VALUE=DATE:20260101")));

        Assert.Equal(0, result.ImportedCount);
        Assert.Contains("Invalid UID", Assert.Single(result.Skipped).Reason);
        Assert.Equal(0, await WithContextAsync(c => c.JournalEntries.CountAsync()));
    }

    [Fact]
    public async Task Import_C1ControlCharUid_IsImported()
    {
        // The import runs the create/update DTO's own pattern (issue #287 L8), which bounds the C0 range and DEL only.
        const string uid = "c1\u0085uid";

        var result = await ImportAsync(Vcalendar(Vjournal(uid, "SUMMARY:C1", "DESCRIPTION:x", "DTSTART;VALUE=DATE:20260101")));

        Assert.Equal(1, result.ImportedCount);
        Assert.Empty(result.Skipped);
        Assert.Equal(uid, (await WithContextAsync(c => c.JournalEntries.SingleAsync())).ExternalUid);
    }

    [Fact]
    public async Task Import_DuplicateUidWithinFile_LastWriteWins()
    {
        var result = await ImportAsync(Vcalendar(
            Vjournal("dupe", "SUMMARY:First", "DESCRIPTION:x", "DTSTART;VALUE=DATE:20260101"),
            Vjournal("dupe", "SUMMARY:Second", "DESCRIPTION:y", "DTSTART;VALUE=DATE:20260101")));

        Assert.Equal(1, result.ImportedCount);
        Assert.Equal("Second", (await WithContextAsync(c => c.JournalEntries.SingleAsync())).Title);
    }

    [Fact]
    public async Task Import_CaseVariantUid_DoesNotMatchExisting()
    {
        await ImportAsync(Vcalendar(Vjournal("Entry-ABC", "SUMMARY:Upper", "DESCRIPTION:x", "DTSTART;VALUE=DATE:20260101")));

        var result = await ImportAsync(Vcalendar(Vjournal("entry-abc", "SUMMARY:Lower", "DESCRIPTION:x", "DTSTART;VALUE=DATE:20260101")));

        Assert.Equal((1, 0), (result.ImportedCount, result.UpdatedCount)); // distinct, not an update
    }

    [Fact]
    public async Task Import_MatchedCategory_LinksTag_UnmatchedCounted()
    {
        var tagId = await SeedJournalTagAsync("Errands");

        var result = await ImportAsync(Vcalendar(Vjournal("c1", "SUMMARY:Shop", "DESCRIPTION:x", "DTSTART;VALUE=DATE:20260101", "CATEGORIES:errands,Nonexistent")));

        Assert.Equal((1, 1), (result.ImportedCount, result.SkippedTagLinkCount));
        Assert.Equal(tagId, Assert.Single((await EntryAsync("c1")).EntryTags).JournalTagId);
    }

    [Fact]
    public async Task Import_Category_MatchesAnArchivedTag_PreferringAnActiveOne()
    {
        var archivedOnly = await SeedJournalTagAsync("Retired", archived: true);
        await SeedJournalTagAsync("Both", archived: true);
        var activeBoth = await SeedJournalTagAsync("Both");

        var result = await ImportAsync(Vcalendar(Vjournal("t1", "SUMMARY:Tagged", "DESCRIPTION:x", "DTSTART;VALUE=DATE:20260101", "CATEGORIES:retired,BOTH")));

        Assert.Equal(0, result.SkippedTagLinkCount);
        var tags = (await EntryAsync("t1")).EntryTags.Select(t => t.JournalTagId).Order().ToArray();
        Assert.Equal(new[] { archivedOnly, activeBoth }.Order().ToArray(), tags);
    }

    [Fact]
    public async Task Import_Contact_WithClaim_LinksIt()
    {
        var cpId = await SeedContactAsync("Acme", "urn:uuid:cp-acme");

        var result = await ImportAsync(Vcalendar(Vjournal("cp1", "SUMMARY:Met Acme", "DESCRIPTION:x", "DTSTART;VALUE=DATE:20260101",
            "X-ODYSSEY-CONTACT:urn:uuid:cp-acme")), WithContacts);

        Assert.Equal((1, 0), (result.ImportedCount, result.SkippedContactLinkCount));
        var row = await WithContextAsync(c => c.JournalEntries.Include(e => e.Contacts).SingleAsync());
        Assert.Equal(cpId, Assert.Single(row.Contacts).ContactId);
    }

    [Fact]
    public async Task Import_Contact_WithClaim_UnmatchedUid_IsSkippedButEntryImports()
    {
        var result = await ImportAsync(Vcalendar(Vjournal("cp2", "SUMMARY:Ghost contact", "DESCRIPTION:x", "DTSTART;VALUE=DATE:20260101",
            "X-ODYSSEY-CONTACT:urn:uuid:nobody")), WithContacts);

        Assert.Equal((1, 1), (result.ImportedCount, result.SkippedContactLinkCount));
    }

    [Fact]
    public async Task Import_Contact_WithoutClaim_IsSkippedButEntryImports()
    {
        await SeedContactAsync("Acme", "urn:uuid:cp-acme");

        var result = await ImportAsync(Vcalendar(Vjournal("cp3", "SUMMARY:Gated", "DESCRIPTION:x", "DTSTART;VALUE=DATE:20260101",
            "X-ODYSSEY-CONTACT:urn:uuid:cp-acme")));

        Assert.Equal((1, 1), (result.ImportedCount, result.SkippedContactLinkCount));
        Assert.Empty((await WithContextAsync(c => c.JournalEntries.Include(e => e.Contacts).SingleAsync())).Contacts);
    }

    [Fact]
    public async Task Import_WithoutContactsRead_NoReferences_Succeeds()
    {
        var result = await ImportAsync(Vcalendar(Vjournal("nc1", "SUMMARY:Plain", "DESCRIPTION:x", "DTSTART;VALUE=DATE:20260101")));

        Assert.Equal((1, 0), (result.ImportedCount, result.SkippedContactLinkCount));
    }

    [Fact]
    public async Task Import_UpdateWithoutClaim_LeavesExistingContactLinkUntouched()
    {
        var cpId = await SeedContactAsync("Acme", "urn:uuid:cp-acme");
        var created = await CreateAsync(NewEntry("Linked", contactIds: [cpId]));

        // Re-import matching UID with an X-ODYSSEY-CONTACT the caller may not resolve: N1 → link left untouched.
        var result = await ImportAsync(Vcalendar(Vjournal(created.ExternalUid, "SUMMARY:Linked", "DESCRIPTION:x", "DTSTART;VALUE=DATE:20260601",
            "X-ODYSSEY-CONTACT:urn:uuid:cp-acme")));

        Assert.Equal((1, 1), (result.UpdatedCount, result.SkippedContactLinkCount));
        var row = await WithContextAsync(c => c.JournalEntries.Include(e => e.Contacts).SingleAsync());
        Assert.Equal(cpId, Assert.Single(row.Contacts).ContactId);
    }

    [Fact]
    public async Task Import_UpdateWithClaim_ZeroReferences_ClearsContacts()
    {
        var cpId = await SeedContactAsync("Acme", "urn:uuid:cp-acme");
        var created = await CreateAsync(NewEntry("Linked", contactIds: [cpId]));

        await ImportAsync(Vcalendar(Vjournal(created.ExternalUid, "SUMMARY:Linked", "DESCRIPTION:x", "DTSTART;VALUE=DATE:20260601")), WithContacts);

        Assert.Empty((await WithContextAsync(c => c.JournalEntries.Include(e => e.Contacts).SingleAsync())).Contacts);
    }

    [Fact]
    public async Task Import_ResolvableAttachment_WithFilesRead_LinksIt()
    {
        var fileId = await SeedFileAsync("doc.pdf", "application/pdf");

        var result = await ImportAsync(Vcalendar(Vjournal("at1", "SUMMARY:With file", "DESCRIPTION:x", "DTSTART;VALUE=DATE:20260101",
            $"ATTACH:odyssey-file:{fileId}")), WithFiles);

        Assert.Equal((1, 0), (result.ImportedCount, result.SkippedAttachmentCount));
        Assert.Equal(fileId, Assert.Single((await EntryAsync("at1")).Attachments).FileId);
    }

    [Fact]
    public async Task Import_Attachment_WithoutFilesRead_IsSkipped()
    {
        var fileId = await SeedFileAsync("doc.pdf", "application/pdf");

        var result = await ImportAsync(Vcalendar(Vjournal("at2", "SUMMARY:Gated", "DESCRIPTION:x", "DTSTART;VALUE=DATE:20260101",
            $"ATTACH:odyssey-file:{fileId}")));

        Assert.Equal((1, 1), (result.ImportedCount, result.SkippedAttachmentCount));
        Assert.Empty((await EntryAsync("at2")).Attachments);
    }

    [Fact]
    public async Task Import_PhotoReference_WithPhotosRead_LinksIt()
    {
        var imageId = await SeedPhotoAsync("pic.jpg");

        var result = await ImportAsync(Vcalendar(Vjournal("ph1", "SUMMARY:With photo", "DESCRIPTION:x", "DTSTART;VALUE=DATE:20260101",
            $"ATTACH:odyssey-photo:{imageId}")), WithFiles);

        Assert.Equal((1, 0), (result.ImportedCount, result.SkippedPhotoCount));
        Assert.Equal(imageId, Assert.Single((await EntryAsync("ph1")).Photos).PhotoId);
    }

    /// <summary>
    /// Issue #428 §3.3: an old export names a photo by the file it wrapped before the move. That id still resolves,
    /// through the photo's LegacyFileId — whether the former file is gone or was kept because something else used it.
    /// </summary>
    [Fact]
    public async Task Import_PhotoReferenceByFormerFileId_ResolvesToThePhoto()
    {
        var (keptPhoto, keptFile) = await SeedPhotoWithFormerFileAsync("kept.jpg");
        var movedPhoto = await SeedPhotoAsync("moved.jpg");
        var movedFile = Guid.NewGuid();
        await WithContextAsync(async context =>
        {
            // What the move left behind when nothing else used the file: the old id kept, the file deleted.
            (await context.Photos.SingleAsync(p => p.PhotoId == movedPhoto)).LegacyFileId = movedFile;
            await context.SaveChangesAsync();
        });

        var result = await ImportAsync(Vcalendar(Vjournal("ph-legacy", "SUMMARY:Old export", "DESCRIPTION:x", "DTSTART;VALUE=DATE:20260101",
            $"ATTACH:odyssey-photo:{keptFile}", $"ATTACH:odyssey-photo:{movedFile}")), WithFiles);

        Assert.Equal(0, result.SkippedPhotoCount);
        Assert.Equal(new HashSet<Guid> { keptPhoto, movedPhoto }, (await EntryAsync("ph-legacy")).Photos.Select(p => p.PhotoId).ToHashSet());
    }

    /// <summary>Issue #428 §3.3: an image file no photo wraps is skipped and counted — the import never creates a photo.</summary>
    [Fact]
    public async Task Import_ImageFileNoPhotoWraps_IsSkipped_AndNoPhotoIsCreated()
    {
        var imageFile = await SeedFileAsync("receipt.jpg", "image/jpeg");

        var result = await ImportAsync(Vcalendar(Vjournal("ph-file", "SUMMARY:A file", "DESCRIPTION:x", "DTSTART;VALUE=DATE:20260101",
            $"ATTACH:odyssey-photo:{imageFile}")), WithFiles);

        Assert.Equal(1, result.SkippedPhotoCount);
        Assert.Empty(await PhotosAsync());
    }

    [Fact]
    public async Task Import_NonImagePhotoReference_IsSkippedButEntryImports()
    {
        var pdfId = await SeedFileAsync("not-a-photo.pdf", "application/pdf");

        var result = await ImportAsync(Vcalendar(Vjournal("ph2", "SUMMARY:Bad photo", "DESCRIPTION:x", "DTSTART;VALUE=DATE:20260101",
            $"ATTACH:odyssey-photo:{pdfId}")), WithFiles);

        Assert.Equal((1, 1), (result.ImportedCount, result.SkippedPhotoCount));
        Assert.Empty((await EntryAsync("ph2")).Photos);
    }

    [Fact]
    public async Task Import_OverLengthLocationOnUpdate_LeavesExistingUnchanged()
    {
        var created = await CreateAsync(NewEntry("Trip", location: "Bergen"));

        await ImportAsync(Vcalendar(Vjournal(created.ExternalUid, "SUMMARY:Trip", "DESCRIPTION:x", "DTSTART;VALUE=DATE:20260601",
            $"X-ODYSSEY-LOCATION:{new string('z', 301)}")));

        Assert.Equal("Bergen", (await WithContextAsync(c => c.JournalEntries.SingleAsync())).Location); // not cleared
    }

    [Fact]
    public async Task Import_TooManyVJournals_IsRefusedWhole_NothingImported()
    {
        Limits.JournalIcsMaxImportEntries = AdminCountCap;
        var blocks = Enumerable.Range(0, AdminCountCap + 1)
            .Select(i => Vjournal($"u{i}", $"SUMMARY:T{i}", "DESCRIPTION:x", "DTSTART;VALUE=DATE:20260101"));

        var refused = await Assert.ThrowsAsync<DomainValidationException>(() => ImportAsync(Vcalendar(blocks.ToArray())));

        Assert.Contains($"more than {AdminCountCap} journal entries", refused.Message);
        Assert.Equal(0, await WithContextAsync(c => c.JournalEntries.CountAsync()));
    }

    [Fact]
    public async Task Import_MalformedFile_IsRefused()
    {
        await Assert.ThrowsAsync<DomainValidationException>(() => ImportAsync("not an ics file"));
    }

    [Fact]
    public async Task Import_OfTheWrongContentType_IsRefused()
    {
        var ics = Vcalendar(Vjournal("x1", "SUMMARY:x", "DESCRIPTION:x", "DTSTART;VALUE=DATE:20260101"));

        await Assert.ThrowsAsync<DomainValidationException>(() => ImportAsync(ics, contentType: "application/json"));
        Assert.Equal(0, await WithContextAsync(c => c.JournalEntries.CountAsync()));
    }

    [Fact]
    public async Task Import_OnlyVEvents_IsNoOp()
    {
        var ics = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//t//EN\r\n"
            + "BEGIN:VEVENT\r\nUID:e1\r\nDTSTART:20300101T090000Z\r\nSUMMARY:Event\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";

        var result = await ImportAsync(ics);

        Assert.Equal((0, 0), (result.ImportedCount, result.UpdatedCount));
    }

    // ------------------------------------------------------------------ update: link removal / reorder

    [Fact]
    public async Task Import_UpdateNamingFewerTags_RemovesTheDroppedLink()
    {
        var alpha = await SeedJournalTagAsync("Alpha");
        var beta = await SeedJournalTagAsync("Beta");
        var created = await CreateAsync(NewEntry("Tagged", tagIds: [alpha, beta]));

        // Re-import the same UID naming only Alpha → Beta must be diffed off (full-replace, not merge).
        var result = await ImportAsync(Vcalendar(Vjournal(created.ExternalUid, "SUMMARY:Tagged", "DESCRIPTION:x", "DTSTART;VALUE=DATE:20260601", "CATEGORIES:Alpha")));

        Assert.Equal(1, result.UpdatedCount);
        Assert.Equal(alpha, Assert.Single((await EntryAsync(created.ExternalUid)).EntryTags).JournalTagId);
    }

    [Fact]
    public async Task Import_UpdateWithNoAttach_RemovesExistingAttachment()
    {
        var fileId = await SeedFileAsync("doc.pdf", "application/pdf");
        var created = await CreateAsync(NewEntry("Filed", attachmentFileIds: [fileId]));

        await ImportAsync(Vcalendar(Vjournal(created.ExternalUid, "SUMMARY:Filed", "DESCRIPTION:x", "DTSTART;VALUE=DATE:20260601")), WithFiles);

        Assert.Empty((await EntryAsync(created.ExternalUid)).Attachments);
    }

    [Fact]
    public async Task Import_UpdateReorderingPhotos_ResequencesPosition()
    {
        var imgA = await SeedPhotoAsync("a.jpg");
        var imgB = await SeedPhotoAsync("b.jpg");
        var created = await CreateAsync(NewEntry("Gallery", photoIds: [imgA, imgB]));
        Assert.Equal(imgA, created.Photos.OrderBy(p => p.Position).First().PhotoId);

        // Re-import with the two photos in reverse order → Position re-sequences (B→0, A→1) over the existing links.
        var result = await ImportAsync(Vcalendar(Vjournal(created.ExternalUid, "SUMMARY:Gallery", "DESCRIPTION:x", "DTSTART;VALUE=DATE:20260601",
            $"ATTACH:odyssey-photo:{imgB}", $"ATTACH:odyssey-photo:{imgA}")), WithFiles);
        Assert.Equal(1, result.UpdatedCount);

        var ordered = (await InScopeAsync(sp => sp.GetRequiredService<JournalEntryService>().Get(created.JournalEntryId)))!
            .Photos.OrderBy(p => p.Position).ToList();
        Assert.Equal([(imgB, 0), (imgA, 1)], ordered.Select(p => (p.PhotoId, p.Position)));
    }

    // ------------------------------------------------------------------ bounded fields / caps

    [Fact]
    public async Task Import_OverLengthUid_IsSkipped_NothingImported()
    {
        // A UID longer than the varchar(255) column would 500 the whole batch on a strict MariaDB; it must instead be a
        // clean per-block skip, preserving skip-and-continue.
        var result = await ImportAsync(Vcalendar(Vjournal(new string('u', 256), "SUMMARY:Too long", "DESCRIPTION:x", "DTSTART;VALUE=DATE:20260101")));

        Assert.Equal(0, result.ImportedCount);
        Assert.Contains("UID exceeds", Assert.Single(result.Skipped).Reason);
        Assert.Equal(0, await WithContextAsync(c => c.JournalEntries.CountAsync()));
    }

    [Fact]
    public async Task Import_DescriptionTooLong_IsSkipped()
    {
        var result = await ImportAsync(Vcalendar(Vjournal("d-long", "SUMMARY:Big", $"DESCRIPTION:{new string('d', 4097)}", "DTSTART;VALUE=DATE:20260101")));

        Assert.Equal(0, result.ImportedCount);
        Assert.Contains("Description", Assert.Single(result.Skipped).Reason);
    }

    [Fact]
    public async Task Import_EntryDateOutOfRange_IsSkipped()
    {
        var result = await ImportAsync(Vcalendar(Vjournal("y1", "SUMMARY:Ancient", "DESCRIPTION:x", "DTSTART;VALUE=DATE:18000101")));

        Assert.Equal(0, result.ImportedCount);
        Assert.Contains("1900", Assert.Single(result.Skipped).Reason);
    }

    [Fact]
    public async Task Import_TagLinks_CappedAtMaxLinksPerKind()
    {
        var names = await SeedManyJournalTagsAsync(55);

        // 55 resolvable CATEGORIES against the shipped cap of 50. The excess used to be dropped SILENTLY — issue #434
        // §9-A: the remainder is now counted AND carries a named skip reason interpolating the effective cap.
        var result = await ImportAsync(Vcalendar(Vjournal("cap1", "SUMMARY:Many tags", "DESCRIPTION:x", "DTSTART;VALUE=DATE:20260101",
            "CATEGORIES:" + string.Join(",", names))));

        Assert.Equal((1, 5), (result.ImportedCount, result.SkippedTagLinkCount));
        var capped = Assert.Single(result.Skipped, group => group.Reason.Contains("per-entry cap"));
        Assert.Equal(5, capped.Count);
        Assert.Contains("50", capped.Reason);
        Assert.Equal(50, (await EntryAsync("cap1")).EntryTags.Count);
    }

    /// <summary>
    /// A repeat of a tag already linked is a duplicate, not a link over the cap — the rule the shared
    /// <c>ImportLinks.Resolve</c> applies to both imports (issue #287 M8).
    /// </summary>
    [Fact]
    public async Task Import_ADuplicateTagAtTheCap_IsNotReportedAsCapped()
    {
        JournalLimits.Value = JournalLimits.Value with { JournalEntryMaxLinksPerKind = 2 };
        var names = await SeedManyJournalTagsAsync(2);

        var result = await ImportAsync(Vcalendar(Vjournal("dup-cap", "SUMMARY:Repeat", "DESCRIPTION:x",
            "DTSTART;VALUE=DATE:20260101", $"CATEGORIES:{names[0]},{names[1]},{names[0]}")));

        Assert.Equal((1, 0), (result.ImportedCount, result.SkippedTagLinkCount));
        Assert.DoesNotContain(result.Skipped, group => group.Reason.Contains("per-entry cap"));
    }

    /// <summary>
    /// The defect fix (issue #434 §9-A): this service enforced a hardcoded 50 while the administrator's
    /// <c>JournalEntryMaxLinksPerKind</c> was honoured on the create/update path and silently ignored here.
    /// </summary>
    [Fact]
    public async Task Import_TagLinks_HonourTheConfiguredCap_NotAHardcodedFifty()
    {
        JournalLimits.Value = JournalLimits.Value with { JournalEntryMaxLinksPerKind = 5 };
        var names = await SeedManyJournalTagsAsync(20);

        var result = await ImportAsync(Vcalendar(Vjournal("cap-cfg", "SUMMARY:Twenty tags", "DESCRIPTION:x",
            "DTSTART;VALUE=DATE:20260101", "CATEGORIES:" + string.Join(",", names))));

        Assert.Equal((1, 15), (result.ImportedCount, result.SkippedTagLinkCount));
        var capped = Assert.Single(result.Skipped, group => group.Reason.Contains("per-entry cap"));
        Assert.Equal(15, capped.Count);
        Assert.Contains("5", capped.Reason);
        Assert.Equal(5, (await EntryAsync("cap-cfg")).EntryTags.Count);
    }

    /// <summary>The create path and the import resolve the same cap from the one lookup (issue #434 §9-A).</summary>
    [Fact]
    public async Task Import_And_Create_ResolveTheSameLinkCap()
    {
        JournalLimits.Value = JournalLimits.Value with { JournalEntryMaxLinksPerKind = 3 };
        var names = await SeedManyJournalTagsAsync(10);
        var tagIds = await WithContextAsync(c => c.JournalTags.Select(tag => tag.JournalTagId).ToListAsync());

        // The create path rejects an over-cap request outright (422) and names the effective cap...
        var refused = await Assert.ThrowsAsync<DomainUnprocessableException>(() => CreateAsync(NewEntry("Direct", tagIds: [.. tagIds])));
        Assert.Contains("3", refused.Message, StringComparison.Ordinal);

        // ...and the import path caps at the same number rather than at 50.
        await ImportAsync(Vcalendar(Vjournal("same-cap", "SUMMARY:Ten tags", "DESCRIPTION:x",
            "DTSTART;VALUE=DATE:20260101", "CATEGORIES:" + string.Join(",", names))));

        Assert.Equal(3, (await EntryAsync("same-cap")).EntryTags.Count);
    }

    /// <summary>
    /// The cost of the fix, pinned: the link caps arrive on ONE snapshot awaited once per import, not once per link.
    /// </summary>
    [Fact]
    public async Task Import_AwaitsTheJournalLimitsSnapshotExactlyOnce()
    {
        var names = await SeedManyJournalTagsAsync(60);
        var before = JournalLimits.Calls;

        await ImportAsync(Vcalendar(Vjournal("count1", "SUMMARY:Many", "DESCRIPTION:x",
            "DTSTART;VALUE=DATE:20260101", "CATEGORIES:" + string.Join(",", names))));

        Assert.Equal(1, JournalLimits.Calls - before);
    }

    // ------------------------------------------------------------------ the entry's own ExternalUid (create path)

    [Fact]
    public async Task Create_WithoutExternalUid_GeneratesUrnUuid()
    {
        Assert.StartsWith("urn:uuid:", (await CreateAsync(NewEntry("Auto"))).ExternalUid);
    }

    [Fact]
    public async Task Create_WithSuppliedExternalUid_StoresVerbatim()
    {
        var request = NewEntry("Given");
        request.ExternalUid = "my-own-id";

        Assert.Equal("my-own-id", (await CreateAsync(request)).ExternalUid);
    }

    [Fact]
    public async Task Create_WithExternalUidAlreadyUsed_IsRefused()
    {
        var first = NewEntry("First");
        first.ExternalUid = "shared-id";
        await CreateAsync(first);

        var second = NewEntry("Second");
        second.ExternalUid = "shared-id";
        await Assert.ThrowsAsync<DomainValidationException>(() => CreateAsync(second));
    }

    // ------------------------------------------------------------------ round trips and inline content

    [Fact]
    public async Task RoundTrip_SameInstance_ReusesTheFileAndPhoto_NoDuplicate()
    {
        var fileId = await SeedFileWithContentAsync("note.txt", "text/plain", NoteBytes);
        var photoId = await SeedPhotoAsync("beach.jpg");
        var entry = await CreateAsync(NewEntry("Camping", photoIds: [photoId], attachmentFileIds: [fileId]));

        var result = await ImportAsync(await ExportAsync(claims: WithUploads), WithUploads);

        Assert.Equal((1, 0, 1, 0, 1), (result.UpdatedCount, result.StoredFileCount, result.ReusedFileCount, result.StoredPhotoCount, result.ReusedPhotoCount));
        Assert.Equal((0, 0), (result.SkippedAttachmentCount, result.SkippedPhotoCount));
        Assert.Single(await PhotosAsync());
        Assert.Single(await FilesAsync()); // the file itself, nothing new
        var row = await EntryAsync(entry.ExternalUid);
        Assert.Equal(fileId, Assert.Single(row.Attachments).FileId);
        Assert.Equal(photoId, Assert.Single(row.Photos).PhotoId);
    }

    [Fact]
    public async Task RoundTrip_IntoAnotherInstance_StoresTheFile_AndUploadsThePhoto_WithTitleAndCaption()
    {
        var fileId = await SeedFileWithContentAsync("note.txt", "text/plain", NoteBytes);
        var photoBytes = PhotoTestData.Jpeg(PhotoTestData.NextSeed());
        var photoId = await SeedPhotoAsync("beach.jpg", photoBytes, "Sunset", "Over the fjord");
        var entry = await CreateAsync(NewEntry("Camping", photoIds: [photoId], attachmentFileIds: [fileId]));
        var exported = await ExportAsync(claims: WithFiles);

        // A second instance of the class is a second database and file volume: another installation.
        var target = new JournalEntryIcsServiceTests();
        try
        {
            var result = await target.ImportAsync(exported, WithUploads);

            Assert.Equal((1, 1, 0, 1, 0), (result.ImportedCount, result.StoredFileCount, result.ReusedFileCount, result.StoredPhotoCount, result.ReusedPhotoCount));
            var file = Assert.Single(await target.FilesAsync());
            Assert.Equal(("note.txt", "text/plain", Sha256(NoteBytes)), (file.FileName, file.ContentType, file.Sha256Hash));
            Assert.NotNull(file.JournalStoredObjectId);
            var photo = Assert.Single(await target.PhotosAsync());
            Assert.Equal(("Sunset", "Over the fjord"), (photo.Title, photo.Caption));
            Assert.NotEqual(Guid.Empty, photo.StoredObjectId); // an owned original on the object store, not a Files-store row
            var row = await target.EntryAsync(entry.ExternalUid);
            Assert.Equal(file.Id, Assert.Single(row.Attachments).FileId);
            Assert.Equal(photo.PhotoId, Assert.Single(row.Photos).PhotoId);

            // Importing the same file again matches what the first import stored.
            var again = await target.ImportAsync(exported, WithUploads);
            Assert.Equal((1, 0, 1, 0, 1), (again.UpdatedCount, again.StoredFileCount, again.ReusedFileCount, again.StoredPhotoCount, again.ReusedPhotoCount));
            Assert.Single(await target.PhotosAsync());
            Assert.Single(await target.FilesAsync());
        }
        finally
        {
            await target.DisposeAsync();
        }
    }

    [Fact]
    public async Task Import_InlinePhoto_MatchingAStoredPhoto_IsReusedByHash_AndKeepsItsOwnTitle()
    {
        var photoBytes = PhotoTestData.Jpeg(PhotoTestData.NextSeed());
        var existingId = await SeedPhotoAsync("beach.jpg", photoBytes, "Library title");

        // No X-ODYSSEY-PHOTO-ID: a photo from elsewhere whose bytes the library already holds.
        var result = await ImportAsync(Vcalendar(Vjournal("p1", "SUMMARY:Hash match", "DESCRIPTION:x", "DTSTART;VALUE=DATE:20260101",
            InlinePhoto(photoBytes, "renamed.jpg", title: "File title"))), WithUploads);

        Assert.Equal((0, 1), (result.StoredPhotoCount, result.ReusedPhotoCount));
        var photo = Assert.Single(await PhotosAsync());
        Assert.Equal((existingId, "Library title"), (photo.PhotoId, photo.Title)); // a match is reused as-is
        Assert.Equal(existingId, Assert.Single((await EntryAsync("p1")).Photos).PhotoId);
    }

    [Fact]
    public async Task Import_SameInlinePhotoOnTwoEntries_IsUploadedOnce()
    {
        var photo = InlinePhoto(PhotoTestData.Jpeg(PhotoTestData.NextSeed()), "beach.jpg");

        var result = await ImportAsync(Vcalendar(
            Vjournal("d1", "SUMMARY:One", "DESCRIPTION:x", "DTSTART;VALUE=DATE:20260101", photo),
            Vjournal("d2", "SUMMARY:Two", "DESCRIPTION:x", "DTSTART;VALUE=DATE:20260102", photo)), WithUploads);

        Assert.Equal((2, 1, 1), (result.ImportedCount, result.StoredPhotoCount, result.ReusedPhotoCount));
        var stored = Assert.Single(await PhotosAsync());
        Assert.Equal(stored.PhotoId, Assert.Single((await EntryAsync("d1")).Photos).PhotoId);
        Assert.Equal(stored.PhotoId, Assert.Single((await EntryAsync("d2")).Photos).PhotoId);
    }

    [Fact]
    public async Task Import_InlinePhoto_ThatIsNotAnImage_IsSkipped_ButTheEntryImports()
    {
        var result = await ImportAsync(Vcalendar(Vjournal("n1", "SUMMARY:Not a photo", "DESCRIPTION:x", "DTSTART;VALUE=DATE:20260101",
            InlinePhoto(NoteBytes, "fake.jpg"))), WithUploads);

        Assert.Equal((1, 1, 0), (result.ImportedCount, result.SkippedPhotoCount, result.StoredPhotoCount));
        var group = Assert.Single(result.Skipped);
        Assert.StartsWith("Attached photo", group.Reason);
        Assert.Equal(["Not a photo"], group.SampleTitles);
        Assert.Empty(await PhotosAsync());
    }

    [Fact]
    public async Task Import_LargerThanTheImportCap_IsRefusedWhole()
    {
        Limits.JournalIcsMaxImportBytes = 1024L * 1024;
        var ics = Vcalendar(Vjournal("b1", "SUMMARY:Big", "DESCRIPTION:x", "DTSTART;VALUE=DATE:20260101",
            InlineAttach(new byte[1024 * 1024], "big.txt", "text/plain")));

        var refused = await Assert.ThrowsAsync<DomainValidationException>(() => ImportAsync(ics, WithUploads));

        Assert.Contains("1 MB limit", refused.Message);
        Assert.Empty(await FilesAsync());
        Assert.Equal(0, await WithContextAsync(c => c.JournalEntries.CountAsync()));
    }

    [Fact]
    public async Task Import_NewInlinePhoto_OverTheAdminUploadCap_IsSkipped_ButTheEntryImports()
    {
        UploadLimits.Megabytes = 1;
        // A JPEG header padded past the 1 MB cap: the size check runs before the image is decoded.
        var big = PhotoTestData.Jpeg(PhotoTestData.NextSeed()).Concat(new byte[1536 * 1024]).ToArray();

        var result = await ImportAsync(Vcalendar(Vjournal("big1", "SUMMARY:Big photo", "DESCRIPTION:x", "DTSTART;VALUE=DATE:20260101",
            InlinePhoto(big, "big.jpg"))), WithUploads);

        Assert.Equal((1, 1, 0), (result.ImportedCount, result.SkippedPhotoCount, result.StoredPhotoCount));
        var group = Assert.Single(result.Skipped);
        Assert.Contains("1 MB limit", group.Reason);
        Assert.Equal(["Big photo"], group.SampleTitles);
        Assert.Empty(await PhotosAsync());
    }

    // The two kinds are gated apart: photo claims never stand in for file claims, nor the reverse — and the refusal
    // comes before anything is written.
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Import_InlineFile_WithoutBothFileClaims_IsForbidden_NothingImported(bool filesRead, bool filesCreate)
    {
        var claims = new Claims(filesRead, filesCreate, PhotosRead: true, PhotosCreate: true);

        await Assert.ThrowsAsync<DomainForbiddenException>(() => ImportAsync(Vcalendar(Vjournal("f1", "SUMMARY:Gated", "DESCRIPTION:x",
            "DTSTART;VALUE=DATE:20260101", InlineAttach(NoteBytes, "note.txt", "text/plain"))), claims));

        Assert.Equal(0, await WithContextAsync(c => c.JournalEntries.CountAsync()));
        Assert.Empty(await FilesAsync());
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Import_InlinePhoto_WithoutBothPhotoClaims_IsForbidden_NothingImported(bool photosRead, bool photosCreate)
    {
        var claims = new Claims(FilesRead: true, FilesCreate: true, PhotosRead: photosRead, PhotosCreate: photosCreate);

        await Assert.ThrowsAsync<DomainForbiddenException>(() => ImportAsync(Vcalendar(Vjournal("g1", "SUMMARY:Gated", "DESCRIPTION:x",
            "DTSTART;VALUE=DATE:20260101", InlinePhoto(PhotoTestData.Jpeg(PhotoTestData.NextSeed()), "beach.jpg"))), claims));

        Assert.Equal(0, await WithContextAsync(c => c.JournalEntries.CountAsync()));
        Assert.Empty(await PhotosAsync());
    }

    // ------------------------------------------------------------------ helpers

    private static string Vcalendar(params string[] journals) =>
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//test//EN\r\n" + string.Concat(journals) + "END:VCALENDAR\r\n";

    private static string Vjournal(string uid, params string[] lines) =>
        "BEGIN:VJOURNAL\r\nUID:" + uid + "\r\n" + string.Join("\r\n", lines) + "\r\nEND:VJOURNAL\r\n";

    private static NewJournalEntry NewEntry(
        string title, string? content = null, string? location = null, DateTime? entryDate = null, Guid[]? tagIds = null,
        Guid[]? contactIds = null, Guid[]? photoIds = null, Guid[]? attachmentFileIds = null) => new()
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

    private Task<ExistingJournalEntry> CreateAsync(NewJournalEntry request, string userId = ActorUserId) =>
        InScopeAsync(sp => sp.GetRequiredService<JournalEntryService>().Create(request, userId, canReadPhotos: true));

    private async Task<ExistingJournalEntry> ArchiveAsync(ExistingJournalEntry entry) =>
        (await InScopeAsync(sp => sp.GetRequiredService<JournalEntryService>().Update(entry.JournalEntryId, new UpdateJournalEntry
        {
            Title = entry.Title,
            Content = entry.Content,
            EntryDate = entry.EntryDate,
            Location = entry.Location,
            Archived = true,
        }, ActorUserId, canReadPhotos: true)))!;

    private async Task<JournalEntryIcsImportResult> ImportAsync(string ics, Claims? claims = null, string contentType = "text/calendar")
    {
        claims ??= ReadWrite;
        await using var stream = Utf8(ics);
        return await InScopeAsync(sp => sp.GetRequiredService<JournalEntryIcsService>().ImportAsync(
            stream, stream.Length, contentType, ActorUserId, claims.FilesRead, claims.ContactsRead,
            canReadPhotos: claims.PhotosRead, canCreateFiles: claims.FilesCreate, canCreatePhotos: claims.PhotosCreate));
    }

    private async Task<string> ExportAsync(JournalEntriesQueryParams? query = null, Claims? claims = null, Guid? onlyId = null)
    {
        claims ??= ReadWrite;
        var output = new MemoryStream();
        await InScopeAsync(sp => sp.GetRequiredService<JournalEntryIcsService>().ExportStreamingAsync(
            query ?? new(), claims.ContactsRead, output, (_, _) => { },
            canReadFiles: claims.FilesRead, canReadPhotos: claims.PhotosRead, onlyId: onlyId));
        return System.Text.Encoding.UTF8.GetString(output.ToArray());
    }

    private async Task<string> ExportFileNameAsync(JournalEntriesQueryParams query, Guid? onlyId = null)
    {
        string? name = null;
        await InScopeAsync(sp => sp.GetRequiredService<JournalEntryIcsService>().ExportStreamingAsync(
            query, false, new MemoryStream(), (fileName, _) => name = fileName, onlyId: onlyId));
        return name!;
    }

    private Task<JournalEntryIcsExportSummary?> SummarizeAsync(JournalEntriesQueryParams? query = null, Claims? claims = null, Guid? onlyId = null)
    {
        claims ??= ReadWrite;
        return InScopeAsync(sp => sp.GetRequiredService<JournalEntryIcsService>().SummarizeExportAsync(
            query ?? new(), claims.FilesRead, claims.PhotosRead, onlyId));
    }

    private Task<JournalEntry> EntryAsync(string uid) => WithContextAsync(c => c.JournalEntries.AsNoTracking()
        .Include(e => e.Attachments).Include(e => e.Photos).Include(e => e.EntryTags)
        .SingleAsync(e => e.ExternalUid == uid));
}
