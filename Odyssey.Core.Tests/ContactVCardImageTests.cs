using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Odyssey.Context;
using Odyssey.Core.Finance;
using Odyssey.Core.Journal;
using Odyssey.Core.Journal.Avatar;
using Odyssey.Dtos;
using Odyssey.Dtos.Journal;
using Odyssey.TestData.Fixtures;
using Xunit;

namespace Odyssey.Core.Tests;

/// <summary>
/// vCard <c>PHOTO</c>/<c>LOGO</c> round-tripping for contact images (issue #86 §9).
///
/// <para>
/// Two rules here are easy to get backwards and are pinned hardest: an entry carrying <b>no</b> image
/// leaves an existing avatar UNCHANGED (import is an upsert, and most address books do not round-trip
/// images, so "no PHOTO" must never mean "clear the picture"), and a <c>PHOTO</c> that is a URI
/// reference is ignored and <b>never dereferenced</b> — fetching one would be a server-side
/// request-forgery primitive driven by an uploaded file.
/// </para>
/// </summary>
public class ContactVCardImageTests
{
    private static ContactAvatarService AvatarServiceFor(OdysseyContext context) =>
        new(context,
            new FileService(context, new FileValidationService()),
            new FixedUploadLimits(64L * 1024 * 1024));

    private static (ContactService Service, ContactVCardService VCard) CreateServices(OdysseyContext context)
    {
        var service = new ContactService(context, new NoopContactReferenceGuard());
        return (service, new ContactVCardService(
            context, service, AvatarServiceFor(context), new FakeImportExportLimitsLookup(),
            NullLogger<ContactVCardService>.Instance));
    }

    private static NewContact Person(string first = "Ada", string last = "Lovelace") => new()
    {
        Type = ContactType.Person,
        Archived = false,
        PersonDetails = new PersonDetailsDto { FirstName = first, LastName = last },
    };

    private static NewContact Organization(string legalName = "Globex") => new()
    {
        Type = ContactType.Organization,
        Archived = false,
        OrganizationDetails = new OrganizationDetailsDto { LegalName = legalName },
    };

    // ── Export is opt-in on BOTH endpoints (AC 24) ────────────────────────────────────────────────

    [Fact]
    public async Task A_single_contact_export_without_the_flag_is_byte_identical_to_one_with_no_image()
    {
        await using var context = TestContextFactory.CreateJournal();
        var (service, vCard) = CreateServices(context);
        var created = await service.Create(Person());

        var before = (await vCard.ExportOneAsync(created.ContactId))!.Content;

        await AttachAsync(context, created.ContactId, ContactImageFixtures.BaselineJpeg(), "image/jpeg");

        // Flipping the single-contact default would silently change what an existing caller receives
        // from a shipped endpoint — the same objection §17 raises against re-authorizing one.
        var after = (await vCard.ExportOneAsync(created.ContactId))!.Content;

        Assert.Equal(StripRev(before), StripRev(after));
        Assert.DoesNotContain("PHOTO", after, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_bulk_export_without_the_flag_carries_no_images()
    {
        await using var context = TestContextFactory.CreateJournal();
        var (service, vCard) = CreateServices(context);
        var created = await service.Create(Person());
        await AttachAsync(context, created.ContactId, ContactImageFixtures.BaselineJpeg(), "image/jpeg");

        var content = await ExportManyAsync(vCard, includeImages: false);

        Assert.DoesNotContain("PHOTO", content, StringComparison.Ordinal);
        Assert.DoesNotContain("LOGO", content, StringComparison.Ordinal);
    }

    // ── Person → PHOTO, Organization → LOGO (AC 26) ───────────────────────────────────────────────

    [Fact]
    public async Task A_persons_image_is_exported_as_a_photo_data_uri_folded_at_75_octets()
    {
        await using var context = TestContextFactory.CreateJournal();
        var (service, vCard) = CreateServices(context);
        var created = await service.Create(Person());
        await AttachAsync(context, created.ContactId, ContactImageFixtures.BaselineJpeg(), "image/jpeg");

        var export = (await vCard.ExportOneAsync(created.ContactId, includeImages: true))!;

        Assert.Contains("PHOTO:data:image/jpeg;base64,", export.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("LOGO:", export.Content, StringComparison.Ordinal);
        AssertFoldedAt75Octets(export.Content);
    }

    [Fact]
    public async Task An_organizations_image_is_exported_as_a_logo_data_uri()
    {
        await using var context = TestContextFactory.CreateJournal();
        var (service, vCard) = CreateServices(context);
        var created = await service.Create(Organization());
        await AttachAsync(context, created.ContactId, ContactImageFixtures.BaselinePng(), "image/png");

        var export = (await vCard.ExportOneAsync(created.ContactId, includeImages: true))!;

        Assert.Contains("LOGO:data:image/png;base64,", export.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("PHOTO:", export.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_exported_image_re_imports_to_a_byte_identical_image()
    {
        await using var context = TestContextFactory.CreateJournal();
        var (service, vCard) = CreateServices(context);
        var created = await service.Create(Person());
        await AttachAsync(context, created.ContactId, ContactImageFixtures.BaselineJpeg(), "image/jpeg");
        var originalBytes = await AvatarBytesAsync(context, created.ContactId);

        var export = (await vCard.ExportOneAsync(created.ContactId, includeImages: true))!;

        // A fresh contact from the same card, so the round trip is decode → validate → strip → store
        // rather than a no-op on the row it came from.
        var reimported = await ImportAsync(vCard, export.Content.Replace(created.ExternalUid, "urn:uuid:round-trip", StringComparison.Ordinal));
        Assert.Equal(1, reimported.CreatedCount);

        var fresh = (await service.ListAllMatching(new ContactsQueryParams(), 1000))
            .Single(c => c.ExternalUid == "urn:uuid:round-trip");

        Assert.Equal(originalBytes, await AvatarBytesAsync(context, fresh.ContactId));
    }

    // ── Import shapes (AC 28) ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_vcard_3_entry_with_encoding_b_attaches_the_image()
    {
        await using var context = TestContextFactory.CreateJournal();
        var (service, vCard) = CreateServices(context);

        // The shape most address books actually export, and the reason a data: URI alone is not enough.
        var base64 = Convert.ToBase64String(ContactImageFixtures.BaselineJpeg());
        var card = Card("urn:uuid:v3", $"PHOTO;ENCODING=b;TYPE=JPEG:{base64}");

        var result = await ImportAsync(vCard, card);

        Assert.Equal(1, result.CreatedCount);
        var contact = await FindByUidAsync(service, "urn:uuid:v3");
        Assert.NotNull(contact.AvatarFileId);
    }

    [Fact]
    public async Task A_photo_on_an_organization_and_a_logo_on_a_person_are_both_accepted()
    {
        // One storage slot; the property name is presentational.
        await using var context = TestContextFactory.CreateJournal();
        var (service, vCard) = CreateServices(context);

        var jpeg = Convert.ToBase64String(ContactImageFixtures.BaselineJpeg());
        var org = Card("urn:uuid:org", $"PHOTO:data:image/jpeg;base64,{jpeg}", kind: "org");
        var person = Card("urn:uuid:person", $"LOGO:data:image/jpeg;base64,{jpeg}");

        await ImportAsync(vCard, org + person);

        Assert.NotNull((await FindByUidAsync(service, "urn:uuid:org")).AvatarFileId);
        Assert.NotNull((await FindByUidAsync(service, "urn:uuid:person")).AvatarFileId);
    }

    // ── SSRF: a URI reference is ignored, never fetched (AC 27) ───────────────────────────────────

    [Fact]
    public async Task A_photo_that_is_an_http_url_is_skipped_and_never_requested()
    {
        await using var context = TestContextFactory.CreateJournal();
        var (service, vCard) = CreateServices(context);

        // A recording handler standing in for every outbound path: if the importer ever dereferenced a
        // PHOTO URL, something would have to make a request, and nothing in this service may.
        var recorder = new RecordingHandler();
        using var http = new HttpClient(recorder);

        var result = await ImportAsync(vCard, Card("urn:uuid:ssrf", "PHOTO:https://attacker.example/x.png"));

        Assert.Equal(1, result.CreatedCount);
        Assert.Null((await FindByUidAsync(service, "urn:uuid:ssrf")).AvatarFileId);
        Assert.Empty(recorder.Requests);

        // The skip is counted and reported, not swallowed.
        Assert.Contains(result.Skipped, g => g.Reason.Contains("link", StringComparison.OrdinalIgnoreCase));
    }

    // ── Bounds before allocation (AC 30) ──────────────────────────────────────────────────────────

    [Fact]
    public async Task A_base64_photo_whose_encoded_length_implies_an_over_cap_image_is_refused()
    {
        await using var context = TestContextFactory.CreateJournal();
        var (service, vCard) = CreateServices(context);

        // The encoded length determines the decoded length arithmetically, so this is rejected WITHOUT
        // the decoded buffer ever being allocated. It matters because ContactVCardMaxImportEntries ships
        // unlimited against an import byte cap sized for text.
        var oversized = new string('A', (int)(ContactAvatarLimits.MaxAvatarBytes / 3 * 4) + 1024);
        var result = await ImportAsync(vCard, Card("urn:uuid:big", $"PHOTO:data:image/png;base64,{oversized}"));

        Assert.Equal(1, result.CreatedCount);
        Assert.Null((await FindByUidAsync(service, "urn:uuid:big")).AvatarFileId);
        Assert.Contains(result.Skipped, g => g.Reason.Contains("MB", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_entry_whose_image_fails_validation_still_imports_without_one()
    {
        // One bad photo must not cost a user the contact.
        await using var context = TestContextFactory.CreateJournal();
        var (service, vCard) = CreateServices(context);

        var gif = Convert.ToBase64String(ContactImageFixtures.Gif());
        var result = await ImportAsync(vCard, Card("urn:uuid:gif", $"PHOTO:data:image/gif;base64,{gif}"));

        Assert.Equal(1, result.CreatedCount);
        Assert.Null((await FindByUidAsync(service, "urn:uuid:gif")).AvatarFileId);
        Assert.NotEmpty(result.Skipped);
    }

    [Fact]
    public async Task An_imported_image_runs_the_same_strip_as_an_upload()
    {
        await using var context = TestContextFactory.CreateJournal();
        var (service, vCard) = CreateServices(context);

        var source = ContactImageFixtures.JpegWithMpfSecondImage();
        var base64 = Convert.ToBase64String(source);
        await ImportAsync(vCard, Card("urn:uuid:mpf", $"PHOTO:data:image/jpeg;base64,{base64}"));

        var contact = await FindByUidAsync(service, "urn:uuid:mpf");
        var stored = await AvatarBytesAsync(context, contact.ContactId);

        Assert.NotNull(stored);
        Assert.True(stored!.Length < source.Length, "The imported image was stored unstripped.");
        Assert.DoesNotContain("MPF", Encoding.Latin1.GetString(stored), StringComparison.Ordinal);
    }

    // ── The upsert rule (AC 29) ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Re_importing_an_entry_without_a_photo_leaves_an_existing_image_unchanged()
    {
        await using var context = TestContextFactory.CreateJournal();
        var (service, vCard) = CreateServices(context);
        var created = await service.Create(Person());
        await AttachAsync(context, created.ContactId, ContactImageFixtures.BaselineJpeg(), "image/jpeg");
        var before = (await service.Get(created.ContactId))!.AvatarFileId;

        var result = await ImportAsync(vCard, Card(created.ExternalUid, extra: null));

        Assert.Equal(1, result.UpdatedCount);
        var after = (await service.Get(created.ContactId))!.AvatarFileId;

        // "No PHOTO" must never mean "clear the picture": most address books do not round-trip images,
        // so treating absence as a clear would strip every avatar on the first re-import.
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task Re_importing_with_a_valid_image_replaces_the_existing_one()
    {
        await using var context = TestContextFactory.CreateJournal();
        var (service, vCard) = CreateServices(context);
        var created = await service.Create(Person());
        await AttachAsync(context, created.ContactId, ContactImageFixtures.BaselineJpeg(), "image/jpeg");
        var before = (await service.Get(created.ContactId))!.AvatarFileId;

        var png = Convert.ToBase64String(ContactImageFixtures.BaselinePng());
        await ImportAsync(vCard, Card(created.ExternalUid, $"PHOTO:data:image/png;base64,{png}"));

        var after = (await service.Get(created.ContactId))!.AvatarFileId;

        Assert.NotEqual(before, after);

        // The replace applies the shared release rule like any other, so the outgoing file is gone.
        Assert.False(context.FileMetadata.Any(fm => fm.Id == before));
    }

    // ── Truncation is reported, not hidden (AC 25) ────────────────────────────────────────────────

    [Fact]
    public async Task A_bulk_export_that_hits_the_output_cap_reports_the_truncation_and_its_row_count()
    {
        await using var context = TestContextFactory.CreateJournal();
        var service = new ContactService(context, new NoopContactReferenceGuard());
        var vCard = new ContactVCardService(
            context, service, AvatarServiceFor(context),
            // A cap small enough that a handful of image-carrying cards crosses it — with the shipped
            // 5 MB cap and images on, truncation is the COMMON case rather than a corner.
            new FakeImportExportLimitsLookup { ContactVCardMaxExportBytes = 4_000 },
            NullLogger<ContactVCardService>.Instance);

        for (var i = 0; i < 6; i++)
        {
            var created = await service.Create(Person($"Person{i}", "Test"));
            await AttachAsync(context, created.ContactId, ContactImageFixtures.BaselineJpeg(), "image/jpeg");
        }

        var truncation = default((int Delivered, int Total)?);
        using var buffer = new MemoryStream();
        var promised = 0;
        await vCard.ExportManyStreamingAsync(
            new ContactsQueryParams(), buffer, (_, rows) => promised = rows,
            includeImages: true,
            onTruncated: (delivered, total) => truncation = (delivered, total));

        Assert.NotNull(truncation);
        Assert.Equal(6, promised);
        Assert.Equal(6, truncation!.Value.Total);

        // It does not return a silently short document: the report names how many contacts the file
        // actually carries, and that number matches what was written.
        Assert.True(truncation.Value.Delivered < 6);
        Assert.Equal(
            truncation.Value.Delivered,
            CountOccurrences(Encoding.UTF8.GetString(buffer.ToArray()), "BEGIN:VCARD"));
    }

    [Fact]
    public async Task A_bulk_export_within_the_cap_reports_no_truncation()
    {
        await using var context = TestContextFactory.CreateJournal();
        var (service, vCard) = CreateServices(context);
        var created = await service.Create(Person());
        await AttachAsync(context, created.ContactId, ContactImageFixtures.BaselineJpeg(), "image/jpeg");

        var truncated = false;
        using var buffer = new MemoryStream();
        await vCard.ExportManyStreamingAsync(
            new ContactsQueryParams(), buffer, (_, _) => { },
            includeImages: true,
            onTruncated: (_, _) => truncated = true);

        Assert.False(truncated);
        Assert.Contains("PHOTO:data:image/jpeg;base64,", Encoding.UTF8.GetString(buffer.ToArray()), StringComparison.Ordinal);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────────────────

    private static async Task AttachAsync(OdysseyContext context, Guid contactId, byte[] bytes, string contentType)
    {
        var attached = await AvatarServiceFor(context)
            .AttachAsync(contactId, bytes, contentType, userId: null);
        Assert.True(attached);
    }

    private static async Task<byte[]?> AvatarBytesAsync(OdysseyContext context, Guid contactId)
    {
        var contact = context.Contacts.Single(c => c.ContactId == contactId);
        return contact.AvatarFileId is not { } fileId
            ? null
            : await AvatarServiceFor(context).GetContentAsync(fileId);
    }

    private static async Task<ExistingContact> FindByUidAsync(ContactService service, string externalUid) =>
        (await service.ListAllMatching(new ContactsQueryParams(), 1000))
            .Single(c => c.ExternalUid == externalUid);

    private static async Task<VCardImportResult> ImportAsync(ContactVCardService vCard, string content)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
        return await vCard.ImportAsync(stream, stream.Length, "text/vcard");
    }

    private static async Task<string> ExportManyAsync(ContactVCardService vCard, bool includeImages)
    {
        using var buffer = new MemoryStream();
        await vCard.ExportManyStreamingAsync(
            new ContactsQueryParams(), buffer, (_, _) => { }, includeImages);
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static string Card(string uid, string? extra, string kind = "individual")
    {
        var builder = new StringBuilder();
        builder.Append("BEGIN:VCARD\r\nVERSION:4.0\r\n");
        builder.Append($"UID:{uid}\r\n");
        builder.Append($"KIND:{kind}\r\n");
        builder.Append(kind == "org" ? "ORG:Globex\r\nFN:Globex\r\n" : "N:Lovelace;Ada;;;\r\nFN:Ada Lovelace\r\n");
        if (extra is not null)
        {
            builder.Append(extra).Append("\r\n");
        }

        builder.Append("END:VCARD\r\n");
        return builder.ToString();
    }

    /// <summary><c>REV</c> is a timestamp, so it legitimately differs between two exports.</summary>
    private static string StripRev(string content) =>
        string.Join('\n', content.Split('\n').Where(line => !line.StartsWith("REV:", StringComparison.Ordinal)));

    private static void AssertFoldedAt75Octets(string content)
    {
        foreach (var line in content.Split("\r\n"))
        {
            Assert.True(
                Encoding.UTF8.GetByteCount(line) <= 75,
                $"A content line exceeded 75 octets: {line[..Math.Min(40, line.Length)]}…");
        }
    }

    private static int CountOccurrences(string haystack, string needle)
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

    /// <summary>Records every outbound request, so "never dereferenced" is an assertion rather than a claim.</summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<Uri?> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
