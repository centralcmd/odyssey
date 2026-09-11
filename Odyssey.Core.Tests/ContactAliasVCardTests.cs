using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Odyssey.Context;
using Odyssey.Core.Journal;
using Odyssey.Dtos;
using Odyssey.Dtos.Journal;
using Xunit;

namespace Odyssey.Core.Tests;

/// <summary>
/// vCard round-trip for aliases and the three new dates (issue #48 §9, AC 26–35).
///
/// <para>
/// The load-bearing decision under test is that an alias's free-text label travels as a
/// <b>grouped property</b>, never as a parameter: RFC 6350's quoted <c>param-value</c> excludes
/// DQUOTE, so a label containing <c>"</c> has no in-band escape, and this parser is not quote-aware.
/// A property value is TEXT, which the existing escaper round-trips losslessly.
/// </para>
/// </summary>
public class ContactAliasVCardTests
{
    private static (ContactService Service, ContactVCardService VCard) CreateServices(OdysseyContext context)
    {
        var service = new ContactService(context, new NoopContactReferenceGuard());
        return (service, new ContactVCardService(
            context, service, new FakeImportExportLimitsLookup(),
            NullLogger<ContactVCardService>.Instance));
    }

    private static NewContact Person(string first, string last) => new()
    {
        Type = ContactType.Person,
        Archived = false,
        PersonDetails = new PersonDetailsDto { FirstName = first, LastName = last },
    };

    private static string Vcard(params string[] lines) =>
        "BEGIN:VCARD\r\nVERSION:4.0\r\n" + string.Join("\r\n", lines) + "\r\nEND:VCARD\r\n";

    private static async Task<VCardImportResult> ImportAsync(ContactVCardService vCard, string content)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
        return await vCard.ImportAsync(stream, stream.Length, "text/vcard");
    }

    // AC 26: the whole shape, in one card — two groups (one labelled, one not), the extended N, and
    // DEATHDATE — and the re-import reproduces all four with the unlabelled label staying null.
    [Fact]
    public async Task RoundTrip_TwoAliasesAMiddleNameAndADeathDate()
    {
        await using var context = TestContextFactory.CreateJournal();
        var (service, vCard) = CreateServices(context);

        var person = Person("Karoline", "Hansen");
        person.PersonDetails!.MiddleName = "Marie";
        person.PersonDetails.DateOfBirth = new DateTime(1951, 9, 2);
        person.PersonDetails.DateOfDeath = new DateTime(2024, 3, 11);
        var contact = await service.Create(person);
        await service.CreateAlias(contact.ContactId, new NewContactAlias { Value = "Berg", Label = "maiden name" });
        await service.CreateAlias(contact.ContactId, new NewContactAlias { Value = "Kari" });

        var export = await vCard.ExportOneAsync(contact.ContactId);

        Assert.NotNull(export);
        Assert.Contains("N:Hansen;Karoline;Marie;;", export!.Content);
        Assert.Contains("DEATHDATE:20240311", export.Content);
        Assert.Contains("item1.NICKNAME:Berg", export.Content);
        Assert.Contains("item1.X-ODYSSEY-ALIAS-LABEL:maiden name", export.Content);
        Assert.Contains("item2.NICKNAME:Kari", export.Content);
        // The unlabelled alias emits no label property at all, rather than an empty one.
        Assert.DoesNotContain("item2.X-ODYSSEY-ALIAS-LABEL", export.Content);

        // Re-import matches on UID, so this updates the same row.
        await service.DeleteAlias(contact.ContactId, (await service.GetAliases(contact.ContactId))![0].Id);
        await service.DeleteAlias(contact.ContactId, (await service.GetAliases(contact.ContactId))![0].Id);
        await ImportAsync(vCard, export.Content);

        var reread = await service.Get(contact.ContactId);
        Assert.Equal("Marie", reread!.PersonDetails!.MiddleName);
        Assert.Equal(new DateTime(2024, 3, 11), reread.PersonDetails.DateOfDeath);
        var aliases = reread.Aliases.ToDictionary(a => a.Value, a => a.Label);
        Assert.Equal("maiden name", aliases["Berg"]);
        Assert.Null(aliases["Kari"]);
    }

    // AC 27: every character a parameter could not carry survives as a property value. The embedded
    // newline is deliberately NOT in this set — the HTTP path rejects it and import strips it, so that
    // state is unreachable and satisfying it would mean weakening a control.
    [Fact]
    public async Task RoundTrip_LabelWithQuotesSemicolonsColonsCommasAndCarets()
    {
        await using var context = TestContextFactory.CreateJournal();
        var (service, vCard) = CreateServices(context);
        var contact = await service.Create(Person("Karoline", "Hansen"));
        const string Hostile = "a\"b;c:d,e^f";
        await service.CreateAlias(contact.ContactId, new NewContactAlias { Value = "Berg", Label = Hostile });

        var export = await vCard.ExportOneAsync(contact.ContactId);
        await service.DeleteAlias(contact.ContactId, (await service.GetAliases(contact.ContactId))![0].Id);
        await ImportAsync(vCard, export!.Content);

        var stored = Assert.Single((await service.GetAliases(contact.ContactId))!);
        Assert.Equal(Hostile, stored.Label);
    }

    // AC 28: the injection the grouped-property decision exists to remove. The hostile label must come
    // back as a literal value with no injected property and no injected parameter anywhere in the card.
    [Fact]
    public async Task RoundTrip_ParameterInjectionAttempt_StaysALiteralValue()
    {
        await using var context = TestContextFactory.CreateJournal();
        var (service, vCard) = CreateServices(context);
        var contact = await service.Create(Person("Karoline", "Hansen"));
        const string Hostile = "a\";TYPE=work;X-EVIL=\"b";
        await service.CreateAlias(contact.ContactId, new NewContactAlias { Value = "Berg", Label = Hostile });

        var export = await vCard.ExportOneAsync(contact.ContactId);

        // The label line carries no parameter list of its own: everything after the first colon is the
        // value, and the escaped semicolons prove the separators were neutralised.
        var labelLine = export!.Content.Split("\r\n").Single(line => line.Contains("X-ODYSSEY-ALIAS-LABEL"));
        Assert.StartsWith("item1.X-ODYSSEY-ALIAS-LABEL:", labelLine);
        Assert.DoesNotContain("X-EVIL=", labelLine[..labelLine.IndexOf(':')]);
        Assert.DoesNotContain("\r\nX-EVIL", export.Content);

        await service.DeleteAlias(contact.ContactId, (await service.GetAliases(contact.ContactId))![0].Id);
        await ImportAsync(vCard, export.Content);
        Assert.Equal(Hostile, Assert.Single((await service.GetAliases(contact.ContactId))!).Label);
    }

    // AC 29: the comma split is conditioned on "our label is present", NOT on "the property is
    // grouped" — grouping is a general vCard mechanism, and Apple emits grouped properties routinely.
    [Theory]
    [InlineData("NICKNAME:Kari,KH")]
    [InlineData("item1.NICKNAME:Kari,KH")]
    public async Task Import_UnlabelledNicknameList_SplitsOnCommas(string nicknameLine)
    {
        await using var context = TestContextFactory.CreateJournal();
        var (service, vCard) = CreateServices(context);

        await ImportAsync(vCard, Vcard("UID:urn:uuid:split", "FN:Karoline Hansen", "N:Hansen;Karoline;;;", nicknameLine));

        var id = await service.FindIdByExternalUid("urn:uuid:split");
        var aliases = (await service.GetAliases(id!.Value))!;
        Assert.Equal(2, aliases.Count);
        Assert.All(aliases, alias => Assert.Null(alias.Label));
        Assert.Contains(aliases, alias => alias.Value == "Kari");
        Assert.Contains(aliases, alias => alias.Value == "KH");
    }

    // The converse: a LABELLED nickname is one alias, because its label describes that whole value.
    // Splitting would attach one label to several unrelated names.
    [Fact]
    public async Task Import_LabelledNickname_IsNotSplitOnCommas()
    {
        await using var context = TestContextFactory.CreateJournal();
        var (service, vCard) = CreateServices(context);

        await ImportAsync(vCard, Vcard(
            "UID:urn:uuid:nosplit", "FN:Karoline Hansen", "N:Hansen;Karoline;;;",
            "item1.NICKNAME:Hansen\\, Kari", "item1.X-ODYSSEY-ALIAS-LABEL:maiden name"));

        var id = await service.FindIdByExternalUid("urn:uuid:nosplit");
        var stored = Assert.Single((await service.GetAliases(id!.Value))!);
        Assert.Equal("Hansen, Kari", stored.Value);
        Assert.Equal("maiden name", stored.Label);
    }

    // AC 30: control characters are STRIPPED on import, not rejected. UnescapeText turns the escape
    // into a real U+000A before the value reaches ContactService, so rejecting would cost the user
    // the whole card — or persist a character that fed straight back into the next export.
    [Fact]
    public async Task Import_ControlCharacterInANickname_IsStrippedAndTheCardStillImports()
    {
        await using var context = TestContextFactory.CreateJournal();
        var (service, vCard) = CreateServices(context);

        var result = await ImportAsync(vCard, Vcard(
            "UID:urn:uuid:ctrl", "FN:Karoline Hansen", "N:Hansen;Karoline;;;", "NICKNAME:Kari\\nBob"));

        Assert.Equal(1, result.CreatedCount);
        var id = await service.FindIdByExternalUid("urn:uuid:ctrl");
        var stored = Assert.Single((await service.GetAliases(id!.Value))!);
        Assert.Equal("KariBob", stored.Value);
        Assert.DoesNotContain(stored.Value, char.IsControl);
    }

    // AC 31: the dictionary is still keyed by BARE property name, so an Apple card carrying grouped
    // properties of its own still resolves every existing lookup.
    [Fact]
    public async Task Import_AppleStyleGroupedProperties_StillResolveTheOrdinaryFields()
    {
        await using var context = TestContextFactory.CreateJournal();
        var (service, vCard) = CreateServices(context);

        await ImportAsync(vCard, Vcard(
            "UID:urn:uuid:apple", "FN:Karoline Hansen", "N:Hansen;Karoline;Marie;;",
            "NOTE:Imported from an address book.", "BDAY:19510902",
            "item1.EMAIL;TYPE=home:kari@example.com", "item1.X-ABLabel:_$!<Home>!$_"));

        var id = await service.FindIdByExternalUid("urn:uuid:apple");
        var contact = await service.Get(id!.Value);

        Assert.Equal("Karoline Hansen", contact!.ResolvedDisplayName);
        Assert.Equal("Marie", contact.PersonDetails!.MiddleName);
        Assert.Equal("Imported from an address book.", contact.Notes);
        Assert.Equal(new DateTime(1951, 9, 2), contact.PersonDetails.DateOfBirth);
        Assert.Equal("kari@example.com", Assert.Single(contact.EmailAddresses).Value);
        // The X-ABLabel is not ours, so it creates no alias — and its EMAIL sibling is not a NICKNAME.
        Assert.Empty(contact.Aliases);
    }

    // AC 32: the four grouped-parse edge cases, so a mis-attached label cannot mislabel a maiden name.
    [Fact]
    public async Task Import_UngroupedLabel_IsIgnored()
    {
        var aliases = await ImportAliases(
            "NICKNAME:Kari", "X-ODYSSEY-ALIAS-LABEL:maiden name");

        Assert.Null(Assert.Single(aliases).Label);
    }

    [Fact]
    public async Task Import_TwoLabelsInOneGroup_TakesTheFirst()
    {
        var aliases = await ImportAliases(
            "item1.NICKNAME:Kari",
            "item1.X-ODYSSEY-ALIAS-LABEL:maiden name",
            "item1.X-ODYSSEY-ALIAS-LABEL:stage name");

        Assert.Equal("maiden name", Assert.Single(aliases).Label);
    }

    [Fact]
    public async Task Import_TwoNicknamesInOneGroup_LabelsTheFirstOnly()
    {
        var aliases = await ImportAliases(
            "item1.NICKNAME:Kari",
            "item1.NICKNAME:KH",
            "item1.X-ODYSSEY-ALIAS-LABEL:maiden name");

        Assert.Equal(2, aliases.Count);
        Assert.Equal("maiden name", aliases.Single(a => a.Value == "Kari").Label);
        Assert.Null(aliases.Single(a => a.Value == "KH").Label);
    }

    [Fact]
    public async Task Import_LabelOnlyGroup_CreatesNoAlias()
    {
        var aliases = await ImportAliases("item1.X-ODYSSEY-ALIAS-LABEL:maiden name");

        Assert.Empty(aliases);
    }

    // AC 33: an import must never overwrite a label a person typed with a blank one, so a matching
    // incoming value is left entirely alone.
    [Fact]
    public async Task Import_MatchingAnExistingAlias_LeavesItsStoredLabelUnchanged()
    {
        await using var context = TestContextFactory.CreateJournal();
        var (service, vCard) = CreateServices(context);
        var person = Person("Karoline", "Hansen");
        person.ExternalUid = "urn:uuid:merge";
        var contact = await service.Create(person);
        await service.CreateAlias(contact.ContactId, new NewContactAlias { Value = "Kari", Label = "maiden name" });

        await ImportAsync(vCard, Vcard(
            "UID:urn:uuid:merge", "FN:Karoline Hansen", "N:Hansen;Karoline;;;", "NICKNAME:Kari", "NICKNAME:KH"));

        var aliases = (await service.GetAliases(contact.ContactId))!;
        Assert.Equal(2, aliases.Count);
        Assert.Equal("maiden name", aliases.Single(a => a.Value == "Kari").Label);
    }

    // An import ADDS; it never deletes, unlike the three contact-method collections, which are
    // replaced wholesale.
    [Fact]
    public async Task Import_DoesNotRemoveAnAliasTheCardOmits()
    {
        await using var context = TestContextFactory.CreateJournal();
        var (service, vCard) = CreateServices(context);
        var person = Person("Karoline", "Hansen");
        person.ExternalUid = "urn:uuid:keep";
        var contact = await service.Create(person);
        await service.CreateAlias(contact.ContactId, new NewContactAlias { Value = "Kari" });

        await ImportAsync(vCard, Vcard("UID:urn:uuid:keep", "FN:Karoline Hansen", "N:Hansen;Karoline;;;"));

        Assert.Equal("Kari", Assert.Single((await service.GetAliases(contact.ContactId))!).Value);
    }

    // AC 34: the overflow is a counted reason group, not a skip — the contact still imports. And an
    // over-long label truncates rather than being rejected, since the informative part survives.
    [Fact]
    public async Task Import_MoreNicknamesThanTheCap_KeepsTheCapAndReportsTheRest()
    {
        await using var context = TestContextFactory.CreateJournal();
        var (service, vCard) = CreateServices(context);
        var lines = new List<string> { "UID:urn:uuid:overflow", "FN:Karoline Hansen", "N:Hansen;Karoline;;;" };
        lines.AddRange(Enumerable.Range(1, 40).Select(index => $"NICKNAME:Alias {index:00}"));

        var result = await ImportAsync(vCard, Vcard([.. lines]));

        Assert.Equal(1, result.CreatedCount);
        var id = await service.FindIdByExternalUid("urn:uuid:overflow");
        Assert.Equal(ContactAliasRules.MaxPerContact, (await service.GetAliases(id!.Value))!.Count);
        Assert.Contains(result.Skipped, group => group.Reason.Contains("Aliases dropped"));
    }

    [Fact]
    public async Task Import_OverlongLabel_IsTruncatedNotRejected()
    {
        var aliases = await ImportAliases(
            "item1.NICKNAME:Kari",
            "item1.X-ODYSSEY-ALIAS-LABEL:" + new string('x', 200));

        var stored = Assert.Single(aliases);
        Assert.Equal(ContactAliasRules.MaxLabelLength, stored.Label!.Length);
    }

    // AC 35: nothing about this change touches a card that carries none of it. The writer already
    // emitted five N components, so a null middle name produces the same bytes as before.
    [Fact]
    public async Task Export_ContactWithNoAliasesAndNoMiddleName_EmitsNeitherPropertyNorAnEmptyComponent()
    {
        await using var context = TestContextFactory.CreateJournal();
        var (service, vCard) = CreateServices(context);
        var contact = await service.Create(Person("Ada", "Lovelace"));

        var export = await vCard.ExportOneAsync(contact.ContactId);

        Assert.Contains("N:Lovelace;Ada;;;", export!.Content);
        Assert.DoesNotContain("NICKNAME", export.Content);
        Assert.DoesNotContain("X-ODYSSEY-ALIAS-LABEL", export.Content);
        Assert.DoesNotContain("DEATHDATE", export.Content);
    }

    // The organization dates use the established X-ODYSSEY- naming, and an unparseable one reads as
    // ABSENT rather than failing the entry — the posture BDAY already had.
    [Fact]
    public async Task RoundTrip_OrganizationLifecycleDates()
    {
        await using var context = TestContextFactory.CreateJournal();
        var (service, vCard) = CreateServices(context);
        var contact = await service.Create(new NewContact
        {
            Type = ContactType.Organization,
            Archived = false,
            OrganizationDetails = new OrganizationDetailsDto
            {
                LegalName = "Pacific Home Insurance Co.",
                EstablishedDate = new DateTime(1974, 6, 1),
                DissolvedDate = new DateTime(2023, 6, 30),
            },
        });

        var export = await vCard.ExportOneAsync(contact.ContactId);
        Assert.Contains("X-ODYSSEY-ESTABLISHED:19740601", export!.Content);
        Assert.Contains("X-ODYSSEY-DISSOLVED:20230630", export.Content);

        await ImportAsync(vCard, export.Content);
        var reread = await service.Get(contact.ContactId);
        Assert.Equal(new DateTime(1974, 6, 1), reread!.OrganizationDetails!.EstablishedDate);
        Assert.Equal(new DateTime(2023, 6, 30), reread.OrganizationDetails.DissolvedDate);
    }

    [Fact]
    public async Task Import_UnparseableDeathDate_ReadsAsAbsentRatherThanFailingTheEntry()
    {
        await using var context = TestContextFactory.CreateJournal();
        var (service, vCard) = CreateServices(context);

        var result = await ImportAsync(vCard, Vcard(
            "UID:urn:uuid:baddate", "FN:Karoline Hansen", "N:Hansen;Karoline;;;", "DEATHDATE:not-a-date"));

        Assert.Equal(1, result.CreatedCount);
        var id = await service.FindIdByExternalUid("urn:uuid:baddate");
        Assert.Null((await service.Get(id!.Value))!.PersonDetails!.DateOfDeath);
    }

    /// <summary>Imports a one-entry card carrying <paramref name="aliasLines"/> and returns its aliases.</summary>
    private static async Task<IReadOnlyList<ExistingContactAlias>> ImportAliases(params string[] aliasLines)
    {
        await using var context = TestContextFactory.CreateJournal();
        var (service, vCard) = CreateServices(context);

        var lines = new List<string> { "UID:urn:uuid:edge", "FN:Karoline Hansen", "N:Hansen;Karoline;;;" };
        lines.AddRange(aliasLines);
        await ImportAsync(vCard, Vcard([.. lines]));

        var id = await service.FindIdByExternalUid("urn:uuid:edge");
        return (await service.GetAliases(id!.Value))!;
    }
}
