using Odyssey.Core;
using Odyssey.Context;
using Odyssey.Core.Journal;
using Odyssey.Dtos;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Odyssey.Dtos.Journal;
using Xunit;

namespace Odyssey.Core.Tests;

/// <summary>vCard (RFC 6350) import/export for Contacts (issue #338).</summary>
public class ContactVCardServiceTests
{
    private static (ContactService Service, ContactVCardService VCard) CreateServices(OdysseyContext context) => (
        new ContactService(context, new NoopContactReferenceGuard()),
        new ContactVCardService(
            context, new ContactService(context, new NoopContactReferenceGuard()), new FakeImportExportLimitsLookup(),
            NullLogger<ContactVCardService>.Instance));

    private static NewContact Person(string first, string last) => new()
    {
        Type = ContactType.Person,
        Archived = false,
        PersonDetails = new PersonDetailsDto { FirstName = first, LastName = last },
    };

    private static NewContact Org(string legalName) => new()
    {
        Type = ContactType.Organization,
        Archived = false,
        OrganizationDetails = new OrganizationDetailsDto { LegalName = legalName },
    };

    // ---------------------------------------------------------------- Export

    [Fact]
    public async Task ExportOne_Person_EmitsExpectedProperties()
    {
        await using var context = TestContextFactory.CreateJournal();
        var (service, vCard) = CreateServices(context);
        var created = await service.Create(Person("Ada", "Lovelace"));
        await service.CreateEmail(created.ContactId, new NewEmailAddress { Label = EmailLabel.Work, Value = "ada@example.com", IsPrimary = true });

        var export = await vCard.ExportOneAsync(created.ContactId);

        Assert.NotNull(export);
        Assert.Contains("BEGIN:VCARD", export!.Content);
        Assert.Contains("VERSION:4.0", export.Content);
        Assert.Contains("KIND:individual", export.Content);
        Assert.Contains("N:Lovelace;Ada;;;", export.Content);
        Assert.Contains("FN:Ada Lovelace", export.Content);
        Assert.Contains("EMAIL;TYPE=work;PREF=1:ada@example.com", export.Content);
        Assert.Contains("END:VCARD", export.Content);
        Assert.EndsWith(".vcf", export.FileName);
    }

    [Fact]
    public async Task ExportOne_Organization_EmitsOrgAndUrl()
    {
        await using var context = TestContextFactory.CreateJournal();
        var (service, vCard) = CreateServices(context);
        var created = await service.Create(new NewContact
        {
            Type = ContactType.Organization,
            Archived = false,
            OrganizationDetails = new OrganizationDetailsDto { LegalName = "Acme Corp", Website = "https://acme.example.com" },
        });

        var export = await vCard.ExportOneAsync(created.ContactId);

        Assert.NotNull(export);
        Assert.Contains("KIND:org", export!.Content);
        Assert.Contains("ORG:Acme Corp", export.Content);
        Assert.Contains("URL:https://acme.example.com", export.Content);
        Assert.Contains($"UID:{(await service.Get(created.ContactId))!.ExternalUid}", export.Content);
    }

    [Fact]
    public async Task ExportOne_ExternalUidWithReservedCharacters_IsEscaped()
    {
        // ContactService.ResolveExternalUid rejects control characters going forward, but the
        // export must still escape ExternalUid (like every other property value) as defense-in-depth
        // against a pre-existing row that predates that guard — otherwise a raw CRLF would corrupt the
        // document's line structure and could forge a second, attacker-controlled VCARD block on
        // re-import (issue #338 review).
        await using var context = TestContextFactory.CreateJournal();
        var (service, vCard) = CreateServices(context);
        var created = await service.Create(Org("Acme"));

        var contact = await context.Contacts.FindAsync(created.ContactId);
        contact!.ExternalUid = "legit-uid\r\nEND:VCARD\r\nBEGIN:VCARD\r\nUID:forged";
        await context.SaveChangesAsync();

        var export = await vCard.ExportOneAsync(created.ContactId);

        Assert.NotNull(export);
        // Escaped: the injected CRLF/structure never becomes real physical line breaks — a naive
        // substring search for "BEGIN:VCARD" would still match the escaped text embedded inside the
        // UID value, so this counts actual physical lines instead of raw substring occurrences.
        var physicalLines = export!.Content.Split("\r\n");
        Assert.Equal(1, physicalLines.Count(line => line == "BEGIN:VCARD"));
        Assert.Contains("UID:legit-uid\\nEND:VCARD\\nBEGIN:VCARD\\nUID:forged", export.Content);
    }

    [Fact]
    public async Task ExportOne_UnknownId_ReturnsNull()
    {
        await using var context = TestContextFactory.CreateJournal();
        var (_, vCard) = CreateServices(context);

        Assert.Null(await vCard.ExportOneAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task ExportMany_NoFilters_ExportsEveryContact_WithAllFileName()
    {
        await using var context = TestContextFactory.CreateJournal();
        var (service, vCard) = CreateServices(context);
        await service.Create(Org("Acme"));
        await service.Create(Org("Globex"));

        var export = await ExportManyAsync(vCard, new ContactsQueryParams());

        Assert.Equal(2, CountOccurrences(export.Content, "BEGIN:VCARD"));
        Assert.Contains("-all-", export.FileName);
    }

    [Fact]
    public async Task ExportMany_WithSearchFilter_ExportsOnlyMatching_WithFilteredFileName()
    {
        await using var context = TestContextFactory.CreateJournal();
        var (service, vCard) = CreateServices(context);
        await service.Create(Org("Acme"));
        await service.Create(Org("Globex"));

        var export = await ExportManyAsync(vCard, new ContactsQueryParams { Search = "acme" });

        Assert.Equal(1, CountOccurrences(export.Content, "BEGIN:VCARD"));
        Assert.Contains("ORG:Acme", export.Content);
        Assert.Contains("-filtered-", export.FileName);
    }

    [Fact]
    public async Task ListAllMatching_ExceedsMaxRows_Throws()
    {
        await using var context = TestContextFactory.CreateJournal();
        var service = new ContactService(context, new NoopContactReferenceGuard());
        await service.Create(Org("A"));
        await service.Create(Org("B"));
        await service.Create(Org("C"));

        await Assert.ThrowsAsync<DomainValidationException>(() => service.ListAllMatching(new ContactsQueryParams(), maxRows: 2));
    }

    // ---------------------------------------------------------------- Import — create/update

    [Fact]
    public async Task Import_SingleEntry_CreatesContactWithParsedExternalUid()
    {
        await using var context = TestContextFactory.CreateJournal();
        var (service, vCard) = CreateServices(context);

        var vcf = Vcard(
            "UID:import-uid-1",
            "FN:Ada Lovelace",
            "N:Lovelace;Ada;;;",
            "EMAIL;TYPE=work:ada@example.com");

        var result = await ImportAsync(vCard, vcf);

        Assert.Equal(1, result.CreatedCount);
        Assert.Equal(0, result.UpdatedCount);
        Assert.Empty(result.Skipped);

        var created = Assert.Single((await service.ListAsync(new ContactsQueryParams())).Items);
        Assert.Equal("import-uid-1", (await service.Get(created.ContactId))!.ExternalUid);
    }

    [Fact]
    public async Task Import_ExportedFile_IsIdempotent_UpdatesSameRecord()
    {
        await using var context = TestContextFactory.CreateJournal();
        var (service, vCard) = CreateServices(context);
        var created = await service.Create(Person("Ada", "Lovelace"));
        await service.CreateEmail(created.ContactId, new NewEmailAddress { Label = EmailLabel.Work, Value = "ada@example.com" });

        var exported = (await vCard.ExportOneAsync(created.ContactId))!.Content;
        var result = await ImportAsync(vCard, exported);

        Assert.Equal(0, result.CreatedCount);
        Assert.Equal(1, result.UpdatedCount);

        var all = await service.ListAsync(new ContactsQueryParams());
        Assert.Single(all.Items); // no duplicate row created
    }

    [Fact]
    public async Task Import_UidMatchedUpdate_ReplacesContactsWholesale()
    {
        await using var context = TestContextFactory.CreateJournal();
        var (service, vCard) = CreateServices(context);
        var created = await service.Create(Org("Acme"));
        await service.CreateEmail(created.ContactId, new NewEmailAddress { Label = EmailLabel.Home, Value = "old@example.com" });
        var externalUid = (await service.Get(created.ContactId))!.ExternalUid;

        var vcf = Vcard(
            $"UID:{externalUid}",
            "FN:Acme",
            "ORG:Acme",
            "EMAIL;TYPE=work:new@example.com");

        var result = await ImportAsync(vCard, vcf);

        Assert.Equal(1, result.UpdatedCount);
        var emails = await service.GetEmails(created.ContactId);
        Assert.Equal("new@example.com", Assert.Single(emails!).Value);
    }

    [Fact]
    public async Task Import_UidMatchedUpdate_DoesNotTouchArchivedState()
    {
        await using var context = TestContextFactory.CreateJournal();
        var (service, vCard) = CreateServices(context);
        var created = await service.Create(Org("Acme"));
        await service.Update(created.ContactId, new NewContact
        {
            Type = ContactType.Organization, Archived = true,
            OrganizationDetails = new OrganizationDetailsDto { LegalName = "Acme" },
        });
        var externalUid = (await service.Get(created.ContactId))!.ExternalUid;

        var vcf = Vcard($"UID:{externalUid}", "FN:Acme", "ORG:Acme");
        await ImportAsync(vCard, vcf);

        Assert.NotNull((await service.Get(created.ContactId))!.Archived);
    }

    [Fact]
    public async Task Import_TwoEntriesSameUidInOneFile_FirstCreatesSecondUpdates()
    {
        await using var context = TestContextFactory.CreateJournal();
        var (service, vCard) = CreateServices(context);

        var vcf = Vcard("UID:dup-1", "FN:Acme", "ORG:Acme")
                  + Vcard("UID:dup-1", "FN:Acme Renamed", "ORG:Acme Renamed");

        var result = await ImportAsync(vCard, vcf);

        Assert.Equal(1, result.CreatedCount);
        Assert.Equal(1, result.UpdatedCount);
        var all = await service.ListAsync(new ContactsQueryParams());
        Assert.Single(all.Items);
        Assert.Equal("Acme Renamed", all.Items.Single().ResolvedDisplayName);
    }

    [Fact]
    public async Task Import_DisplayNameOverride_RoundTrips()
    {
        await using var context = TestContextFactory.CreateJournal();
        var (service, vCard) = CreateServices(context);
        var created = await service.Create(new NewContact
        {
            Type = ContactType.Person,
            DisplayName = "Ada (agent)",
            Archived = false,
            PersonDetails = new PersonDetailsDto { FirstName = "Ada", LastName = "Lovelace" },
        });

        var exported = (await vCard.ExportOneAsync(created.ContactId))!.Content;
        Assert.Contains("FN:Ada (agent)", exported);

        // Delete and reimport from scratch to confirm the override round-trips through FN.
        await service.Delete(created.ContactId);
        await ImportAsync(vCard, exported);

        var reimported = Assert.Single((await service.ListAsync(new ContactsQueryParams())).Items);
        Assert.Equal("Ada (agent)", reimported.DisplayName);
    }

    // ---------------------------------------------------------------- Import — skip reasons

    [Fact]
    public async Task Import_NoNOrOrg_SkippedWithReason()
    {
        await using var context = TestContextFactory.CreateJournal();
        var (_, vCard) = CreateServices(context);

        var result = await ImportAsync(vCard, Vcard("UID:no-type", "FN:Mystery"));

        Assert.Equal(0, result.CreatedCount);
        var group = Assert.Single(result.Skipped);
        Assert.Contains("Could not determine contact type", group.Reason);
    }

    [Fact]
    public async Task Import_PersonMissingLastName_SkippedWholeEntry()
    {
        await using var context = TestContextFactory.CreateJournal();
        var (_, vCard) = CreateServices(context);

        // N with only a first-name component (LastName ends up empty) — Odyssey requires both.
        var result = await ImportAsync(vCard, Vcard("UID:half-name", "FN:Solo", "N:;Solo;;;"));

        Assert.Equal(0, result.CreatedCount);
        Assert.Single(result.Skipped);
    }

    [Fact]
    public async Task Import_OrganizationNoteTooLong_SkippedWholeEntry()
    {
        await using var context = TestContextFactory.CreateJournal();
        var (_, vCard) = CreateServices(context);
        var longNote = new string('x', 1025);

        var result = await ImportAsync(vCard, Vcard("UID:long-note", "FN:Acme", "ORG:Acme", $"NOTE:{longNote}"));

        Assert.Equal(0, result.CreatedCount);
        Assert.Single(result.Skipped);
    }

    [Fact]
    public async Task Import_InvalidEmail_DropsJustThatEmail_NotWholeEntry()
    {
        await using var context = TestContextFactory.CreateJournal();
        var (service, vCard) = CreateServices(context);

        var result = await ImportAsync(vCard, Vcard(
            "UID:bad-email", "FN:Acme", "ORG:Acme",
            "EMAIL;TYPE=work:not-an-email",
            "EMAIL;TYPE=home:good@example.com"));

        Assert.Equal(1, result.CreatedCount);
        // The entry still succeeds (only one repeatable block was dropped), but the drop itself is now
        // reported rather than vanishing silently (issue #338 review) — an invalid email format is
        // pre-filtered by ParseEmail (returns null) rather than thrown, but it's just as real a drop.
        var skipGroup = Assert.Single(result.Skipped);
        Assert.StartsWith("Email address dropped:", skipGroup.Reason);
        Assert.Equal(1, skipGroup.Count);
        var created = Assert.Single((await service.ListAsync(new ContactsQueryParams())).Items);
        var emails = await service.GetEmails(created.ContactId);
        Assert.Equal("good@example.com", Assert.Single(emails!).Value);
    }

    [Fact]
    public async Task Import_InvalidCountryCode_DropsJustThatAddress()
    {
        await using var context = TestContextFactory.CreateJournal();
        var (service, vCard) = CreateServices(context);

        var result = await ImportAsync(vCard, Vcard(
            "UID:bad-country", "FN:Acme", "ORG:Acme",
            "ADR;TYPE=work:;;Storgata 1;Oslo;;0150;Norway"));

        Assert.Equal(1, result.CreatedCount);
        var skipGroup = Assert.Single(result.Skipped);
        Assert.StartsWith("Address dropped:", skipGroup.Reason);
        var created = Assert.Single((await service.ListAsync(new ContactsQueryParams())).Items);
        Assert.Empty((await service.GetAddresses(created.ContactId))!);
    }

    [Fact]
    public async Task Import_NonHttpWebsite_DroppedNotSkipped()
    {
        await using var context = TestContextFactory.CreateJournal();
        var (service, vCard) = CreateServices(context);

        var result = await ImportAsync(vCard, Vcard(
            "UID:bad-url", "FN:Acme", "ORG:Acme", "URL:javascript:alert(1)"));

        Assert.Equal(1, result.CreatedCount);
        Assert.Empty(result.Skipped);
        var created = Assert.Single((await service.ListAsync(new ContactsQueryParams())).Items);
        Assert.Null(created.OrganizationDetails!.Website);
    }

    [Fact]
    public async Task Import_UnrecognizedGender_DroppedNotSkipped()
    {
        await using var context = TestContextFactory.CreateJournal();
        var (service, vCard) = CreateServices(context);

        var result = await ImportAsync(vCard, Vcard(
            "UID:gender-x", "FN:Sam Rivers", "N:Rivers;Sam;;;", "GENDER:X"));

        Assert.Equal(1, result.CreatedCount);
        var created = Assert.Single((await service.ListAsync(new ContactsQueryParams())).Items);
        Assert.Null(created.PersonDetails!.Sex);
    }

    // ---------------------------------------------------------------- Reserved characters (§9, AC #17)

    [Fact]
    public async Task ReservedCharacters_RoundTripThroughExportImport()
    {
        await using var context = TestContextFactory.CreateJournal();
        var (service, vCard) = CreateServices(context);
        // Comma/semicolon/backslash are the reserved characters this app's own free-text fields can
        // actually hold — an embedded newline can't (ContactService rejects control characters in
        // Notes/LegalName/etc. for every write path, not just vCard import), so it's exercised instead
        // via UnescapeText directly in the folding test below.
        var created = await service.Create(new NewContact
        {
            Type = ContactType.Organization,
            Archived = false,
            Notes = "Notes with a comma, a semicolon; and a backslash \\ here",
            OrganizationDetails = new OrganizationDetailsDto { LegalName = "Comma, Semicolon; & Co." },
        });

        var exported = (await vCard.ExportOneAsync(created.ContactId))!.Content;
        Assert.Contains("Comma\\, Semicolon\\;", exported);

        await service.Delete(created.ContactId);
        await ImportAsync(vCard, exported);

        var reimported = Assert.Single((await service.ListAsync(new ContactsQueryParams())).Items);
        Assert.Equal("Comma, Semicolon; & Co.", reimported.OrganizationDetails!.LegalName);
        Assert.Equal("Notes with a comma, a semicolon; and a backslash \\ here", reimported.Notes);
    }

    [Fact]
    public async Task ImportedNote_WithEmbeddedNewline_IsSkippedWholeEntry()
    {
        // A foreign vCard's NOTE may legitimately contain an escaped newline (RFC 6350 allows it), but
        // Odyssey's Notes field rejects control characters on every write path — so re-importing such
        // a value is correctly a whole-entry skip, not silent corruption or a crash.
        await using var context = TestContextFactory.CreateJournal();
        var (_, vCard) = CreateServices(context);

        var result = await ImportAsync(vCard, Vcard("UID:multiline", "FN:Acme", "ORG:Acme", "NOTE:Line one\\nLine two"));

        Assert.Equal(0, result.CreatedCount);
        Assert.Single(result.Skipped);
    }

    [Fact]
    public async Task ImportedUid_WithEmbeddedNewline_SkippedWholeEntry_NotStoredVerbatim()
    {
        // A foreign vCard's UID escaped as "foo\nbar" unescapes to a real newline; storing that
        // verbatim as ExternalUid would let a later export forge a second, attacker-controlled VCARD
        // block (issue #338 review) — ContactService.ResolveExternalUid now rejects it instead.
        await using var context = TestContextFactory.CreateJournal();
        var (service, vCard) = CreateServices(context);

        var result = await ImportAsync(vCard, Vcard(
            "UID:legit-uid\\nEND:VCARD\\nBEGIN:VCARD\\nUID:forged", "FN:Acme", "ORG:Acme"));

        Assert.Equal(0, result.CreatedCount);
        var group = Assert.Single(result.Skipped);
        Assert.Contains("control characters", group.Reason);
        Assert.Empty((await service.ListAsync(new ContactsQueryParams())).Items);
    }

    [Fact]
    public async Task LongNote_IsFoldedOnExport_AndUnfoldsBackToOriginalOnImport()
    {
        await using var context = TestContextFactory.CreateJournal();
        var (service, vCard) = CreateServices(context);
        var longNote = string.Concat(Enumerable.Repeat("abcdefghij ", 20)).Trim(); // well over 75 octets
        var created = await service.Create(new NewContact
        {
            Type = ContactType.Organization,
            Archived = false,
            Notes = longNote,
            OrganizationDetails = new OrganizationDetailsDto { LegalName = "Acme" },
        });

        var exported = (await vCard.ExportOneAsync(created.ContactId))!.Content;
        // Folded lines are CRLF + a single leading space; the raw NOTE line itself must be split.
        Assert.Contains("\r\n ", exported);

        await service.Delete(created.ContactId);
        await ImportAsync(vCard, exported);

        var reimported = Assert.Single((await service.ListAsync(new ContactsQueryParams())).Items);
        Assert.Equal(longNote, reimported.Notes);
    }

    [Fact]
    public async Task ManyFoldedContinuationLines_UnfoldInLinearTime()
    {
        // Regression guard for a quadratic-time bug (issue #338 review): Unfold used to rebuild a
        // folded line via `result[^1] += raw[1..]`, which reallocates and copies the WHOLE line-so-far
        // on every continuation — O(N^2) for one property folded across N physical lines. RFC 6350
        // §3.2 lets any producer fold anywhere, so a well-formed (if unusual) file could reach this,
        // not just a malicious one. Uses an unrecognized property (never read/length-checked by any
        // mapping code) folded across 50,000 one-character continuation lines, so the only cost being
        // measured is the unfolding itself — a quadratic reconstruction would blow well past the bound
        // below; the fixed, linear one finishes in a small fraction of it.
        await using var context = TestContextFactory.CreateJournal();
        var (_, vCard) = CreateServices(context);

        const int foldCount = 300_000;
        var sb = new StringBuilder("BEGIN:VCARD\r\nVERSION:4.0\r\nUID:fold-test\r\nFN:Acme\r\nORG:Acme\r\nX-PADDING:a");
        for (var i = 0; i < foldCount; i++)
        {
            sb.Append("\r\n b");
        }

        sb.Append("\r\nEND:VCARD\r\n");

        var stopwatch = Stopwatch.StartNew();
        var result = await ImportAsync(vCard, sb.ToString());
        stopwatch.Stop();

        Assert.Equal(1, result.CreatedCount);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5),
            $"Expected linear-time line unfolding, took {stopwatch.Elapsed} for {foldCount} continuation lines.");
    }

    [Fact]
    public async Task ManyEmailsInOneEntry_CapsAtMaxRepeatablePropertiesPerEntry()
    {
        // Regression guard for the uncapped-properties-per-entry finding (issue #338 review):
        // MaxVCardEntries only bounds the number of top-level VCARD blocks, not repeatable properties
        // within one, and CreateEmail re-queries every sibling created so far — so N emails in a single
        // entry cost O(N^2), not O(N). Feeds well more than the per-entry cap (200) and asserts only
        // the cap's worth are actually created, with the excess reported once (not once per dropped
        // email, which would otherwise balloon the result payload to match).
        await using var context = TestContextFactory.CreateJournal();
        var (service, vCard) = CreateServices(context);

        const int emailCount = 1000;
        var lines = new List<string> { "UID:many-emails", "FN:Acme", "ORG:Acme" };
        lines.AddRange(Enumerable.Range(0, emailCount).Select(i => $"EMAIL;TYPE=work:person{i}@example.com"));

        var result = await ImportAsync(vCard, Vcard(lines.ToArray()));

        Assert.Equal(1, result.CreatedCount);
        var skipGroup = Assert.Single(result.Skipped);
        Assert.Contains("more than", skipGroup.Reason);
        Assert.Equal(1, skipGroup.Count); // reported once for the entry, not once per dropped email

        var created = Assert.Single((await service.ListAsync(new ContactsQueryParams())).Items);
        var emails = await service.GetEmails(created.ContactId);
        Assert.Equal(200, emails!.Count); // capped, not all 1000
    }

    // ---------------------------------------------------------------- Envelope-level rejection

    [Fact]
    public async Task Import_MalformedFile_Throws()
    {
        await using var context = TestContextFactory.CreateJournal();
        var (_, vCard) = CreateServices(context);

        await Assert.ThrowsAsync<DomainValidationException>(() => ImportAsync(vCard, "this is not a vcard"));
    }

    [Fact]
    public async Task Import_OverByteCap_Throws()
    {
        await using var context = TestContextFactory.CreateJournal();
        var (_, vCard) = CreateServices(context);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("BEGIN:VCARD\r\nEND:VCARD\r\n"));

        var overCap = new FakeImportExportLimitsLookup().ContactVCardMaxImportBytes + 1;
        await Assert.ThrowsAsync<DomainValidationException>(
            () => vCard.ImportAsync(stream, overCap, "text/vcard"));
    }

    [Fact]
    public async Task Import_LargeBatchAboveThePreviousCap_IsNotRejectedWholesale()
    {
        // Single-tenant, self-hosted deployment (issue #338 review) — the entry-count cap is effectively
        // removed (int.MaxValue) rather than an arbitrary limit, since MaxExportRows must never produce
        // a file the import endpoint then refuses to accept back. Sized just above the OLD 5000-entry
        // cap. Entries are intentionally typeless (a cheap per-entry skip, no DB write) so this verifies
        // the envelope-level entry-count check specifically, without the cost of 5001 real Create calls
        // — if the old cap still applied, ImportAsync would throw before returning any result at all.
        // See issue #343 for making the (now effectively unbounded) caps configurable for operators who
        // want a tighter one.
        await using var context = TestContextFactory.CreateJournal();
        var (_, vCard) = CreateServices(context);
        var many = string.Concat(Enumerable.Range(0, 5001).Select(i => Vcard($"UID:many-{i}")));

        var result = await ImportAsync(vCard, many);

        Assert.Equal(5001, Assert.Single(result.Skipped).Count);
    }

    [Fact]
    public void IsAcceptedContentType_AcceptsKnownTypesAndAbsent()
    {
        Assert.True(ContactVCardService.IsAcceptedContentType(null));
        Assert.True(ContactVCardService.IsAcceptedContentType(""));
        Assert.True(ContactVCardService.IsAcceptedContentType("text/vcard"));
        Assert.True(ContactVCardService.IsAcceptedContentType("application/octet-stream"));
        Assert.False(ContactVCardService.IsAcceptedContentType("application/json"));
    }

    // ---------------------------------------------------------------- helpers

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

    private static string Vcard(params string[] lines) =>
        "BEGIN:VCARD\r\nVERSION:4.0\r\n" + string.Join("\r\n", lines) + "\r\nEND:VCARD\r\n";

    private static async Task<VCardImportResult> ImportAsync(ContactVCardService vCard, string content)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
        return await vCard.ImportAsync(stream, stream.Length, "text/vcard");
    }

    // Drives the streaming export (issue #343 §5 Goal 8) into an in-memory buffer and reassembles the
    // old (FileName, Content) shape these tests were written against.
    private static async Task<(string FileName, string Content)> ExportManyAsync(
        ContactVCardService vCard, ContactsQueryParams query)
    {
        using var buffer = new MemoryStream();
        string? fileName = null;
        await vCard.ExportManyStreamingAsync(query, buffer, (name, _) => fileName = name);
        return (fileName!, Encoding.UTF8.GetString(buffer.ToArray()));
    }
}
