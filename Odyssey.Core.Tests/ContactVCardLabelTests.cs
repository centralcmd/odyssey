using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Odyssey.Context;
using Odyssey.Core.Finance;
using Odyssey.Core.Journal;
using Odyssey.Dtos;
using Odyssey.Dtos.Journal;
using Xunit;

namespace Odyssey.Core.Tests;

/// <summary>
/// The vCard label codec (issue #47 §9, §16.13–17): the <c>X-ODYSSEY-LABEL</c> extension, the
/// name-set parse and — the important one — <b>clamp, never drop</b>.
/// </summary>
public class ContactVCardLabelTests
{
    private static (ContactService Service, ContactVCardService VCard) CreateServices(OdysseyContext context) => (
        new ContactService(context, new NoopContactReferenceGuard()),
        new ContactVCardService(
            context, new ContactService(context, new NoopContactReferenceGuard()), new FakeImportExportLimitsLookup(),
            NullLogger<ContactVCardService>.Instance));

    private static NewContact Person(string first = "Ada", string last = "Lovelace") => new()
    {
        Type = ContactType.Person,
        Archived = false,
        PersonDetails = new PersonDetailsDto { FirstName = first, LastName = last },
    };

    private static NewContact Org(string legalName = "Acme") => new()
    {
        Type = ContactType.Organization,
        Archived = false,
        OrganizationDetails = new OrganizationDetailsDto { LegalName = legalName },
    };

    private static string Vcard(params string[] lines) =>
        "BEGIN:VCARD\r\nVERSION:4.0\r\n" + string.Join("\r\n", lines) + "\r\nEND:VCARD\r\n";

    private static async Task<VCardImportResult> ImportAsync(ContactVCardService vCard, string content)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
        return await vCard.ImportAsync(stream, stream.Length, "text/vcard");
    }

    // What a third-party address book does to a property it does not understand: drop the X- parameter
    // and keep the row. Only the parameter half of the content line — everything before the first
    // colon — is touched, since an ADR value is itself ';'-delimited.
    private static string StripExtensionParams(string line)
    {
        var colon = line.IndexOf(':');
        if (line.StartsWith(' ') || colon < 0)
            return line;

        var parameters = line[..colon].Split(';')
            .Where(segment => !segment.StartsWith("X-ODYSSEY-LABEL=", StringComparison.Ordinal));

        return string.Join(';', parameters) + line[colon..];
    }

    // ── Export (§16.13, §16.14) ──────────────────────────────────────────────

    /// <summary>
    /// An organization label carries the nearest standard token AND the extension, so an Odyssey →
    /// Odyssey round trip keeps the finer distinction while every other address book still reads the
    /// row.
    /// </summary>
    [Fact]
    public async Task Exporting_an_organization_label_emits_the_standard_token_and_the_extension()
    {
        await using var context = TestContextFactory.CreateJournal();
        var (service, vCard) = CreateServices(context);
        var created = await service.Create(Org());

        await service.CreatePhone(created.ContactId, new NewPhoneNumber { Label = PhoneLabel.Claims, Value = "+47 22 00 00 00" });
        await service.CreateEmail(created.ContactId, new NewEmailAddress { Label = EmailLabel.Support, Value = "support@acme.example" });
        await service.CreateAddress(created.ContactId, new NewAddress
        {
            Label = AddressLabel.Visiting, Line1 = "Storgata 55", City = "Oslo", CountryCode = "NO",
        });

        var export = await vCard.ExportOneAsync(created.ContactId);

        Assert.Contains("TEL;TYPE=voice,work;PREF=1;X-ODYSSEY-LABEL=Claims:", export!.Content);
        Assert.Contains("EMAIL;TYPE=work;PREF=1;X-ODYSSEY-LABEL=Support:", export.Content);
        Assert.Contains("ADR;TYPE=work;PREF=1;X-ODYSSEY-LABEL=Visiting:", export.Content);
    }

    /// <summary>
    /// The extension is emitted <b>only</b> where the label has no standard token of its own, so an
    /// export containing no organization labels is byte-identical to the pre-#47 output.
    /// </summary>
    [Fact]
    public async Task Exporting_only_standard_labels_emits_no_extension_parameter()
    {
        await using var context = TestContextFactory.CreateJournal();
        var (service, vCard) = CreateServices(context);
        var created = await service.Create(Person());

        await service.CreatePhone(created.ContactId, new NewPhoneNumber { Label = PhoneLabel.Mobile, Value = "+47 900 00 000", IsPrimary = true });
        await service.CreateEmail(created.ContactId, new NewEmailAddress { Label = EmailLabel.Home, Value = "ada@example.com" });
        await service.CreateAddress(created.ContactId, new NewAddress
        {
            Label = AddressLabel.Work, Line1 = "Storgata 55", City = "Oslo", CountryCode = "NO",
        });

        var export = await vCard.ExportOneAsync(created.ContactId);

        Assert.DoesNotContain("X-ODYSSEY-LABEL", export!.Content);
        Assert.Contains("TEL;TYPE=cell;PREF=1:", export.Content);
        Assert.Contains("EMAIL;TYPE=home;PREF=1:ada@example.com", export.Content);
        Assert.Contains("ADR;TYPE=work;PREF=1:", export.Content);
    }

    /// <summary><c>Postal</c> maps to <c>home</c> plus the extension — RFC 6350 dropped vCard 3.0's
    /// <c>postal</c> ADR type, so there is no standard token to use.</summary>
    [Fact]
    public async Task Postal_exports_as_home_plus_the_extension()
    {
        await using var context = TestContextFactory.CreateJournal();
        var (service, vCard) = CreateServices(context);
        var created = await service.Create(Person());

        await service.CreateAddress(created.ContactId, new NewAddress
        {
            Label = AddressLabel.Postal, Line1 = "Postboks 1", City = "Oslo", CountryCode = "NO",
        });

        var export = await vCard.ExportOneAsync(created.ContactId);

        Assert.Contains("ADR;TYPE=home;PREF=1;X-ODYSSEY-LABEL=Postal:", export!.Content);
    }

    // ── Round trip (§16.13) ──────────────────────────────────────────────────

    [Fact]
    public async Task An_exported_organization_label_reimports_as_itself()
    {
        await using var context = TestContextFactory.CreateJournal();
        var (service, vCard) = CreateServices(context);
        var source = await service.Create(Org("Acme"));
        await service.CreatePhone(source.ContactId, new NewPhoneNumber { Label = PhoneLabel.Claims, Value = "+47 22 00 00 00" });

        var export = await vCard.ExportOneAsync(source.ContactId);
        await service.Delete(source.ContactId);

        var result = await ImportAsync(vCard, export!.Content);

        Assert.Equal(1, result.CreatedCount);
        var reimported = Assert.Single((await service.ListAsync(new ContactsQueryParams())).Items);
        Assert.Equal(PhoneLabel.Claims, Assert.Single(reimported.PhoneNumbers).Label);
    }

    // ── The clamp, as a regression test (§16.15) ─────────────────────────────

    /// <summary>
    /// What Google and Apple emit for an organization: <c>TEL;TYPE=home</c>, <c>EMAIL;TYPE=work</c>,
    /// <c>ADR;TYPE=home</c>. Without the clamp the scope check throws, the per-property catch swallows
    /// it, and the row is <b>dropped</b> — so an Odyssey → third-party → Odyssey round trip through any
    /// client that strips <c>X-</c> parameters would silently delete every organization email and phone.
    /// </summary>
    [Fact]
    public async Task Importing_person_tokens_onto_an_organization_clamps_rather_than_dropping()
    {
        await using var context = TestContextFactory.CreateJournal();
        var (service, vCard) = CreateServices(context);

        var vcf = Vcard(
            "UID:clamp-me",
            "KIND:org",
            "FN:Acme",
            "ORG:Acme",
            "TEL;TYPE=home:+47 22 00 00 00",
            "EMAIL;TYPE=work:post@acme.example",
            "ADR;TYPE=home:;;Storgata 55;Oslo;;0184;NO");

        var result = await ImportAsync(vCard, vcf);

        Assert.Equal(1, result.CreatedCount);
        Assert.Empty(result.Skipped);

        var contact = Assert.Single((await service.ListAsync(new ContactsQueryParams())).Items);
        Assert.Equal(PhoneLabel.Other, Assert.Single(contact.PhoneNumbers).Label);
        Assert.Equal(EmailLabel.Other, Assert.Single(contact.EmailAddresses).Label);
        Assert.Equal(AddressLabel.Other, Assert.Single(contact.Addresses).Label);
    }

    /// <summary>The round trip that matters: export, strip every <c>X-</c> parameter (what a
    /// third-party client does), re-import. Every row survives — no label may cost a record.</summary>
    [Fact]
    public async Task A_round_trip_that_strips_the_extension_preserves_every_row()
    {
        await using var context = TestContextFactory.CreateJournal();
        var (service, vCard) = CreateServices(context);
        var created = await service.Create(Org("Acme"));

        await service.CreatePhone(created.ContactId, new NewPhoneNumber { Label = PhoneLabel.Switchboard, Value = "+47 22 00 00 00" });
        await service.CreatePhone(created.ContactId, new NewPhoneNumber { Label = PhoneLabel.Claims, Value = "+47 22 00 00 01" });
        await service.CreateEmail(created.ContactId, new NewEmailAddress { Label = EmailLabel.General, Value = "post@acme.example" });
        await service.CreateAddress(created.ContactId, new NewAddress
        {
            Label = AddressLabel.Visiting, Line1 = "Storgata 55", City = "Oslo", CountryCode = "NO",
        });

        var export = await vCard.ExportOneAsync(created.ContactId);
        var stripped = string.Join("\r\n", export!.Content.Split("\r\n").Select(StripExtensionParams));

        Assert.DoesNotContain("X-ODYSSEY-LABEL", stripped);

        var result = await ImportAsync(vCard, stripped);

        Assert.Equal(1, result.UpdatedCount);
        Assert.Empty(result.Skipped);
        var contact = (await service.Get(created.ContactId))!;
        Assert.Equal(2, contact.PhoneNumbers.Count);
        Assert.Single(contact.EmailAddresses);
        Assert.Single(contact.Addresses);
    }

    // ── The name-set parse (§16.16) ──────────────────────────────────────────

    /// <summary>
    /// <c>Enum.TryParse</c> returns <see langword="true"/> for a numeric string and for a
    /// comma-separated name list, either of which would persist a value no member names. Matching
    /// against <c>Enum.GetNames</c> rejects both, and the standard token then decides.
    /// </summary>
    [Theory]
    [InlineData("20")]           // TryParse would accept this as the ordinal
    [InlineData("Home,Work")]    // TryParse would OR these into ordinal 3
    [InlineData("NotALabel")]
    [InlineData("")]
    [InlineData("-1")]
    public async Task An_extension_label_that_names_no_member_falls_back_to_the_standard_token(string value)
    {
        await using var context = TestContextFactory.CreateJournal();
        var (service, vCard) = CreateServices(context);

        var vcf = Vcard(
            "UID:bad-ext",
            "KIND:org",
            "FN:Acme",
            "ORG:Acme",
            $"TEL;TYPE=voice,work;X-ODYSSEY-LABEL={value}:+47 22 00 00 00");

        var result = await ImportAsync(vCard, vcf);

        var contact = Assert.Single((await service.ListAsync(new ContactsQueryParams())).Items);
        var phone = Assert.Single(contact.PhoneNumbers);
        Assert.True(Enum.IsDefined(phone.Label), $"Persisted an undefined ordinal: {(int)phone.Label}.");
        Assert.Equal(PhoneLabel.Other, phone.Label);

        // The rejected string never reaches the response body or a skip reason (§10.7).
        Assert.Empty(result.Skipped);
    }

    /// <summary>A 4 KB extension value is rejected the same way, and never echoed.</summary>
    [Fact]
    public async Task An_oversized_extension_label_is_discarded_without_being_echoed()
    {
        await using var context = TestContextFactory.CreateJournal();
        var (service, vCard) = CreateServices(context);
        var huge = new string('A', 4096);

        var vcf = Vcard(
            "UID:huge-ext",
            "KIND:org",
            "FN:Acme",
            "ORG:Acme",
            $"TEL;TYPE=voice,work;X-ODYSSEY-LABEL={huge}:+47 22 00 00 00");

        var result = await ImportAsync(vCard, vcf);

        var contact = Assert.Single((await service.ListAsync(new ContactsQueryParams())).Items);
        Assert.Equal(PhoneLabel.Other, Assert.Single(contact.PhoneNumbers).Label);
        Assert.All(result.Skipped, s =>
        {
            Assert.DoesNotContain(huge, s.Reason);
            Assert.All(s.SampleNames, n => Assert.DoesNotContain(huge, n));
        });
    }

    /// <summary>The extension is matched case-insensitively and takes precedence over the token.</summary>
    [Fact]
    public async Task The_extension_label_wins_over_the_standard_token()
    {
        await using var context = TestContextFactory.CreateJournal();
        var (service, vCard) = CreateServices(context);

        var vcf = Vcard(
            "UID:ext-wins",
            "KIND:org",
            "FN:Acme",
            "ORG:Acme",
            "TEL;TYPE=voice,work;X-ODYSSEY-LABEL=emergency:+47 22 00 00 00");

        await ImportAsync(vCard, vcf);

        var contact = Assert.Single((await service.ListAsync(new ContactsQueryParams())).Items);
        Assert.Equal(PhoneLabel.Emergency, Assert.Single(contact.PhoneNumbers).Label);
    }

    /// <summary>An extension naming a member that is out of scope for the record's type still clamps —
    /// the extension decides which member, the clamp decides whether it may be written.</summary>
    [Fact]
    public async Task An_extension_label_out_of_scope_for_the_record_type_is_clamped()
    {
        await using var context = TestContextFactory.CreateJournal();
        var (service, vCard) = CreateServices(context);

        var vcf = Vcard(
            "UID:ext-out-of-scope",
            "KIND:individual",
            "FN:Ada Lovelace",
            "N:Lovelace;Ada;;;",
            "TEL;TYPE=voice,work;X-ODYSSEY-LABEL=Switchboard:+47 22 00 00 00");

        await ImportAsync(vCard, vcf);

        var contact = Assert.Single((await service.ListAsync(new ContactsQueryParams())).Items);
        Assert.Equal(PhoneLabel.Other, Assert.Single(contact.PhoneNumbers).Label);
    }

    // ── UID-matched re-import across a KIND change (§16.17) ──────────────────

    /// <summary>
    /// A UID-matched re-import replaces all three collections wholesale, so every label is re-derived
    /// through the clamp against the record's NEW type — this is also one of the two real triggers of
    /// the concurrent-type-switch race in §9.
    /// </summary>
    [Fact]
    public async Task A_uid_matched_reimport_whose_kind_changed_leaves_no_invalid_label()
    {
        await using var context = TestContextFactory.CreateJournal();
        var (service, vCard) = CreateServices(context);
        var created = await service.Create(Person());
        await service.CreatePhone(created.ContactId, new NewPhoneNumber { Label = PhoneLabel.Home, Value = "+47 22 00 00 00" });
        await service.CreateEmail(created.ContactId, new NewEmailAddress { Label = EmailLabel.Home, Value = "ada@example.com" });
        var uid = (await service.Get(created.ContactId))!.ExternalUid;

        var vcf = Vcard(
            $"UID:{uid}",
            "KIND:org",
            "FN:Lovelace Consulting",
            "ORG:Lovelace Consulting",
            "TEL;TYPE=home:+47 22 00 00 00",
            "EMAIL;TYPE=home:post@lovelace.example");

        var result = await ImportAsync(vCard, vcf);

        Assert.Equal(1, result.UpdatedCount);
        var contact = (await service.Get(created.ContactId))!;
        Assert.Equal(ContactType.Organization, contact.Type);
        Assert.True(ContactLabelScope.IsValidFor(Assert.Single(contact.PhoneNumbers).Label, contact.Type));
        Assert.True(ContactLabelScope.IsValidFor(Assert.Single(contact.EmailAddresses).Label, contact.Type));
    }
}
